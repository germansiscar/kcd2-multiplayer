using System.Net;
using System.Net.Sockets;
using Serilog;
using KcdMp.Shared.Protocol;
using KcdMp.Server.Sessions;

namespace KcdMp.Server;

public class RelayServer(
    int port,
    bool echo = false,
    string password = "",
    TimeSpan? sessionIdleTimeout = null,
    ILogger? logger = null)
{
    private readonly List<ClientSession> _clients = [];
    private readonly List<Task> _sessionTasks = [];
    private readonly ServerSessionBackend _sessionBackend = new(sessionIdleTimeout);
    private readonly Dictionary<Guid, ClientSession> _clientsBySessionId = [];
    private readonly object _lock = new();
    private readonly ILogger _logger = logger ?? Log.Logger;
    public bool Echo { get; } = echo;
    public string Password { get; } = password;

    public async Task RunAsync(CancellationToken ct = default)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        _logger.Information("Listening on port {Port}...", port);
        _logger.Information("Waiting for clients to connect.");
        var timeoutTask = MonitorIdleSessionsAsync(ct);

        try
        {
            while (true)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                var serverSession = _sessionBackend.CreateSession(tcp.Client.RemoteEndPoint?.ToString());
                var session = new ClientSession(tcp, this, serverSession.SessionId, _logger);
                _sessionBackend.AttachTransportClientId(serverSession.SessionId, session.Id);

                lock (_lock)
                {
                    _clients.Add(session);
                    _clientsBySessionId[session.SessionId] = session;
                }

                var sessionTask = session.RunAsync().ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        _clients.Remove(session);
                        _clientsBySessionId.Remove(session.SessionId);
                        _sessionTasks.RemoveAll(x => x.IsCompleted);
                    }

                    _sessionBackend.CloseSession(session.SessionId, session.CloseReason);
                    _logger.Information("[-] {Client} disconnected. Clients: {ClientCount}. Reason: {CloseReason}. SessionId: {SessionId}",
                        session.Name ?? $"id={session.Id}", _clients.Count, session.CloseReason, session.SessionId);
                    if (session.IsReady)
                        BroadcastDisconnect(session);
                });

                lock (_lock)
                    _sessionTasks.Add(sessionTask);
            }
        }
        catch (OperationCanceledException)
        {
            // Server cancellation is expected for Ctrl+C / process shutdown.
        }
        finally
        {
            listener.Stop();
            ShutdownActiveSessions();
            Task[] runningSessions;
            lock (_lock)
                runningSessions = [.. _sessionTasks];
            await Task.WhenAll(runningSessions);
            try { await timeoutTask; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Broadcasts a position update from <paramref name="source"/> to all other ready clients.
    /// In echo mode also reflects the position back to the sender as ghost id=0.</summary>
    public void Broadcast(ClientSession source, float x, float y, float z, float rotZ, byte flags)
    {
        List<ClientSession> targets;
        lock (_lock)
            targets = [.. _clients.Where(c => c != source && c.IsReady)];

        foreach (var target in targets)
            target.EnqueueGhost(source.Id, x, y, z, rotZ, flags);

        if (Echo)
        {
            // Place echo ghost 1 m to the right of the player's facing direction
            float sideX = (float)Math.Cos(rotZ);
            float sideY = -(float)Math.Sin(rotZ);
            source.EnqueueGhost(0, x + sideX, y + sideY, z, rotZ, flags);
        }
    }

    /// <summary>Sends a Name (0x03) packet about <paramref name="source"/> to all other ready clients.</summary>
    public void BroadcastName(ClientSession source)
    {
        if (source.Name is null) return;

        List<ClientSession> targets;
        lock (_lock)
            targets = [.. _clients.Where(c => c != source && c.IsReady)];

        foreach (var target in targets)
            target.EnqueueName(source.Id, source.Name);
    }

    /// <summary>Broadcasts a Disconnect (0x06) packet to all remaining clients so they can remove the ghost.</summary>
    public void BroadcastDisconnect(ClientSession disconnected)
    {
        List<ClientSession> targets;
        lock (_lock)
            targets = [.. _clients.Where(c => c.IsReady)];

        foreach (var target in targets)
            target.EnqueueDisconnect(disconnected.Id);
    }

    /// <summary>Sends Name (0x03) packets of all currently ready clients to <paramref name="newClient"/>.</summary>
    public void SendAllNamesTo(ClientSession newClient)
    {
        List<ClientSession> existing;
        lock (_lock)
            existing = [.. _clients.Where(c => c != newClient && c.IsReady)];

        foreach (var c in existing)
            newClient.EnqueueName(c.Id, c.Name!);
    }

    /// <summary>Broadcasts a StateSync (0x08) packet to all other ready clients.</summary>
    public void BroadcastState(ClientSession source, byte stateType, byte[] payload)
    {
        List<ClientSession> targets;
        lock (_lock)
            targets = [.. _clients.Where(c => c != source && c.IsReady)];

        var packet = PacketWriter.StateSync(source.Id, stateType, payload);
        foreach (var target in targets)
            target.EnqueueRaw(packet);
    }

    /// <summary>Broadcasts an EventRelay (0x0A) packet to all other ready clients.</summary>
    public void BroadcastEvent(ClientSession source, ushort eventType, byte[] jsonPayload)
    {
        List<ClientSession> targets;
        lock (_lock)
            targets = [.. _clients.Where(c => c != source && c.IsReady)];

        var packet = PacketWriter.EventRelay(source.Id, eventType, jsonPayload);
        foreach (var target in targets)
            target.EnqueueRaw(packet);
    }

    public IReadOnlyList<ServerSessionRecord> GetActiveSessions()
        => _sessionBackend.GetActiveSessions();

    public IReadOnlyList<ServerSessionRecord> GetClosedSessions()
        => _sessionBackend.GetClosedSessions();

    public IReadOnlyList<ServerSessionRecord> GetPendingAuthenticationSessions()
        => _sessionBackend.GetPendingAuthenticationSessions();

    public IReadOnlyList<ServerSessionLifecycleEvent> GetSessionLifecycleEvents()
        => _sessionBackend.GetLifecycleEvents();

    internal void MarkSessionActivity(Guid sessionId)
        => _sessionBackend.TouchSession(sessionId);

    internal void MarkAuthenticationAccepted(Guid sessionId)
        => _sessionBackend.MarkAuthenticationAccepted(sessionId);

    internal void MarkAuthenticationRejected(Guid sessionId)
        => _sessionBackend.MarkAuthenticationRejected(sessionId);

    private async Task MonitorIdleSessionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var timedOut = _sessionBackend.GetTimedOutSessionCandidates();
            if (timedOut.Count > 0)
            {
                lock (_lock)
                {
                    foreach (var candidate in timedOut)
                    {
                        if (_clientsBySessionId.TryGetValue(candidate.SessionId, out var session))
                            session.RequestClose(ServerSessionCloseReason.Timeout);
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private void ShutdownActiveSessions()
    {
        List<ClientSession> active;
        lock (_lock)
            active = [.. _clients];

        foreach (var session in active)
            session.RequestClose(ServerSessionCloseReason.Shutdown);
    }
}
