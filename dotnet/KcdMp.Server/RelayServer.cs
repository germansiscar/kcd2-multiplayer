using System.Net;
using System.Net.Sockets;
using KcdMp.Server.Characters;
using KcdMp.Server.Identity;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using KcdMp.Server.Sessions;
using KcdMp.Shared.Protocol;
using Serilog;

namespace KcdMp.Server;

public class RelayServer
{
    private readonly int _port;
    private readonly List<ClientSession> _clients = [];
    private readonly List<Task> _sessionTasks = [];
    private readonly ServerSessionBackend _sessionBackend;
    private readonly IPlayerIdentityService _identityService;
    private readonly ICharacterSessionBindingService _characterBindingService;
    private readonly Dictionary<Guid, ClientSession> _clientsBySessionId = [];
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private readonly IServerObservabilitySink _observability;

    public RelayServer(
        int port,
        bool echo = false,
        string password = "",
        TimeSpan? sessionIdleTimeout = null,
        JsonPersistenceOptions? persistenceOptions = null,
        PlayerIdentityOptions? identityOptions = null,
        CharacterSessionBindingOptions? characterBindingOptions = null,
        ILogger? logger = null,
        IServerObservabilitySink? observability = null,
        ServerObservabilityOptions? observabilityOptions = null,
        IJsonPersistenceStore? persistenceStore = null,
        IPlayerIdentityService? identityService = null,
        ICharacterSessionBindingService? characterBindingService = null)
    {
        _port = port;
        Echo = echo;
        Password = password;
        _logger = logger ?? Log.Logger;
        _sessionBackend = new ServerSessionBackend(sessionIdleTimeout);
        _sessionBackend.LifecycleEventEmitted += HandleSessionLifecycleEvent;
        _observability = observability ?? new SerilogServerObservabilitySink(_logger, observabilityOptions);
        var store = persistenceStore ?? new JsonFilePersistenceStore(persistenceOptions, _logger, _observability);
        _identityService = identityService ?? new PlayerIdentityService(store, _observability, identityOptions, _logger);
        if (characterBindingService is not null)
        {
            _characterBindingService = characterBindingService;
        }
        else
        {
            var characterService = new CharacterProfileService(store, _identityService, _observability, _logger);
            _characterBindingService = new CharacterSessionBindingService(
                characterService,
                _identityService,
                characterBindingOptions,
                _logger);
        }
    }

    public bool Echo { get; }
    public string Password { get; }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Emit(
            ServerObservableEventType.ServerStarted,
            ServerObservableComponent.ServerLifecycle,
            ServerObservableSeverity.Information,
            "Relay server started.",
            payload: new Dictionary<string, object?> { ["port"] = _port });
        _logger.Information("Listening on port {Port}...", _port);
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

                var sessionTask = session.RunAsync().ContinueWith(task =>
                {
                    lock (_lock)
                    {
                        _clients.Remove(session);
                        _clientsBySessionId.Remove(session.SessionId);
                        _sessionTasks.RemoveAll(x => x.IsCompleted);
                    }

                    if (task.IsFaulted)
                    {
                        Emit(
                            ServerObservableEventType.BackendError,
                            ServerObservableComponent.Backend,
                            ServerObservableSeverity.Error,
                            "Unexpected client session failure.",
                            session.SessionId,
                            new Dictionary<string, object?>
                            {
                                ["exception"] = task.Exception?.GetBaseException().Message,
                                ["close_reason"] = session.CloseReason.ToString(),
                            });
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
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.BackendError,
                ServerObservableComponent.Backend,
                ServerObservableSeverity.Error,
                "Relay server loop failed.",
                payload: new Dictionary<string, object?> { ["exception"] = ex.Message });
            throw;
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

            Emit(
                ServerObservableEventType.ServerStopped,
                ServerObservableComponent.ServerLifecycle,
                ServerObservableSeverity.Information,
                "Relay server stopped.",
                payload: new Dictionary<string, object?> { ["port"] = _port });
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

    internal async Task<(bool Allowed, string? Reason, string? IdentityId)> ResolveAndAssociateIdentityAsync(
        Guid sessionId,
        PlayerIdentityClaim claim,
        string? preferredCharacterId,
        CancellationToken ct = default)
    {
        var resolved = await _identityService.ResolveOrCreateAsync(claim, ct);
        if (!resolved.IsAllowed || resolved.Identity is null)
            return (false, resolved.DenialReason ?? "Identity denied.", null);

        if (!_sessionBackend.TryAssociateIdentity(sessionId, resolved.Identity.InternalId))
        {
            Emit(
                ServerObservableEventType.IdentityAccessDenied,
                ServerObservableComponent.Session,
                ServerObservableSeverity.Warning,
                "Identity already has an active session.",
                sessionId,
                payload: new Dictionary<string, object?>
                {
                    ["identity_id"] = resolved.Identity.InternalId,
                });
            return (false, "Identity already connected.", resolved.Identity.InternalId);
        }

        var binding = await _characterBindingService.BindAsync(resolved.Identity.InternalId, preferredCharacterId, ct);
        if (!binding.IsAllowed)
        {
            _sessionBackend.ClearIdentityAssociation(sessionId);
            Emit(
                ServerObservableEventType.CharacterAccessDenied,
                ServerObservableComponent.Session,
                ServerObservableSeverity.Warning,
                "Character binding denied.",
                sessionId,
                payload: new Dictionary<string, object?>
                {
                    ["identity_id"] = resolved.Identity.InternalId,
                    ["character_id"] = preferredCharacterId,
                    ["reason"] = binding.DenialReason,
                });
            return (false, binding.DenialReason ?? "Character binding denied.", resolved.Identity.InternalId);
        }

        _sessionBackend.SetCharacterReference(sessionId, binding.CharacterId);
        return (true, null, resolved.Identity.InternalId);
    }

    internal void EmitBackendError(Guid? sessionId, string message, Exception? ex = null)
    {
        Emit(
            ServerObservableEventType.BackendError,
            ServerObservableComponent.Backend,
            ServerObservableSeverity.Error,
            message,
            sessionId,
            new Dictionary<string, object?> { ["exception"] = ex?.Message });
    }

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

    private void HandleSessionLifecycleEvent(ServerSessionLifecycleEvent lifecycleEvent)
    {
        var eventType = lifecycleEvent.Type switch
        {
            ServerSessionLifecycleEventType.SessionCreated => ServerObservableEventType.SessionCreated,
            ServerSessionLifecycleEventType.AuthenticationAccepted => ServerObservableEventType.AuthenticationAccepted,
            ServerSessionLifecycleEventType.AuthenticationRejected => ServerObservableEventType.AuthenticationRejected,
            ServerSessionLifecycleEventType.AssociationPending => ServerObservableEventType.AssociationPending,
            ServerSessionLifecycleEventType.SessionClosed => ServerObservableEventType.SessionClosed,
            _ => ServerObservableEventType.BackendError,
        };

        var severity = lifecycleEvent.Type switch
        {
            ServerSessionLifecycleEventType.AuthenticationRejected => ServerObservableSeverity.Warning,
            _ => ServerObservableSeverity.Information,
        };

        var payload = lifecycleEvent.CloseReason is null
            ? null
            : new Dictionary<string, object?> { ["close_reason"] = lifecycleEvent.CloseReason.ToString() };

        Emit(eventType, ServerObservableComponent.Session, severity, lifecycleEvent.Type.ToString(), lifecycleEvent.SessionId, payload);

        if (lifecycleEvent.CloseReason == ServerSessionCloseReason.Timeout)
        {
            Emit(
                ServerObservableEventType.SessionTimeout,
                ServerObservableComponent.Session,
                ServerObservableSeverity.Warning,
                "Session closed by timeout.",
                lifecycleEvent.SessionId);
        }
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableComponent component,
        ServerObservableSeverity severity,
        string message,
        Guid? sessionId = null,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        _observability.Emit(new ServerObservableEvent(
            type,
            component,
            severity,
            DateTimeOffset.UtcNow,
            SessionId: sessionId,
            Message: message,
            Payload: payload));
    }
}
