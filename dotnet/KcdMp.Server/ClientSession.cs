using System.Net.Sockets;
using System.Threading.Channels;
using Serilog;
using KcdMp.Shared.Protocol;

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

    public byte Id { get; } = (byte)Interlocked.Increment(ref _idCounter);
    public string? Name { get; private set; }
    public bool IsReady => Name is not null;

    public ClientSession(TcpClient tcp, RelayServer server, ILogger? logger = null)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _server = server;
        _logger = logger ?? Log.Logger;
    }

    public async Task RunAsync()
    {
        var writeTask = WriteLoopAsync();
        try
        {
            // --- Auth (if server has password) ---
            if (!string.IsNullOrEmpty(_server.Password))
            {
                var authPacket = await _stream.ReadPacketAsync();
                if (authPacket.Type != PacketType.Auth)
                {
                    EnqueueRaw(PacketWriter.AuthResult(false, "Expected Auth packet"));
                    return;
                }
                string clientPassword = PacketReader.ReadUtf8(authPacket.Payload, 0, authPacket.Payload.Length);
                if (clientPassword != _server.Password)
                {
                    EnqueueRaw(PacketWriter.AuthResult(false, "Wrong password"));
                    await Task.Delay(100); // let packet flush before closing
                    return;
                }
                EnqueueRaw(PacketWriter.AuthResult(true, "OK"));
            }

            // --- Handshake ---
            var hsPacket = await _stream.ReadPacketAsync();
            if (hsPacket.Type != PacketType.Handshake)
            {
                _logger.Warning("[!] Client sent bad handshake type 0x{PacketType:X2}, dropping.", (byte)hsPacket.Type);
                return;
            }
            Name = PacketReader.ReadUtf8(hsPacket.Payload, 0, hsPacket.Payload.Length);

            _logger.Information("[+] '{Name}' connected (id={Id}) from {RemoteEndPoint}. Clients: active",
                Name, Id, _tcp.Client.RemoteEndPoint);

            // Send Ack with assigned ID
            EnqueueRaw(PacketWriter.Ack(Id));

            // Broadcast this client's name to all others; send existing names to this client
            _server.BroadcastName(this);
            _server.SendAllNamesTo(this);

            // --- Position receive loop ---
            // Accepts both v1 (16 bytes: x,y,z,rotZ) and v2 (17 bytes: x,y,z,rotZ,flags)
            while (true)
            {
                var packet = await _stream.ReadPacketAsync();

                if (packet.Type == PacketType.Ping && packet.Payload.Length == 8)
                {
                    EnqueueRaw(PacketWriter.Pong(packet.Payload));
                    continue;
                }

                if (packet.Type != PacketType.Position || (packet.Payload.Length != 16 && packet.Payload.Length != 17))
                    continue;

                var (x, y, z, rotZ, flags) = PacketReader.ParsePosition(packet.Payload);
                _server.Broadcast(this, x, y, z, rotZ, flags);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
        {
            // Normal disconnect
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

    private void EnqueueRaw(byte[] packet) =>
        _writeQueue.Writer.TryWrite(packet);

    private async Task WriteLoopAsync()
    {
        await foreach (var packet in _writeQueue.Reader.ReadAllAsync())
        {
            try { await _stream.WriteAsync(packet); }
            catch { break; }
        }
    }

}
