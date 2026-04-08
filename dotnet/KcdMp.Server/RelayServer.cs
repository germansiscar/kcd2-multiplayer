using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KcdMp.Server.AccessControl;
using KcdMp.Server.Admin;
using KcdMp.Server.Audit;
using KcdMp.Server.Bans;
using KcdMp.Server.Characters;
using KcdMp.Server.Communication;
using KcdMp.Server.Currency;
using KcdMp.Server.Identity;
using KcdMp.Server.Inventory;
using KcdMp.Server.InventoryRules;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using KcdMp.Server.Respawn;
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
    private readonly IServerAccessControlService _accessControlService;
    private readonly IIdentityBanService _identityBanService;
    private readonly IPlayerIdentityService _identityService;
    private readonly ICharacterProfileService _characterProfileService;
    private readonly ICharacterSessionBindingService _characterBindingService;
    private readonly ICharacterLifecycleService _characterLifecycleService;
    private readonly ICharacterInventoryService _characterInventoryService;
    private readonly ICharacterCurrencyService _characterCurrencyService;
    private readonly ICharacterRespawnService _characterRespawnService;
    private readonly IInventoryRulesConfigurationService _inventoryRulesService;
    private readonly IServerAdminService _adminService;
    private readonly Dictionary<Guid, ClientSession> _clientsBySessionId = [];
    private readonly HashSet<string> _bootstrapAdminIdentityIds;
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private readonly IServerObservabilitySink _observability;
    private readonly Dictionary<uint, PendingStateProjection> _pendingProjections = [];
    private readonly bool _worldInitEnabled;
    private readonly int _worldInitMaxRetries;
    private readonly bool _worldInitBlockOnCriticalFailure;
    private readonly string _worldInitSkillsPerksMode;
    private readonly bool _worldInitReapplyOnZoneLoad;
    private uint _projectionCounter;
    private long _presenceRevision;
    private readonly Dictionary<Guid, PresenceSpatialSnapshot> _presenceSpatialBySession = [];
    private readonly Dictionary<Guid, PresenceSnapshotEntry> _lastPresenceBySession = [];
    private readonly HashSet<Guid> _presenceWorldReadySessions = [];
    private readonly ServerCommunicationOptions _communicationOptions;
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _chatRateBuckets = [];
    private readonly Random _chatRandom = new();

    public RelayServer(
        int port,
        bool echo = false,
        string password = "",
        TimeSpan? sessionIdleTimeout = null,
        JsonPersistenceOptions? persistenceOptions = null,
        ServerAccessControlOptions? accessControlOptions = null,
        IdentityBanOptions? identityBanOptions = null,
        PlayerIdentityOptions? identityOptions = null,
        CharacterSessionBindingOptions? characterBindingOptions = null,
        CharacterLifecycleOptions? characterLifecycleOptions = null,
        CharacterInventoryOptions? characterInventoryOptions = null,
        CharacterCurrencyOptions? characterCurrencyOptions = null,
        CharacterRespawnOptions? characterRespawnOptions = null,
        InventoryRulesOptions? inventoryRulesOptions = null,
        ILogger? logger = null,
        IServerObservabilitySink? observability = null,
        ServerObservabilityOptions? observabilityOptions = null,
        ServerAuditOptions? auditOptions = null,
        IJsonPersistenceStore? persistenceStore = null,
        IServerAccessControlService? accessControlService = null,
        IIdentityBanService? identityBanService = null,
        IPlayerIdentityService? identityService = null,
        ICharacterProfileService? characterProfileService = null,
        ICharacterSessionBindingService? characterBindingService = null,
        ICharacterLifecycleService? characterLifecycleService = null,
        ICharacterInventoryService? characterInventoryService = null,
        ICharacterCurrencyService? characterCurrencyService = null,
        ICharacterRespawnService? characterRespawnService = null,
        IInventoryRulesConfigurationService? inventoryRulesService = null,
        ServerCommunicationOptions? communicationOptions = null,
        bool worldInitEnabled = true,
        int worldInitMaxRetries = 1,
        bool worldInitBlockOnCriticalFailure = true,
        string? worldInitSkillsPerksMode = null,
        bool worldInitReapplyOnZoneLoad = true,
        IReadOnlyCollection<string>? bootstrapAdminIdentityIds = null,
        IServerAdminService? adminService = null)
    {
        _port = port;
        Echo = echo;
        Password = password;
        _logger = logger ?? Log.Logger;
        _bootstrapAdminIdentityIds = bootstrapAdminIdentityIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(bootstrapAdminIdentityIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.Ordinal);
        _worldInitEnabled = worldInitEnabled;
        _worldInitMaxRetries = Math.Max(0, worldInitMaxRetries);
        _worldInitBlockOnCriticalFailure = worldInitBlockOnCriticalFailure;
        _worldInitSkillsPerksMode = string.IsNullOrWhiteSpace(worldInitSkillsPerksMode) ? "pending" : worldInitSkillsPerksMode.Trim();
        _worldInitReapplyOnZoneLoad = worldInitReapplyOnZoneLoad;
        _communicationOptions = communicationOptions ?? new ServerCommunicationOptions();
        _sessionBackend = new ServerSessionBackend(sessionIdleTimeout);
        _sessionBackend.LifecycleEventEmitted += HandleSessionLifecycleEvent;
        if (observability is not null)
        {
            _observability = observability;
        }
        else
        {
            var runtimeObservability = new SerilogServerObservabilitySink(_logger, observabilityOptions);
            var auditObservability = new PersistentAuditObservabilitySink(
                persistenceOptions,
                auditOptions,
                _logger);
            _observability = new CompositeServerObservabilitySink(
                [runtimeObservability, auditObservability],
                _logger);
        }

        var store = persistenceStore ?? new JsonFilePersistenceStore(persistenceOptions, _logger, _observability);
        _accessControlService = accessControlService ?? new ServerAccessControlService(store, _observability, accessControlOptions, _logger);
        _identityBanService = identityBanService ?? new IdentityBanService(store, _observability, identityBanOptions, _logger);
        _identityService = identityService ?? new PlayerIdentityService(store, _observability, identityOptions, _logger);
        _characterProfileService = characterProfileService ?? new CharacterProfileService(store, _identityService, _observability, _logger);
        if (characterBindingService is not null)
        {
            _characterBindingService = characterBindingService;
        }
        else
        {
            _characterBindingService = new CharacterSessionBindingService(
                _characterProfileService,
                _identityService,
                characterBindingOptions,
                _logger);
        }

        _characterLifecycleService = characterLifecycleService
                                     ?? new CharacterLifecycleService(
                                         store,
                                         _characterProfileService,
                                         _observability,
                                         characterLifecycleOptions,
                                         _logger);
        _characterInventoryService = characterInventoryService
                                    ?? new CharacterInventoryService(
                                        store,
                                        _observability,
                                        characterInventoryOptions,
                                        _logger);
        _inventoryRulesService = inventoryRulesService
                                 ?? new InventoryRulesConfigurationService(
                                     store,
                                     _observability,
                                     inventoryRulesOptions,
                                     _logger);
        _characterCurrencyService = characterCurrencyService
                                    ?? new CharacterCurrencyService(
                                        store,
                                        _observability,
                                        characterCurrencyOptions,
                                        _logger);
        _characterRespawnService = characterRespawnService
                                   ?? new CharacterRespawnService(
                                       store,
                                       _observability,
                                       characterRespawnOptions,
                                       _inventoryRulesService,
                                       logger: _logger);
        _adminService = adminService
                        ?? new ServerAdminService(
                            _identityService,
                            _characterProfileService,
                            _identityBanService,
                            _observability,
                            listSessions: () => _sessionBackend.GetActiveSessions(),
                            kickSession: TryKickSessionById,
                            applyBan: ApplyIdentityBanAsync,
                            revokeBan: RevokeIdentityBanAsync,
                            persistenceOptions: persistenceOptions,
                            logger: _logger);
    }

    public bool Echo { get; }
    public string Password { get; }
    public IServerAdminService Admin => _adminService;

    public async Task RunAsync(CancellationToken ct = default)
    {
        await _accessControlService.GetActiveConfigurationAsync(ct);
        await _inventoryRulesService.GetActiveConfigurationAsync(ct);
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

                    HandleCharacterLifecycleOnSessionClose(session.SessionId, session.CloseReason);
                    RemovePendingProjectionsForSession(session.SessionId);
                    lock (_lock)
                    {
                        _presenceWorldReadySessions.Remove(session.SessionId);
                        _chatRateBuckets.Remove(session.SessionId);
                    }
                    _sessionBackend.CloseSession(session.SessionId, session.CloseReason);
                    _logger.Information("[-] {Client} disconnected. Clients: {ClientCount}. Reason: {CloseReason}. SessionId: {SessionId}",
                        session.Name ?? $"id={session.Id}", _clients.Count, session.CloseReason, session.SessionId);
                    if (session.IsReady)
                    {
                        BroadcastDisconnect(session);
                        _ = ProjectPresenceSnapshotToAllReadyAsync("session_closed", session.SessionId);
                    }
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
            await _characterCurrencyService.SaveAndUnloadAllAsync(CharacterLifecycleSaveReason.Shutdown, CancellationToken.None);
            await _characterRespawnService.SaveAndUnloadAllAsync(CharacterLifecycleSaveReason.Shutdown, CancellationToken.None);
            await _characterInventoryService.SaveAndUnloadAllAsync(CharacterLifecycleSaveReason.Shutdown, CancellationToken.None);
            await _characterLifecycleService.SaveAndUnloadAllAsync(CharacterLifecycleSaveReason.Shutdown, CancellationToken.None);
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
        if (!TryGetPresenceSourceSession(source, out var sourceSession))
            return;

        lock (_lock)
        {
            _presenceSpatialBySession[source.SessionId] = new PresenceSpatialSnapshot(
                x,
                y,
                z,
                rotZ,
                IsRiding: (flags & 0x01) != 0,
                UpdatedAtUtc: DateTimeOffset.UtcNow);
        }

        List<ClientSession> targets;
        lock (_lock)
            targets = [.. _clients.Where(c => c != source && c.IsReady)];

        foreach (var target in targets)
            target.EnqueueGhost(sourceSession.TransportClientId!.Value, x, y, z, rotZ, flags);

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
        if (disconnected.IsReady)
        {
            lock (_lock)
                _presenceSpatialBySession.Remove(disconnected.SessionId);
        }

        List<ClientSession> targets;
        lock (_lock)
            targets = [.. _clients.Where(c => c.IsReady)];

        foreach (var target in targets)
            target.EnqueueDisconnect(disconnected.Id);
    }

    internal void NotifyPresenceSessionReady(Guid sessionId)
    {
        if (_worldInitEnabled && !IsPresenceWorldContextReady(sessionId))
        {
            EmitPresenceBlockedByWorldInit(sessionId, "session_ready_waiting_world_init");
            _ = SendPresenceBlockedProjectionToClientAsync(sessionId, "world_init_pending");
            return;
        }

        _ = ProjectPresenceSnapshotToAllReadyAsync("session_ready", sessionId);
    }

    private bool TryGetPresenceSourceSession(ClientSession source, out ServerSessionRecord session)
    {
        session = default!;
        if (!source.IsReady)
            return false;

        var active = _sessionBackend.GetActiveSessions().FirstOrDefault(x => x.SessionId == source.SessionId);
        if (active is null || active.TransportClientId is null)
            return false;

        if (!string.IsNullOrWhiteSpace(active.IdentityId) && !string.IsNullOrWhiteSpace(active.CharacterId))
        {
            if (_worldInitEnabled && !IsPresenceWorldContextReady(active.SessionId))
            {
                EmitPresenceBlockedByWorldInit(active.SessionId, "position_update_ignored_world_init_incomplete");
                return false;
            }

            session = active;
            return true;
        }

        Emit(
            ServerObservableEventType.PresenceDesyncDetected,
            ServerObservableComponent.Session,
            ServerObservableSeverity.Warning,
            "Ignored position update from session without ready presence context.",
            source.SessionId,
            new Dictionary<string, object?>
            {
                ["transport_client_id"] = source.Id,
                ["identity_id"] = active.IdentityId,
                ["character_id"] = active.CharacterId,
            });
        return false;
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

    internal async Task HandleClientEventAsync(
        ClientSession source,
        ushort eventType,
        byte[] jsonPayload,
        CancellationToken ct = default)
    {
        if (eventType != (ushort)EventType.ChatSubmit)
        {
            BroadcastEvent(source, eventType, jsonPayload);
            return;
        }

        await HandleChatSubmitAsync(source, jsonPayload, ct);
    }

    private async Task HandleChatSubmitAsync(ClientSession source, byte[] jsonPayload, CancellationToken ct)
    {
        var rawText = ExtractChatText(jsonPayload);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            await SendSystemChatAsync(source, "Mensaje vacio.", "chat_empty");
            EmitChatEvent(ServerObservableEventType.ChatMessageRejected, source.SessionId, source.Id, "chat_empty", null, null);
            return;
        }

        var trimmed = rawText.Trim();
        if (trimmed.Length > _communicationOptions.MaxMessageLength)
            trimmed = trimmed[.._communicationOptions.MaxMessageLength];

        if (!TryConsumeChatRateToken(source.SessionId, DateTimeOffset.UtcNow))
        {
            await SendSystemChatAsync(source, "Rate limit de chat alcanzado. Espera un momento.", "chat_rate_limited");
            EmitChatEvent(ServerObservableEventType.ChatRateLimitTriggered, source.SessionId, source.Id, "chat_rate_limited", null, null);
            return;
        }

        var senderSession = _sessionBackend.GetActiveSessions().FirstOrDefault(x => x.SessionId == source.SessionId);
        if (senderSession is null || string.IsNullOrWhiteSpace(senderSession.IdentityId) || string.IsNullOrWhiteSpace(senderSession.CharacterId))
        {
            await SendSystemChatAsync(source, "Sesion sin contexto de personaje activo.", "chat_session_context_missing");
            EmitChatEvent(ServerObservableEventType.ChatMessageRejected, source.SessionId, source.Id, "chat_session_context_missing", null, null);
            return;
        }

        var identity = await _identityService.GetByInternalIdAsync(senderSession.IdentityId!, ct);
        var character = await _characterProfileService.GetByInternalIdAsync(senderSession.CharacterId!, ct);
        if (identity is null || character is null)
        {
            await SendSystemChatAsync(source, "No se pudo resolver la identidad del mensaje.", "chat_sender_identity_missing");
            EmitChatEvent(ServerObservableEventType.ChatMessageRejected, source.SessionId, source.Id, "chat_sender_identity_missing", null, null);
            return;
        }

        var command = ParseChatCommand(trimmed);
        if (command.Kind == ChatCommandKind.Invalid)
        {
            await SendSystemChatAsync(source, "Canal/comando de chat invalido.", "chat_invalid_channel");
            EmitChatEvent(ServerObservableEventType.ChatInvalidChannel, source.SessionId, source.Id, "chat_invalid_channel", null, null);
            return;
        }

        if (command.Kind == ChatCommandKind.System && identity.Role != PlayerIdentityRole.Admin)
        {
            await SendSystemChatAsync(source, "Solo admins pueden usar /sys.", "chat_admin_required");
            EmitChatEvent(ServerObservableEventType.ChatMessageRejected, source.SessionId, source.Id, "chat_admin_required", null, null);
            return;
        }

        var respawn = await _characterRespawnService.GetLoadedForSessionAsync(source.SessionId, ct);
        var isUnconscious = respawn?.State == CharacterDefeatState.Unconscious;
        if (isUnconscious && !command.AllowedWhileUnconscious)
        {
            await SendSystemChatAsync(source, "No puedes hablar en este estado. Usa /ooc, /me, /do o /try.", "chat_unconscious_restricted");
            EmitChatEvent(ServerObservableEventType.ChatMessageRejected, source.SessionId, source.Id, "chat_unconscious_restricted", null, null);
            return;
        }

        var senderSpatial = TryGetSpatial(source.SessionId);
        if (command.RequiresSpatialContext && senderSpatial is null)
        {
            await SendSystemChatAsync(source, "Contexto de presencia no listo para chat local.", "chat_presence_missing");
            EmitChatEvent(ServerObservableEventType.ChatMessageRejected, source.SessionId, source.Id, "chat_presence_missing", null, null);
            return;
        }

        var senderZone = senderSpatial is null ? null : ComputeZoneKey(senderSpatial.X, senderSpatial.Y);
        var senderName = string.IsNullOrWhiteSpace(character.FullName) ? source.Name ?? "Unknown" : character.FullName;
        IReadOnlyList<ClientSession> recipients;

        if (command.Kind == ChatCommandKind.WhisperDirect)
        {
            var target = await ResolveWhisperTargetAsync(source.SessionId, command.TargetHint ?? "", ct);
            if (target is null)
            {
                await SendSystemChatAsync(source, "Jugador objetivo no encontrado para /w.", "chat_whisper_target_missing");
                EmitChatEvent(ServerObservableEventType.ChatMessageRejected, source.SessionId, source.Id, "chat_whisper_target_missing", null, null);
                return;
            }

            if (senderSpatial is null || target.Spatial is null)
            {
                await SendSystemChatAsync(source, "No hay contexto espacial valido para /w.", "chat_whisper_presence_missing");
                EmitChatEvent(ServerObservableEventType.ChatMessageRejected, source.SessionId, source.Id, "chat_whisper_presence_missing", null, null);
                return;
            }

            var sameZone = string.Equals(senderZone, ComputeZoneKey(target.Spatial.X, target.Spatial.Y), StringComparison.Ordinal);
            var distance = ComputeDistance(senderSpatial, target.Spatial);
            if (!sameZone || distance > _communicationOptions.ProximityWhisperRadius)
            {
                await SendSystemChatAsync(source, "El objetivo de /w esta fuera de alcance.", "chat_whisper_out_of_range");
                EmitChatEvent(ServerObservableEventType.ChatMessageRejected, source.SessionId, source.Id, "chat_whisper_out_of_range", null, distance);
                return;
            }

            recipients = source.SessionId == target.Client.SessionId
                ? [target.Client]
                : [source, target.Client];
        }
        else if (command.Scope == ChatScope.Global || command.Scope == ChatScope.System)
        {
            lock (_lock)
                recipients = [.. _clients.Where(c => c.IsReady)];
        }
        else
        {
            recipients = ResolveProximityRecipients(source, senderSpatial!, command.Mode);
        }

        if (recipients.Count == 0)
            recipients = [source];

        var messageId = $"chat_{Guid.NewGuid():N}";
        var tryOutcome = command.Kind == ChatCommandKind.Try ? RollTryOutcome() : (bool?)null;
        var formatted = FormatChatLine(command, senderName, command.Content, tryOutcome);

        var payloadObject = new
        {
            messageId,
            channel = command.Channel,
            scope = command.Scope.ToString().ToLowerInvariant(),
            mode = command.Mode.ToString().ToLowerInvariant(),
            senderCharacterId = character.InternalId,
            senderCharacterName = senderName,
            senderIdentityId = identity.InternalId,
            senderTransportClientId = source.Id,
            text = command.Content,
            formatted,
            tryOutcome,
            zone = senderZone,
            timestampUtc = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payloadObject);
        var packet = PacketWriter.EventRelay(source.Id, (ushort)EventType.ChatMessage, json);
        foreach (var recipient in recipients)
            recipient.EnqueueRaw(packet);

        EmitChatEvent(
            ServerObservableEventType.ChatMessageAccepted,
            source.SessionId,
            source.Id,
            command.Channel,
            recipients.Count,
            null,
            command.Content);
        EmitChatEvent(
            ServerObservableEventType.ChatMessageDelivered,
            source.SessionId,
            source.Id,
            command.Channel,
            recipients.Count,
            null);
    }

    public IReadOnlyList<ServerSessionRecord> GetActiveSessions()
        => _sessionBackend.GetActiveSessions();

    public IReadOnlyList<ServerSessionRecord> GetClosedSessions()
        => _sessionBackend.GetClosedSessions();

    public IReadOnlyList<ServerSessionRecord> GetPendingAuthenticationSessions()
        => _sessionBackend.GetPendingAuthenticationSessions();

    public IReadOnlyList<ServerSessionLifecycleEvent> GetSessionLifecycleEvents()
        => _sessionBackend.GetLifecycleEvents();

    public bool TryKickSessionById(Guid sessionId)
    {
        ClientSession? client = null;
        lock (_lock)
            _clientsBySessionId.TryGetValue(sessionId, out client);

        if (client is null)
            return false;

        SendAdministrativeInvalidationProjection(
            client,
            sessionId,
            reasonCode: "kicked",
            message: "Session closed by server administration.",
            accessDenied: true,
            isBanned: false,
            kicked: true);
        client.RequestClose(ServerSessionCloseReason.AuthenticationRejected);
        return true;
    }

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
        var accessConfig = await _accessControlService.GetActiveConfigurationAsync(ct);
        var resolved = await _identityService.ResolveOrCreateAsync(claim, accessConfig.AccessMode, ct);
        if (!resolved.IsAllowed || resolved.Identity is null)
            return (false, resolved.DenialReason ?? "Identity resolution failed.", null);

        if (_bootstrapAdminIdentityIds.Contains(resolved.Identity.InternalId))
        {
            await _identityService.TrySetRoleAsync(resolved.Identity.InternalId, PlayerIdentityRole.Admin, ct);
            await _identityService.TrySetStatusAsync(resolved.Identity.InternalId, PlayerIdentityStatus.Active, ct);
            var refreshed = await _identityService.GetByInternalIdAsync(resolved.Identity.InternalId, ct);
            if (refreshed is not null)
            {
                resolved = resolved with { Identity = refreshed };
            }
        }

        var banDecision = await _identityBanService.EvaluateAccessAsync(
            sessionId,
            resolved.Identity.InternalId,
            ct);
        if (banDecision.IsDenied)
            return (false, banDecision.DenialReason ?? "Identity banned.", resolved.Identity.InternalId);

        var accessDecision = await _accessControlService.EvaluateIdentityAccessAsync(
            new ServerAccessIdentityEvaluationRequest(
                SessionId: sessionId,
                IdentityId: resolved.Identity.InternalId,
                IdentityStatus: resolved.Identity.Status,
                IsDeactivated: resolved.Identity.IsDeactivated,
                IdentityCreatedInThisAttempt: resolved.Created),
            ct);
        if (!accessDecision.IsAllowed)
            return (false, accessDecision.DenialReason ?? "Identity denied by access policy.", resolved.Identity.InternalId);

        if (!_sessionBackend.TryAssociateIdentity(sessionId, resolved.Identity.InternalId))
        {
            _accessControlService.EmitDuplicateActiveSessionDenied(sessionId, resolved.Identity.InternalId);
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

        if (!string.IsNullOrWhiteSpace(binding.CharacterId))
        {
            var validation = await _characterLifecycleService.ValidateLightAsync(
                resolved.Identity.InternalId,
                binding.CharacterId!,
                ct);
            if (!validation.IsAllowed)
            {
                _sessionBackend.ClearIdentityAssociation(sessionId);
                Emit(
                    ServerObservableEventType.CharacterLoadFailed,
                    ServerObservableComponent.Session,
                    ServerObservableSeverity.Warning,
                    "Character validation failed before load.",
                    sessionId,
                    payload: new Dictionary<string, object?>
                    {
                        ["identity_id"] = resolved.Identity.InternalId,
                        ["character_id"] = binding.CharacterId,
                        ["reason"] = validation.DenialReason,
                    });
                return (false, validation.DenialReason ?? "Character validation failed.", resolved.Identity.InternalId);
            }

            var loadResult = await _characterLifecycleService.LoadForSessionAsync(
                sessionId,
                resolved.Identity.InternalId,
                binding.CharacterId!,
                ct);
            if (!loadResult.Loaded)
            {
                _sessionBackend.ClearIdentityAssociation(sessionId);
                Emit(
                    ServerObservableEventType.CharacterLoadFailed,
                    ServerObservableComponent.Session,
                    ServerObservableSeverity.Warning,
                    "Character load failed while preparing session.",
                    sessionId,
                    payload: new Dictionary<string, object?>
                    {
                        ["identity_id"] = resolved.Identity.InternalId,
                        ["character_id"] = binding.CharacterId,
                        ["reason"] = loadResult.DenialReason,
                    });
                return (false, loadResult.DenialReason ?? "Character load failed.", resolved.Identity.InternalId);
            }

            var inventoryLoad = await _characterInventoryService.LoadForSessionAsync(
                sessionId,
                resolved.Identity.InternalId,
                binding.CharacterId!,
                ct);
            if (!inventoryLoad.Loaded)
            {
                await _characterLifecycleService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                _sessionBackend.ClearIdentityAssociation(sessionId);
                Emit(
                    ServerObservableEventType.InventoryLoadFailed,
                    ServerObservableComponent.Session,
                    ServerObservableSeverity.Warning,
                    "Inventory load failed while preparing session.",
                    sessionId,
                    payload: new Dictionary<string, object?>
                    {
                        ["identity_id"] = resolved.Identity.InternalId,
                        ["character_id"] = binding.CharacterId,
                        ["reason"] = inventoryLoad.DenialReason,
                    });
                return (false, inventoryLoad.DenialReason ?? "Inventory load failed.", resolved.Identity.InternalId);
            }

            var currencyLoad = await _characterCurrencyService.LoadForSessionAsync(
                sessionId,
                resolved.Identity.InternalId,
                binding.CharacterId!,
                ct);
            if (!currencyLoad.Loaded)
            {
                await _characterInventoryService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                await _characterLifecycleService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                _sessionBackend.ClearIdentityAssociation(sessionId);
                Emit(
                    ServerObservableEventType.CurrencyLoadFailed,
                    ServerObservableComponent.Session,
                    ServerObservableSeverity.Warning,
                    "Currency load failed while preparing session.",
                    sessionId,
                    payload: new Dictionary<string, object?>
                    {
                        ["identity_id"] = resolved.Identity.InternalId,
                        ["character_id"] = binding.CharacterId,
                        ["reason"] = currencyLoad.DenialReason,
                    });
                return (false, currencyLoad.DenialReason ?? "Currency load failed.", resolved.Identity.InternalId);
            }

            var respawnLoad = await _characterRespawnService.LoadForSessionAsync(
                sessionId,
                resolved.Identity.InternalId,
                binding.CharacterId!,
                ct);
            if (!respawnLoad.Loaded)
            {
                await _characterCurrencyService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                await _characterInventoryService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                await _characterLifecycleService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                _sessionBackend.ClearIdentityAssociation(sessionId);
                Emit(
                    ServerObservableEventType.RespawnLoadFailed,
                    ServerObservableComponent.Session,
                    ServerObservableSeverity.Warning,
                    "Respawn lifecycle load failed while preparing session.",
                    sessionId,
                    payload: new Dictionary<string, object?>
                    {
                        ["identity_id"] = resolved.Identity.InternalId,
                        ["character_id"] = binding.CharacterId,
                        ["reason"] = respawnLoad.DenialReason,
                    });
                return (false, respawnLoad.DenialReason ?? "Respawn lifecycle load failed.", resolved.Identity.InternalId);
            }

            var ruleApply = await _inventoryRulesService.ApplyCharacterDefeatStateAsync(
                sessionId,
                resolved.Identity.InternalId,
                binding.CharacterId!,
                respawnLoad.Lifecycle?.State ?? CharacterDefeatState.Alive,
                InventoryRuleTrigger.SessionLoad,
                ct);
            if (!ruleApply.Applied)
            {
                await _characterRespawnService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                await _characterCurrencyService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                await _characterInventoryService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                await _characterLifecycleService.SaveAndUnloadSessionAsync(
                    sessionId,
                    CharacterLifecycleSaveReason.SessionClosed,
                    ct);
                _sessionBackend.ClearIdentityAssociation(sessionId);
                Emit(
                    ServerObservableEventType.InventoryRuleApplyFailed,
                    ServerObservableComponent.Session,
                    ServerObservableSeverity.Warning,
                    "Inventory rules could not be initialized for session.",
                    sessionId,
                    new Dictionary<string, object?>
                    {
                        ["identity_id"] = resolved.Identity.InternalId,
                        ["character_id"] = binding.CharacterId,
                        ["reason"] = ruleApply.DenialReason,
                    });
                return (false, ruleApply.DenialReason ?? "Inventory rules initialization failed.", resolved.Identity.InternalId);
            }
        }

        _sessionBackend.SetCharacterReference(sessionId, binding.CharacterId);
        return (true, null, resolved.Identity.InternalId);
    }

    internal async Task ProjectInitialStateAsync(Guid sessionId, CancellationToken ct = default)
    {
        ClientSession? client;
        lock (_lock)
            _clientsBySessionId.TryGetValue(sessionId, out client);

        if (client is null || !client.IsReady)
            return;

        var session = _sessionBackend.GetActiveSessions().FirstOrDefault(x => x.SessionId == sessionId);
        if (session is null)
            return;

        var accessConfig = await _accessControlService.GetActiveConfigurationAsync(ct);
        var identity = !string.IsNullOrWhiteSpace(session.IdentityId)
            ? await _identityService.GetByInternalIdAsync(session.IdentityId!, ct)
            : null;
        var character = !string.IsNullOrWhiteSpace(session.CharacterId)
            ? await _characterProfileService.GetByInternalIdAsync(session.CharacterId!, ct)
            : null;
        var inventory = await _characterInventoryService.GetLoadedForSessionAsync(sessionId, ct);
        var currency = await _characterCurrencyService.GetLoadedForSessionAsync(sessionId, ct);
        var respawn = await _characterRespawnService.GetLoadedForSessionAsync(sessionId, ct);

        SendStateProjection(
            client,
            sessionId,
            ProjectionDomain.SessionCharacter,
            ProjectionApplicability.Direct,
            new
            {
                sessionId = session.SessionId.ToString(),
                transportClientId = session.TransportClientId,
                identityId = session.IdentityId,
                characterId = session.CharacterId,
                displayName = client.Name,
                readiness = "Ready",
            },
            retryable: true);

        if (_worldInitEnabled)
        {
            SendStateProjection(
                client,
                sessionId,
                ProjectionDomain.WorldInitialization,
                ProjectionApplicability.Direct,
                new
                {
                    cycleId = $"wi_{Guid.NewGuid():N}",
                    trigger = "session_connect",
                    blockOnCriticalFailure = _worldInitBlockOnCriticalFailure,
                    reapplyOnZoneLoad = _worldInitReapplyOnZoneLoad,
                    skillsPerksMode = _worldInitSkillsPerksMode,
                    criticalNpcCleanup = true,
                    criticalInventoryCleanup = true,
                    criticalEquipmentCleanup = true,
                    criticalSkillsPerksCleanup = !string.Equals(_worldInitSkillsPerksMode, "pending", StringComparison.OrdinalIgnoreCase),
                    criticalServerStateApply = true,
                    authoritativeStateReady = true,
                },
                retryable: true,
                maxRetries: _worldInitMaxRetries);

            await SendPresenceBlockedProjectionToClientAsync(sessionId, "world_init_pending");
        }
        else
        {
            await SendPresenceProjectionToClientAsync(
                client,
                sessionId,
                reason: "initial_state",
                ct);
        }

        SendStateProjection(
            client,
            sessionId,
            ProjectionDomain.LifeCycle,
            ProjectionApplicability.Direct,
            new
            {
                defeatState = respawn?.State.ToString() ?? CharacterDefeatState.Alive.ToString(),
                unconsciousUntilUtc = respawn?.UnconsciousUntilUtc,
                pendingRespawn = respawn?.State is CharacterDefeatState.PendingRespawn,
            },
            retryable: true);

        SendStateProjection(
            client,
            sessionId,
            ProjectionDomain.Inventory,
            ProjectionApplicability.Partial,
            new
            {
                characterId = session.CharacterId,
                containerCount = inventory?.Containers.Count ?? 0,
                totalItems = inventory?.Containers.Sum(x => x.Items.Count) ?? 0,
                reflectable = true,
                note = "Server inventory is canonical. Client/game reflection can be partial.",
            },
            retryable: true);

        SendStateProjection(
            client,
            sessionId,
            ProjectionDomain.Currency,
            ProjectionApplicability.Partial,
            new
            {
                characterId = session.CharacterId,
                balance = currency?.Balance ?? 0,
                reflectable = true,
                note = "Server currency is canonical. Client/game reflection can be partial.",
            },
            retryable: true);

        SendStateProjection(
            client,
            sessionId,
            ProjectionDomain.Administrative,
            ProjectionApplicability.Direct,
            new
            {
                accessMode = accessConfig.AccessMode.ToString(),
                identityRole = identity?.Role.ToString() ?? PlayerIdentityRole.Player.ToString(),
                identityStatus = identity?.Status.ToString() ?? PlayerIdentityStatus.Active.ToString(),
                characterStatus = character?.Status.ToString() ?? CharacterProfileStatus.Active.ToString(),
                accessDenied = false,
                isBanned = false,
                kicked = false,
            },
            retryable: true);
    }

    private async Task ProjectPresenceSnapshotToAllReadyAsync(
        string reason,
        Guid? changedSessionId,
        CancellationToken ct = default)
    {
        List<ClientSession> targets;
        lock (_lock)
            targets = [.. _clients.Where(x => x.IsReady)];

        if (targets.Count == 0)
            return;

        var snapshot = await BuildPresenceSnapshotAsync(reason, changedSessionId, ct);
        foreach (var client in targets)
        {
            SendStateProjection(
                client,
                client.SessionId,
                ProjectionDomain.Presence,
                ProjectionApplicability.Direct,
                snapshot,
                retryable: true);
        }
    }

    private async Task SendPresenceProjectionToClientAsync(
        ClientSession client,
        Guid sessionId,
        string reason,
        CancellationToken ct = default)
    {
        var snapshot = await BuildPresenceSnapshotAsync(reason, sessionId, ct);
        SendStateProjection(
            client,
            sessionId,
            ProjectionDomain.Presence,
            ProjectionApplicability.Direct,
            snapshot,
            retryable: true);
    }

    private Task SendPresenceBlockedProjectionToClientAsync(
        Guid sessionId,
        string reason)
    {
        ClientSession? client;
        lock (_lock)
            _clientsBySessionId.TryGetValue(sessionId, out client);

        if (client is null || !client.IsReady)
            return Task.CompletedTask;

        SendStateProjection(
            client,
            sessionId,
            ProjectionDomain.Presence,
            ProjectionApplicability.Direct,
            new
            {
                mode = "ghost_npc",
                projectionSource = "server",
                remotePresenceEnabled = false,
                visibility = "global",
                reason,
                changedSessionId = sessionId.ToString(),
                revision = _presenceRevision,
                worldInitRequired = _worldInitEnabled,
                presences = Array.Empty<object>(),
            },
            retryable: false);

        return Task.CompletedTask;
    }

    private async Task<object> BuildPresenceSnapshotAsync(
        string reason,
        Guid? changedSessionId,
        CancellationToken ct)
    {
        var active = _sessionBackend
            .GetActiveSessions()
            .Where(IsPresenceVisibleSession)
            .ToList();

        var entries = new List<PresenceSnapshotEntry>(active.Count);
        foreach (var session in active)
        {
            var displayName = ResolveDisplayName(session);
            var state = await ResolvePresenceStateAsync(session, ct);

            PresenceSpatialSnapshot? spatial;
            lock (_lock)
                _presenceSpatialBySession.TryGetValue(session.SessionId, out spatial);

            entries.Add(new PresenceSnapshotEntry(
                SessionId: session.SessionId,
                TransportClientId: session.TransportClientId!.Value,
                IdentityId: session.IdentityId!,
                CharacterId: session.CharacterId!,
                DisplayName: displayName,
                PresenceState: state,
                Spatial: spatial));
        }

        entries.Sort((a, b) => a.TransportClientId.CompareTo(b.TransportClientId));

        long revision;
        List<PresenceSnapshotEntry> created;
        List<PresenceSnapshotEntry> removed;
        lock (_lock)
        {
            _presenceRevision++;
            revision = _presenceRevision;
            created = entries.Where(x => !_lastPresenceBySession.ContainsKey(x.SessionId)).ToList();
            removed = _lastPresenceBySession.Values.Where(x => entries.All(current => current.SessionId != x.SessionId)).ToList();
            _lastPresenceBySession.Clear();
            foreach (var entry in entries)
                _lastPresenceBySession[entry.SessionId] = entry;
        }

        foreach (var entry in created)
        {
            Emit(
                ServerObservableEventType.PresenceCreated,
                ServerObservableComponent.Session,
                ServerObservableSeverity.Information,
                "Server presence entry created.",
                entry.SessionId,
                new Dictionary<string, object?>
                {
                    ["transport_client_id"] = entry.TransportClientId,
                    ["identity_id"] = entry.IdentityId,
                    ["character_id"] = entry.CharacterId,
                    ["presence_state"] = entry.PresenceState,
                });
        }

        foreach (var entry in removed)
        {
            Emit(
                ServerObservableEventType.PresenceRemoved,
                ServerObservableComponent.Session,
                ServerObservableSeverity.Information,
                "Server presence entry removed.",
                entry.SessionId,
                new Dictionary<string, object?>
                {
                    ["transport_client_id"] = entry.TransportClientId,
                    ["identity_id"] = entry.IdentityId,
                    ["character_id"] = entry.CharacterId,
                });
        }

        return new
        {
            mode = "ghost_npc",
            projectionSource = "server",
            remotePresenceEnabled = true,
            visibility = "global",
            reason,
            changedSessionId = changedSessionId?.ToString(),
            revision,
            worldInitRequired = _worldInitEnabled,
            presences = entries.Select(entry => new
            {
                sessionId = entry.SessionId.ToString(),
                transportClientId = entry.TransportClientId,
                identityId = entry.IdentityId,
                characterId = entry.CharacterId,
                displayName = entry.DisplayName,
                presenceState = entry.PresenceState,
                visibility = "visible",
                lastKnownPosition = entry.Spatial is null
                    ? null
                    : new
                    {
                        x = entry.Spatial.X,
                        y = entry.Spatial.Y,
                        z = entry.Spatial.Z,
                        rotZ = entry.Spatial.RotZ,
                        isRiding = entry.Spatial.IsRiding,
                        updatedAtUtc = entry.Spatial.UpdatedAtUtc,
                    },
            }),
        };
    }

    private bool IsPresenceVisibleSession(ServerSessionRecord session)
    {
        return session.State == ServerSessionState.Active
               && session.ConnectionState == ServerSessionConnectionState.Connected
               && session.AuthState == ServerSessionAuthState.Accepted
               && session.TransportClientId is not null
               && !string.IsNullOrWhiteSpace(session.IdentityId)
               && !string.IsNullOrWhiteSpace(session.CharacterId)
               && IsPresenceWorldContextReady(session.SessionId);
    }

    private bool IsPresenceWorldContextReady(Guid sessionId)
    {
        if (!_worldInitEnabled)
            return true;

        lock (_lock)
            return _presenceWorldReadySessions.Contains(sessionId);
    }

    private string ResolveDisplayName(ServerSessionRecord session)
    {
        lock (_lock)
        {
            if (_clientsBySessionId.TryGetValue(session.SessionId, out var client)
                && !string.IsNullOrWhiteSpace(client.Name))
            {
                return client.Name!;
            }
        }

        return $"Player-{session.TransportClientId}";
    }

    private async Task<string> ResolvePresenceStateAsync(ServerSessionRecord session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.CharacterId))
            return "not_visible";

        var respawn = await _characterRespawnService.GetLoadedForSessionAsync(session.SessionId, ct);
        if (respawn is null)
            return "normal";

        return respawn.State switch
        {
            CharacterDefeatState.Unconscious => "unconscious",
            CharacterDefeatState.PendingRespawn => "respawn_pending",
            _ => "normal",
        };
    }

    internal void RegisterStateProjectionResult(
        Guid sessionId,
        uint projectionId,
        byte domainRaw,
        byte statusRaw,
        byte[] detailsPayload)
    {
        var domain = Enum.IsDefined(typeof(ProjectionDomain), domainRaw)
            ? (ProjectionDomain)domainRaw
            : ProjectionDomain.SessionCharacter;
        var status = Enum.IsDefined(typeof(ProjectionApplyStatus), statusRaw)
            ? (ProjectionApplyStatus)statusRaw
            : ProjectionApplyStatus.Failed;
        var detailsJson = detailsPayload.Length == 0 ? "{}" : Encoding.UTF8.GetString(detailsPayload);

        var payload = new Dictionary<string, object?>
        {
            ["projection_id"] = projectionId,
            ["domain"] = domain.ToString(),
            ["status"] = status.ToString(),
            ["details"] = detailsJson,
        };

        switch (status)
        {
            case ProjectionApplyStatus.Started:
                Emit(
                    ServerObservableEventType.StateApplyStarted,
                    ServerObservableComponent.ClientIntegration,
                    ServerObservableSeverity.Debug,
                    "Client started applying server projection.",
                    sessionId,
                    payload);
                if (domain == ProjectionDomain.Presence)
                {
                    Emit(
                        ServerObservableEventType.PresenceRepresentationStarted,
                        ServerObservableComponent.ClientIntegration,
                        ServerObservableSeverity.Debug,
                        "Client started applying presence projection.",
                        sessionId,
                        payload);
                }
                else if (domain == ProjectionDomain.WorldInitialization)
                {
                    Emit(
                        ServerObservableEventType.WorldInitializationStarted,
                        ServerObservableComponent.ClientIntegration,
                        ServerObservableSeverity.Information,
                        "World initialization projection started.",
                        sessionId,
                        payload);
                }
                return;

            case ProjectionApplyStatus.Applied:
                Emit(
                    ServerObservableEventType.StateApplySucceeded,
                    ServerObservableComponent.ClientIntegration,
                    ServerObservableSeverity.Information,
                    "Client applied server projection.",
                    sessionId,
                    payload);
                if (domain == ProjectionDomain.WorldInitialization)
                {
                    MarkWorldInitializationReady(sessionId, isReady: true);
                    Emit(
                        ServerObservableEventType.WorldInitializationCompleted,
                        ServerObservableComponent.ClientIntegration,
                        ServerObservableSeverity.Information,
                        "World initialization completed.",
                        sessionId,
                        payload);
                    _ = ProjectPresenceSnapshotToAllReadyAsync("world_init_completed", sessionId);
                }
                break;

            case ProjectionApplyStatus.PartiallyApplied:
                Emit(
                    ServerObservableEventType.StateApplyPartial,
                    ServerObservableComponent.ClientIntegration,
                    ServerObservableSeverity.Warning,
                    "Client partially applied server projection.",
                    sessionId,
                    payload);
                Emit(
                    ServerObservableEventType.StateDesyncDetected,
                    ServerObservableComponent.ClientIntegration,
                    ServerObservableSeverity.Warning,
                    "Projection partial apply indicates canonical/local mismatch.",
                    sessionId,
                    payload);
                if (domain == ProjectionDomain.Presence)
                {
                    Emit(
                        ServerObservableEventType.PresenceRepresentationPartial,
                        ServerObservableComponent.ClientIntegration,
                        ServerObservableSeverity.Warning,
                        "Client partially applied presence projection.",
                        sessionId,
                        payload);
                    Emit(
                        ServerObservableEventType.PresenceDesyncDetected,
                        ServerObservableComponent.ClientIntegration,
                        ServerObservableSeverity.Warning,
                        "Presence projection partial apply indicates mismatch.",
                        sessionId,
                        payload);
                }
                else if (domain == ProjectionDomain.WorldInitialization)
                {
                    MarkWorldInitializationReady(sessionId, isReady: true);
                    _ = ProjectPresenceSnapshotToAllReadyAsync("world_init_partial", sessionId);
                }
                break;

            case ProjectionApplyStatus.NotApplied:
            case ProjectionApplyStatus.Failed:
                Emit(
                    ServerObservableEventType.StateApplyFailed,
                    ServerObservableComponent.ClientIntegration,
                    ServerObservableSeverity.Warning,
                    "Client failed to apply server projection.",
                    sessionId,
                    payload);
                if (domain == ProjectionDomain.WorldInitialization)
                {
                    MarkWorldInitializationReady(sessionId, isReady: false);
                    Emit(
                        ServerObservableEventType.WorldInitializationFailed,
                        ServerObservableComponent.ClientIntegration,
                        ServerObservableSeverity.Warning,
                        "World initialization failed.",
                        sessionId,
                        payload);
                    EmitPresenceBlockedByWorldInit(sessionId, "world_init_failed");
                    _ = ProjectPresenceSnapshotToAllReadyAsync("world_init_failed", sessionId);
                }
                Emit(
                    ServerObservableEventType.StateDesyncDetected,
                    ServerObservableComponent.ClientIntegration,
                    ServerObservableSeverity.Warning,
                    "Projection apply failed and generated a desync incident.",
                    sessionId,
                    payload);
                if (domain == ProjectionDomain.Presence)
                {
                    Emit(
                        ServerObservableEventType.PresenceRepresentationFailed,
                        ServerObservableComponent.ClientIntegration,
                        ServerObservableSeverity.Warning,
                        "Client failed to apply presence projection.",
                        sessionId,
                        payload);
                    Emit(
                        ServerObservableEventType.PresenceDesyncDetected,
                        ServerObservableComponent.ClientIntegration,
                        ServerObservableSeverity.Warning,
                        "Presence projection apply failed and generated a desync incident.",
                        sessionId,
                        payload);
                }
                break;
        }

        PendingStateProjection? pending = null;
        lock (_lock)
        {
            if (_pendingProjections.TryGetValue(projectionId, out var found))
            {
                pending = found;
                _pendingProjections.Remove(projectionId);
            }
        }

        if (pending is null)
            return;

        if (domain == ProjectionDomain.Presence
            && status == ProjectionApplyStatus.Applied
            && pending.Attempt > 0)
        {
            Emit(
                ServerObservableEventType.PresenceDesyncCorrected,
                ServerObservableComponent.ClientIntegration,
                ServerObservableSeverity.Information,
                "Presence projection desync corrected after retry.",
                sessionId,
                payload);
        }

        if ((status is ProjectionApplyStatus.Failed or ProjectionApplyStatus.NotApplied)
            && pending.Attempt < pending.MaxRetries)
        {
            ScheduleProjectionRetry(pending, status, detailsJson);
            return;
        }

        if (pending.Domain == ProjectionDomain.WorldInitialization
            && _worldInitBlockOnCriticalFailure
            && (status is ProjectionApplyStatus.Failed or ProjectionApplyStatus.NotApplied))
        {
            Emit(
                ServerObservableEventType.WorldInitializationBlocked,
                ServerObservableComponent.ClientIntegration,
                ServerObservableSeverity.Warning,
                "World initialization failed after retries; session will be blocked.",
                sessionId,
                payload);

            ClientSession? client;
            lock (_lock)
                _clientsBySessionId.TryGetValue(sessionId, out client);

            if (client is not null)
            {
                SendAdministrativeInvalidationProjection(
                    client,
                    sessionId,
                    reasonCode: "world_init_failed",
                    message: "World initialization failed. Session blocked until local state is recovered.",
                    accessDenied: true,
                    isBanned: false,
                    kicked: true);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100);
                        client.RequestClose(ServerSessionCloseReason.AuthenticationRejected);
                    }
                    catch
                    {
                    }
                });
            }
        }
    }

    public async Task<IdentityBanApplyResult> ApplyIdentityBanAsync(
        IdentityBanApplyRequest request,
        CancellationToken ct = default)
    {
        var result = await _identityBanService.ApplyBanAsync(request, null, ct);
        if (!result.Applied || string.IsNullOrWhiteSpace(request.IdentityId))
            return result;

        if (_sessionBackend.TryGetSessionIdByIdentity(request.IdentityId, out var activeSessionId))
        {
            ClientSession? client = null;
            lock (_lock)
            {
                _clientsBySessionId.TryGetValue(activeSessionId, out client);
            }

            if (client is not null)
            {
                SendAdministrativeInvalidationProjection(
                    client,
                    activeSessionId,
                    reasonCode: "ban",
                    message: request.Summary ?? "You are banned from this server.",
                    accessDenied: true,
                    isBanned: true,
                    kicked: true);
                await Task.Delay(100, ct);
                client.RequestClose(ServerSessionCloseReason.AuthenticationRejected);
                Emit(
                    ServerObservableEventType.BanSessionKicked,
                    ServerObservableComponent.Session,
                    ServerObservableSeverity.Warning,
                    "Connected identity was kicked because a ban was applied.",
                    activeSessionId,
                    payload: new Dictionary<string, object?>
                    {
                        ["identity_id"] = request.IdentityId,
                        ["ban_id"] = result.Ban?.BanId,
                    });
            }
        }

        return result;
    }

    public Task<IdentityBanRevocationResult> RevokeIdentityBanAsync(
        IdentityBanRevocationRequest request,
        CancellationToken ct = default)
    {
        return _identityBanService.RevokeActiveBanAsync(request, null, ct);
    }

    private void SendStateProjection(
        ClientSession client,
        Guid sessionId,
        ProjectionDomain domain,
        ProjectionApplicability applicability,
        object payload,
        bool retryable,
        int maxRetries = 1)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        uint projectionId;
        lock (_lock)
        {
            _projectionCounter++;
            if (_projectionCounter == 0)
                _projectionCounter++;
            projectionId = _projectionCounter;
            if (retryable)
            {
                _pendingProjections[projectionId] = new PendingStateProjection(
                    projectionId,
                    sessionId,
                    domain,
                    applicability,
                    json,
                    Attempt: 0,
                    MaxRetries: Math.Max(0, maxRetries));
            }
        }

        client.EnqueueRaw(PacketWriter.StateProjection(
            projectionId,
            (byte)domain,
            (byte)applicability,
            json));

        Emit(
            ServerObservableEventType.StateProjected,
            ServerObservableComponent.ClientIntegration,
            ServerObservableSeverity.Information,
            "Server projected canonical state to client.",
            sessionId,
            payload: new Dictionary<string, object?>
            {
                ["projection_id"] = projectionId,
                ["domain"] = domain.ToString(),
                ["applicability"] = applicability.ToString(),
                ["retryable"] = retryable,
            });
    }

    private void SendAdministrativeInvalidationProjection(
        ClientSession client,
        Guid sessionId,
        string reasonCode,
        string message,
        bool accessDenied,
        bool isBanned,
        bool kicked)
    {
        SendStateProjection(
            client,
            sessionId,
            ProjectionDomain.Administrative,
            ProjectionApplicability.Direct,
            new
            {
                reasonCode,
                message,
                accessDenied,
                isBanned,
                kicked,
            },
            retryable: false);
    }

    private void ScheduleProjectionRetry(
        PendingStateProjection pending,
        ProjectionApplyStatus failureStatus,
        string detailsJson)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (pending.Attempt + 1)));

                ClientSession? client;
                lock (_lock)
                    _clientsBySessionId.TryGetValue(pending.SessionId, out client);

                if (client is null || !client.IsReady)
                    return;

                var retried = pending with { Attempt = pending.Attempt + 1 };
                lock (_lock)
                    _pendingProjections[pending.ProjectionId] = retried;

                client.EnqueueRaw(PacketWriter.StateProjection(
                    retried.ProjectionId,
                    (byte)retried.Domain,
                    (byte)retried.Applicability,
                    retried.JsonPayload));

                Emit(
                    ServerObservableEventType.StateProjected,
                    ServerObservableComponent.ClientIntegration,
                    ServerObservableSeverity.Warning,
                    "Retrying state projection after failed apply attempt.",
                    retried.SessionId,
                    payload: new Dictionary<string, object?>
                    {
                        ["projection_id"] = retried.ProjectionId,
                        ["domain"] = retried.Domain.ToString(),
                        ["attempt"] = retried.Attempt,
                        ["max_retries"] = retried.MaxRetries,
                        ["previous_status"] = failureStatus.ToString(),
                        ["previous_details"] = detailsJson,
                    });
            }
            catch (Exception ex)
            {
                EmitBackendError(
                    pending.SessionId,
                    "Projection retry scheduling failed.",
                    ex);
            }
        });
    }

    private void RemovePendingProjectionsForSession(Guid sessionId)
    {
        lock (_lock)
        {
            var stale = _pendingProjections
                .Where(x => x.Value.SessionId == sessionId)
                .Select(x => x.Key)
                .ToArray();
            foreach (var projectionId in stale)
                _pendingProjections.Remove(projectionId);
        }
    }

    private void MarkWorldInitializationReady(Guid sessionId, bool isReady)
    {
        lock (_lock)
        {
            if (isReady)
            {
                _presenceWorldReadySessions.Add(sessionId);
            }
            else
            {
                _presenceWorldReadySessions.Remove(sessionId);
            }
        }
    }

    private void EmitPresenceBlockedByWorldInit(Guid sessionId, string reasonCode)
    {
        Emit(
            ServerObservableEventType.PresenceDesyncDetected,
            ServerObservableComponent.ClientIntegration,
            ServerObservableSeverity.Warning,
            "Presence projection blocked because world initialization is incomplete.",
            sessionId,
            new Dictionary<string, object?>
            {
                ["reason_code"] = reasonCode,
                ["world_init_required"] = _worldInitEnabled,
            });
    }

    private void HandleCharacterLifecycleOnSessionClose(Guid sessionId, ServerSessionCloseReason reason)
    {
        var saveReason = reason switch
        {
            ServerSessionCloseReason.NetworkDisconnect => CharacterLifecycleSaveReason.NetworkDisconnect,
            ServerSessionCloseReason.Timeout => CharacterLifecycleSaveReason.SessionTimeout,
            ServerSessionCloseReason.Shutdown => CharacterLifecycleSaveReason.Shutdown,
            _ => CharacterLifecycleSaveReason.SessionClosed,
        };

        try
        {
            _characterRespawnService
                .SaveAndUnloadSessionAsync(sessionId, saveReason)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.RespawnSaveFailed,
                ServerObservableComponent.Persistence,
                ServerObservableSeverity.Error,
                "Respawn lifecycle save on session close failed.",
                sessionId,
                payload: new Dictionary<string, object?> { ["exception"] = ex.Message });
        }

        try
        {
            _characterCurrencyService
                .SaveAndUnloadSessionAsync(sessionId, saveReason)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.CurrencySaveFailed,
                ServerObservableComponent.Persistence,
                ServerObservableSeverity.Error,
                "Currency save on session close failed.",
                sessionId,
                payload: new Dictionary<string, object?> { ["exception"] = ex.Message });
        }

        try
        {
            _characterInventoryService
                .SaveAndUnloadSessionAsync(sessionId, saveReason)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.InventorySaveFailed,
                ServerObservableComponent.Persistence,
                ServerObservableSeverity.Error,
                "Inventory save on session close failed.",
                sessionId,
                payload: new Dictionary<string, object?> { ["exception"] = ex.Message });
        }

        try
        {
            _characterLifecycleService
                .SaveAndUnloadSessionAsync(sessionId, saveReason)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.CharacterSaveFailed,
                ServerObservableComponent.Persistence,
                ServerObservableSeverity.Error,
                "Character save on session close failed.",
                sessionId,
                payload: new Dictionary<string, object?> { ["exception"] = ex.Message });
        }
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

    private static string ExtractChatText(byte[] jsonPayload)
    {
        var raw = Encoding.UTF8.GetString(jsonPayload);
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return raw;

            if (root.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String)
                return textNode.GetString() ?? string.Empty;

            if (root.TryGetProperty("message", out var messageNode) && messageNode.ValueKind == JsonValueKind.String)
                return messageNode.GetString() ?? string.Empty;

            return raw;
        }
        catch
        {
            return raw;
        }
    }

    private ChatCommand ParseChatCommand(string text)
    {
        if (!text.StartsWith('/'))
            return new ChatCommand(ChatCommandKind.LocalNormal, ChatScope.Proximity, ChatProximityMode.Normal, "local", text, null, AllowedWhileUnconscious: false, RequiresSpatialContext: true);

        var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return ChatCommand.Invalid();

        var cmd = parts[0].Trim().ToLowerInvariant();
        switch (cmd)
        {
            case "/ooc":
            {
                var content = parts.Length >= 2 ? text[(parts[0].Length + 1)..].Trim() : string.Empty;
                return string.IsNullOrWhiteSpace(content)
                    ? ChatCommand.Invalid()
                    : new ChatCommand(ChatCommandKind.Ooc, ChatScope.Global, ChatProximityMode.Normal, "global_ooc", content, null, AllowedWhileUnconscious: true, RequiresSpatialContext: false);
            }
            case "/me":
            {
                var content = parts.Length >= 2 ? text[(parts[0].Length + 1)..].Trim() : string.Empty;
                return string.IsNullOrWhiteSpace(content)
                    ? ChatCommand.Invalid()
                    : new ChatCommand(ChatCommandKind.Me, ChatScope.Proximity, ChatProximityMode.Normal, "rp_me", content, null, AllowedWhileUnconscious: true, RequiresSpatialContext: true);
            }
            case "/do":
            {
                var content = parts.Length >= 2 ? text[(parts[0].Length + 1)..].Trim() : string.Empty;
                return string.IsNullOrWhiteSpace(content)
                    ? ChatCommand.Invalid()
                    : new ChatCommand(ChatCommandKind.Do, ChatScope.Proximity, ChatProximityMode.Normal, "rp_do", content, null, AllowedWhileUnconscious: true, RequiresSpatialContext: true);
            }
            case "/try":
            {
                var content = parts.Length >= 2 ? text[(parts[0].Length + 1)..].Trim() : string.Empty;
                return string.IsNullOrWhiteSpace(content)
                    ? ChatCommand.Invalid()
                    : new ChatCommand(ChatCommandKind.Try, ChatScope.Proximity, ChatProximityMode.Normal, "rp_try", content, null, AllowedWhileUnconscious: true, RequiresSpatialContext: true);
            }
            case "/w":
            {
                if (parts.Length < 3)
                    return ChatCommand.Invalid();
                return new ChatCommand(ChatCommandKind.WhisperDirect, ChatScope.Direct, ChatProximityMode.Whisper, "direct_whisper", parts[2].Trim(), parts[1].Trim(), AllowedWhileUnconscious: false, RequiresSpatialContext: true);
            }
            case "/shout":
            case "/s":
            {
                var content = parts.Length >= 2 ? text[(parts[0].Length + 1)..].Trim() : string.Empty;
                return string.IsNullOrWhiteSpace(content)
                    ? ChatCommand.Invalid()
                    : new ChatCommand(ChatCommandKind.LocalShout, ChatScope.Proximity, ChatProximityMode.Shout, "local_shout", content, null, AllowedWhileUnconscious: false, RequiresSpatialContext: true);
            }
            case "/whisper":
            {
                var content = parts.Length >= 2 ? text[(parts[0].Length + 1)..].Trim() : string.Empty;
                return string.IsNullOrWhiteSpace(content)
                    ? ChatCommand.Invalid()
                    : new ChatCommand(ChatCommandKind.LocalWhisper, ChatScope.Proximity, ChatProximityMode.Whisper, "local_whisper", content, null, AllowedWhileUnconscious: false, RequiresSpatialContext: true);
            }
            case "/sys":
            {
                var content = parts.Length >= 2 ? text[(parts[0].Length + 1)..].Trim() : string.Empty;
                return string.IsNullOrWhiteSpace(content)
                    ? ChatCommand.Invalid()
                    : new ChatCommand(ChatCommandKind.System, ChatScope.System, ChatProximityMode.Normal, "system", content, null, AllowedWhileUnconscious: true, RequiresSpatialContext: false);
            }
            default:
                return ChatCommand.Invalid();
        }
    }

    private bool TryConsumeChatRateToken(Guid sessionId, DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            if (!_chatRateBuckets.TryGetValue(sessionId, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _chatRateBuckets[sessionId] = queue;
            }

            var threshold = nowUtc - _communicationOptions.RateLimitWindow;
            while (queue.Count > 0 && queue.Peek() < threshold)
                queue.Dequeue();

            if (queue.Count >= _communicationOptions.RateLimitMaxMessages)
                return false;

            queue.Enqueue(nowUtc);
            return true;
        }
    }

    private PresenceSpatialSnapshot? TryGetSpatial(Guid sessionId)
    {
        lock (_lock)
        {
            _presenceSpatialBySession.TryGetValue(sessionId, out var spatial);
            return spatial;
        }
    }

    private IReadOnlyList<ClientSession> ResolveProximityRecipients(
        ClientSession source,
        PresenceSpatialSnapshot senderSpatial,
        ChatProximityMode mode)
    {
        var radius = mode switch
        {
            ChatProximityMode.Whisper => _communicationOptions.ProximityWhisperRadius,
            ChatProximityMode.Shout => _communicationOptions.ProximityShoutRadius,
            _ => _communicationOptions.ProximityNormalRadius,
        };

        var senderZone = ComputeZoneKey(senderSpatial.X, senderSpatial.Y);
        List<ClientSession> candidates;
        Dictionary<Guid, PresenceSpatialSnapshot> spatialBySession;
        lock (_lock)
        {
            candidates = [.. _clients.Where(c => c.IsReady)];
            spatialBySession = new Dictionary<Guid, PresenceSpatialSnapshot>(_presenceSpatialBySession);
        }

        var recipients = new List<ClientSession>(capacity: candidates.Count);
        foreach (var client in candidates)
        {
            if (client.SessionId == source.SessionId)
            {
                recipients.Add(client);
                continue;
            }

            if (!spatialBySession.TryGetValue(client.SessionId, out var targetSpatial))
                continue;
            if (!string.Equals(senderZone, ComputeZoneKey(targetSpatial.X, targetSpatial.Y), StringComparison.Ordinal))
                continue;
            if (ComputeDistance(senderSpatial, targetSpatial) > radius)
                continue;

            recipients.Add(client);
        }

        return recipients;
    }

    private async Task<WhisperTarget?> ResolveWhisperTargetAsync(Guid senderSessionId, string targetHint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetHint))
            return null;

        List<ClientSession> candidates;
        lock (_lock)
            candidates = [.. _clients.Where(c => c.IsReady && c.SessionId != senderSessionId)];

        WhisperTarget? match = null;
        foreach (var candidate in candidates)
        {
            var session = _sessionBackend.GetActiveSessions().FirstOrDefault(x => x.SessionId == candidate.SessionId);
            if (session is null || string.IsNullOrWhiteSpace(session.CharacterId))
                continue;

            var character = await _characterProfileService.GetByInternalIdAsync(session.CharacterId!, ct);
            if (character is null)
                continue;

            if (!character.FullName.Contains(targetHint, StringComparison.OrdinalIgnoreCase)
                && !(candidate.Name?.Contains(targetHint, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            var spatial = TryGetSpatial(candidate.SessionId);
            if (match is not null)
                return null; // Ambiguous whisper target.

            match = new WhisperTarget(candidate, character.FullName, spatial);
        }

        return match;
    }

    private string ComputeZoneKey(float x, float y)
    {
        var cell = Math.Max(1f, _communicationOptions.ProximityZoneCellSize);
        var zx = (int)Math.Floor(x / cell);
        var zy = (int)Math.Floor(y / cell);
        return $"{zx}:{zy}";
    }

    private static double ComputeDistance(PresenceSpatialSnapshot a, PresenceSpatialSnapshot b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private bool RollTryOutcome()
    {
        lock (_chatRandom)
            return _chatRandom.Next(0, 100) >= 50;
    }

    private static string FormatChatLine(ChatCommand command, string senderName, string text, bool? tryOutcome)
    {
        return command.Kind switch
        {
            ChatCommandKind.Ooc => $"[OOC] {senderName}: {text}",
            ChatCommandKind.Me => $"* {senderName} {text}",
            ChatCommandKind.Do => $"[DO] {text} ({senderName})",
            ChatCommandKind.Try => $"[TRY] {senderName} {text} => {(tryOutcome == true ? "EXITO" : "FALLO")}",
            ChatCommandKind.LocalWhisper or ChatCommandKind.WhisperDirect => $"[Susurro] {senderName}: {text}",
            ChatCommandKind.LocalShout => $"[Grito] {senderName}: {text}",
            ChatCommandKind.System => $"[SYSTEM] {text}",
            _ => $"{senderName}: {text}",
        };
    }

    private async Task SendSystemChatAsync(ClientSession target, string text, string channel)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageId = $"sys_{Guid.NewGuid():N}",
            channel,
            scope = "system",
            mode = "normal",
            senderCharacterId = "system",
            senderCharacterName = "SYSTEM",
            senderIdentityId = "system",
            senderTransportClientId = 0,
            text,
            formatted = $"[SYSTEM] {text}",
            tryOutcome = (bool?)null,
            zone = (string?)null,
            timestampUtc = DateTimeOffset.UtcNow,
        });

        target.EnqueueRaw(PacketWriter.EventRelay(0, (ushort)EventType.ChatMessage, payload));
        await Task.CompletedTask;
    }

    private void EmitChatEvent(
        ServerObservableEventType eventType,
        Guid sessionId,
        byte sourceClientId,
        string channel,
        int? recipientCount,
        double? distance,
        string? text = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["transport_client_id"] = sourceClientId,
            ["channel"] = channel,
            ["recipient_count"] = recipientCount,
            ["distance"] = distance,
        };

        if (_communicationOptions.AuditIncludeMessageText && !string.IsNullOrWhiteSpace(text))
            payload["text"] = text;

        Emit(
            eventType,
            ServerObservableComponent.Networking,
            eventType is ServerObservableEventType.ChatMessageRejected or ServerObservableEventType.ChatRateLimitTriggered or ServerObservableEventType.ChatInvalidChannel
                ? ServerObservableSeverity.Warning
                : ServerObservableSeverity.Information,
            "Server communication event.",
            sessionId,
            payload);
    }

    private sealed record PresenceSpatialSnapshot(
        float X,
        float Y,
        float Z,
        float RotZ,
        bool IsRiding,
        DateTimeOffset UpdatedAtUtc);

    private sealed record PresenceSnapshotEntry(
        Guid SessionId,
        byte TransportClientId,
        string IdentityId,
        string CharacterId,
        string DisplayName,
        string PresenceState,
        PresenceSpatialSnapshot? Spatial);

    private sealed record PendingStateProjection(
        uint ProjectionId,
        Guid SessionId,
        ProjectionDomain Domain,
        ProjectionApplicability Applicability,
        byte[] JsonPayload,
        int Attempt,
        int MaxRetries);

    private enum ChatCommandKind
    {
        Invalid = 0,
        LocalNormal = 1,
        LocalWhisper = 2,
        LocalShout = 3,
        Me = 4,
        Do = 5,
        Try = 6,
        Ooc = 7,
        WhisperDirect = 8,
        System = 9,
    }

    private enum ChatScope
    {
        Proximity = 0,
        Global = 1,
        Direct = 2,
        System = 3,
    }

    private enum ChatProximityMode
    {
        Normal = 0,
        Whisper = 1,
        Shout = 2,
    }

    private sealed record ChatCommand(
        ChatCommandKind Kind,
        ChatScope Scope,
        ChatProximityMode Mode,
        string Channel,
        string Content,
        string? TargetHint,
        bool AllowedWhileUnconscious,
        bool RequiresSpatialContext)
    {
        public static ChatCommand Invalid()
            => new(ChatCommandKind.Invalid, ChatScope.Proximity, ChatProximityMode.Normal, "invalid", string.Empty, null, false, false);
    }

    private sealed record WhisperTarget(
        ClientSession Client,
        string CharacterName,
        PresenceSpatialSnapshot? Spatial);
}
