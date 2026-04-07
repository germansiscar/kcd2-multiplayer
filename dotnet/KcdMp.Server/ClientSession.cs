using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using KcdMp.Server.Identity;
using Serilog;
using KcdMp.Shared.Protocol;
using KcdMp.Server.Sessions;

namespace KcdMp.Server;

/// <summary>
/// Handles one connected client agent.
///
/// Wire protocol (all packets):
///   [type:1][payloadLen:2 LE][payload:N]
///
/// C→S  0x00  Handshake:  [nameLen:1][name:UTF-8]
/// C→S  0x01  Position:   [x:4f][y:4f][z:4f][rotZ:4f][flags:1]  (17 bytes, LE IEEE-754)
///               flags bit 0: isRiding
/// C→S  0x04  Ping:       [timestamp:8 LE int64]
/// S→C  0xFF  Ack:        [assignedId:1]
/// S→C  0x02  Ghost:      [ghostId:1][x:4f][y:4f][z:4f][rotZ:4f][flags:1]  (18 bytes)
/// S→C  0x03  Name:       [ghostId:1][name:UTF-8...]
/// S→C  0x05  Pong:       [timestamp:8 LE int64]  (echo of Ping)
/// S→C  0x06  Disconnect: [ghostId:1]
/// </summary>
public class ClientSession
{
    private static int _idCounter;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly RelayServer _server;
    private readonly Channel<byte[]> _writeQueue = Channel.CreateUnbounded<byte[]>();
    private readonly ILogger _logger;
    private int _closeRequested;

    public byte Id { get; } = (byte)Interlocked.Increment(ref _idCounter);
    public Guid SessionId { get; }
    public string? Name { get; private set; }
    public bool IsReady => Name is not null;
    public ServerSessionCloseReason CloseReason { get; private set; } = ServerSessionCloseReason.NetworkDisconnect;

    public ClientSession(TcpClient tcp, RelayServer server, Guid sessionId, ILogger? logger = null)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _server = server;
        SessionId = sessionId;
        _logger = logger ?? Log.Logger;
    }

    public async Task RunAsync()
    {
        var writeTask = WriteLoopAsync();
        try
        {
            _server.MarkSessionActivity(SessionId);

            // --- Auth (if server has password) ---
            if (!string.IsNullOrEmpty(_server.Password))
            {
                var authPacket = await _stream.ReadPacketAsync();
                _server.MarkSessionActivity(SessionId);
                if (authPacket.Type != PacketType.Auth)
                {
                    _server.MarkAuthenticationRejected(SessionId);
                    CloseReason = ServerSessionCloseReason.AuthenticationRejected;
                    EnqueueRaw(PacketWriter.AuthResult(false, "Expected Auth packet"));
                    return;
                }
                string clientPassword = PacketReader.ReadUtf8(authPacket.Payload, 0, authPacket.Payload.Length);
                if (clientPassword != _server.Password)
                {
                    _server.MarkAuthenticationRejected(SessionId);
                    CloseReason = ServerSessionCloseReason.AuthenticationRejected;
                    EnqueueRaw(PacketWriter.AuthResult(false, "Wrong password"));
                    await Task.Delay(100); // let packet flush before closing
                    return;
                }
                EnqueueRaw(PacketWriter.AuthResult(true, "OK"));
                _server.MarkAuthenticationAccepted(SessionId);
            }
            else
            {
                _server.MarkAuthenticationAccepted(SessionId);
            }

            // --- Handshake ---
            var hsPacket = await _stream.ReadPacketAsync();
            _server.MarkSessionActivity(SessionId);
            if (hsPacket.Type != PacketType.Handshake)
            {
                _logger.Warning("[!] Client sent bad handshake type 0x{PacketType:X2}, dropping.", (byte)hsPacket.Type);
                return;
            }
            var handshakePayload = PacketReader.ReadUtf8(hsPacket.Payload, 0, hsPacket.Payload.Length);
            var joinClaim = ParseSessionJoinClaim(handshakePayload);
            var (allowed, reason, identityId) = await _server.ResolveAndAssociateIdentityAsync(
                SessionId,
                joinClaim.IdentityClaim,
                joinClaim.PreferredCharacterId);
            if (!allowed)
            {
                _server.MarkAuthenticationRejected(SessionId);
                CloseReason = ServerSessionCloseReason.AuthenticationRejected;
                EnqueueRaw(PacketWriter.AuthResult(false, reason ?? "Identity rejected"));
                await Task.Delay(100);
                return;
            }

            Name = joinClaim.IdentityClaim.DisplayName;

            _logger.Information("[+] '{Name}' connected (id={Id}) from {RemoteEndPoint}. Clients: active",
                Name, Id, _tcp.Client.RemoteEndPoint);
            _logger.Information("[identity] Session {SessionId} associated to {IdentityId}", SessionId, identityId);

            // Send Ack with assigned ID
            EnqueueRaw(PacketWriter.Ack(Id));

            // Broadcast this client's name to all others; send existing names to this client
            _server.BroadcastName(this);
            _server.SendAllNamesTo(this);
            await _server.ProjectInitialStateAsync(SessionId);
            _server.NotifyPresenceSessionReady(SessionId);

            // --- Position receive loop ---
            // Accepts both v1 (16 bytes: x,y,z,rotZ) and v2 (17 bytes: x,y,z,rotZ,flags)
            while (true)
            {
                var packet = await _stream.ReadPacketAsync();
                _server.MarkSessionActivity(SessionId);

                if (packet.Type == PacketType.Ping && packet.Payload.Length == 8)
                {
                    EnqueueRaw(PacketWriter.Pong(packet.Payload));
                    continue;
                }

                switch (packet.Type)
                {
                    case PacketType.Position when packet.Payload.Length is 16 or 17:
                        var (x, y, z, rotZ, flags) = PacketReader.ParsePosition(packet.Payload);
                        _server.Broadcast(this, x, y, z, rotZ, flags);
                        break;

                    case PacketType.StateUpdate when packet.Payload.Length >= 1:
                        var (stateType, statePayload) = PacketReader.ParseStateUpdate(packet.Payload);
                        _server.BroadcastState(this, stateType, statePayload);
                        break;

                    case PacketType.Event when packet.Payload.Length >= 2:
                        var (eventType, jsonPayload) = PacketReader.ParseEvent(packet.Payload);
                        _server.BroadcastEvent(this, eventType, jsonPayload);
                        break;

                    case PacketType.StateProjectionResult when packet.Payload.Length >= 6:
                        var (projectionId, domain, status, detailsPayload) = PacketReader.ParseStateProjectionResult(packet.Payload);
                        _server.RegisterStateProjectionResult(SessionId, projectionId, domain, status, detailsPayload);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
        {
            // Normal disconnect
        }
        catch (Exception ex)
        {
            _server.EmitBackendError(SessionId, "Unhandled exception in client session loop.", ex);
        }
        finally
        {
            _writeQueue.Writer.Complete();
            await writeTask;
            _tcp.Dispose();
        }
    }

    /// <summary>Thread-safe: enqueue a Ghost packet to be sent to this client.</summary>
    public void EnqueueGhost(byte ghostId, float x, float y, float z, float rotZ, byte flags)
        => EnqueueRaw(PacketWriter.Ghost(ghostId, x, y, z, rotZ, flags));

    /// <summary>Thread-safe: enqueue a Disconnect packet (0x06) to be sent to this client.</summary>
    public void EnqueueDisconnect(byte ghostId)
        => EnqueueRaw(PacketWriter.DisconnectPacket(ghostId));

    /// <summary>Thread-safe: enqueue a Name packet (0x03) to be sent to this client.</summary>
    public void EnqueueName(byte ghostId, string name)
        => EnqueueRaw(PacketWriter.NamePacket(ghostId, name));

    /// <summary>Thread-safe: enqueue a raw packet to be sent to this client.</summary>
    public void EnqueueRaw(byte[] packet) =>
        _writeQueue.Writer.TryWrite(packet);

    public void RequestClose(ServerSessionCloseReason reason)
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0)
            return;

        CloseReason = reason;

        try { _tcp.Client.Shutdown(SocketShutdown.Both); }
        catch { }
        try { _tcp.Close(); }
        catch { }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var packet in _writeQueue.Reader.ReadAllAsync())
        {
            try { await _stream.WriteAsync(packet); }
            catch { break; }
        }
    }

    private static SessionJoinClaim ParseSessionJoinClaim(string handshakePayload)
    {
        if (string.IsNullOrWhiteSpace(handshakePayload))
            return new SessionJoinClaim(new PlayerIdentityClaim("Unknown", null, null), null);

        var trimmed = handshakePayload.Trim();
        if (!trimmed.StartsWith('{'))
            return new SessionJoinClaim(new PlayerIdentityClaim(trimmed, null, null), null);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            string displayName = ReadOptionalString(root, "displayName")
                                 ?? ReadOptionalString(root, "name")
                                 ?? "Unknown";
            string? persistentToken = ReadOptionalString(root, "persistentToken")
                                      ?? ReadOptionalString(root, "token");
            string? steamId = ReadOptionalString(root, "steamId");
            string? characterId = ReadOptionalString(root, "characterId")
                                  ?? ReadOptionalString(root, "activeCharacterId");

            var normalizedName = string.IsNullOrWhiteSpace(displayName) ? "Unknown" : displayName.Trim();
            return new SessionJoinClaim(
                new PlayerIdentityClaim(normalizedName, persistentToken?.Trim(), steamId?.Trim()),
                characterId?.Trim());
        }
        catch
        {
            return new SessionJoinClaim(new PlayerIdentityClaim(trimmed, null, null), null);
        }
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node))
            return null;

        if (node.ValueKind != JsonValueKind.String)
            return null;

        var value = node.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record SessionJoinClaim(PlayerIdentityClaim IdentityClaim, string? PreferredCharacterId);

}
