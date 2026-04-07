using System.Net;
using System.Net.Sockets;
using KcdMp.Server.AccessControl;
using KcdMp.Server.Admin;
using KcdMp.Server.Audit;
using KcdMp.Server.Bans;
using KcdMp.Server.Characters;
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

    public bool TryKickSessionById(Guid sessionId)
    {
        ClientSession? client = null;
        lock (_lock)
            _clientsBySessionId.TryGetValue(sessionId, out client);

        if (client is null)
            return false;

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
}
