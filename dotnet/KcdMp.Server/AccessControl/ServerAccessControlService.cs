using KcdMp.Server.Identity;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.AccessControl;

public sealed class ServerAccessControlService : IServerAccessControlService
{
    private readonly IJsonPersistenceStore _store;
    private readonly IServerObservabilitySink _observability;
    private readonly ILogger _logger;
    private readonly ServerAccessControlOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServerAccessControlConfigurationRecord? _cachedConfiguration;

    public ServerAccessControlService(
        IJsonPersistenceStore store,
        IServerObservabilitySink observability,
        ServerAccessControlOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new ServerAccessControlOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<ServerAccessControlConfigurationRecord> GetActiveConfigurationAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cachedConfiguration is not null)
                return Clone(_cachedConfiguration);
        }
        finally
        {
            _gate.Release();
        }

        var createdDefault = false;
        ServerAccessControlConfigurationRecord loaded;
        try
        {
            loaded = await _store.LoadAsync<ServerAccessControlConfigurationRecord>(
                JsonPersistenceDomains.Config,
                _options.ConfigDocumentId,
                validate: ValidateConfiguration,
                ct) ?? CreateDefaultConfiguration();
            if (loaded.CreatedAtUtc == default)
                loaded.CreatedAtUtc = DateTimeOffset.UtcNow;
            loaded.UpdatedAtUtc = DateTimeOffset.UtcNow;
            createdDefault = !await ConfigurationExistsAsync(ct);
            if (createdDefault)
                await _store.SaveAsync(JsonPersistenceDomains.Config, _options.ConfigDocumentId, loaded, ct);
        }
        catch (Exception ex) when (ex is PersistenceValidationException or PersistenceLoadException)
        {
            Emit(
                ServerObservableEventType.AccessControlConfigValidationFailed,
                ServerObservableSeverity.Error,
                "Access control configuration is invalid.",
                payload: new Dictionary<string, object?>
                {
                    ["config_id"] = _options.ConfigDocumentId,
                    ["exception"] = ex.Message,
                });
            throw;
        }

        await _gate.WaitAsync(ct);
        try
        {
            _cachedConfiguration = Clone(loaded);
            return Clone(_cachedConfiguration);
        }
        finally
        {
            _gate.Release();
            Emit(
                ServerObservableEventType.AccessControlConfigLoaded,
                ServerObservableSeverity.Information,
                "Access control configuration loaded.",
                payload: new Dictionary<string, object?>
                {
                    ["config_id"] = _options.ConfigDocumentId,
                    ["created_default"] = createdDefault,
                    ["access_mode"] = loaded.AccessMode.ToString(),
                });
        }
    }

    public async Task<ServerAccessEvaluationResult> EvaluateIdentityAccessAsync(
        ServerAccessIdentityEvaluationRequest request,
        CancellationToken ct = default)
    {
        var config = await GetActiveConfigurationAsync(ct);

        if (request.IsDeactivated || request.IdentityStatus == PlayerIdentityStatus.Blocked)
        {
            EmitDenied(
                request.SessionId,
                request.IdentityId,
                ServerAccessDecisionReason.DeniedBlockedIdentity,
                "Identity is blocked.");
            return new ServerAccessEvaluationResult(false, ServerAccessDecisionReason.DeniedBlockedIdentity, "Identity is blocked.");
        }

        if (config.AccessMode == ServerAccessMode.Whitelist
            && config.DenyPendingIdentityInWhitelistMode
            && request.IdentityStatus == PlayerIdentityStatus.Pending)
        {
            var reason = request.IdentityCreatedInThisAttempt
                ? ServerAccessDecisionReason.DeniedNewIdentityInWhitelistMode
                : ServerAccessDecisionReason.DeniedPendingIdentityInWhitelistMode;
            var message = request.IdentityCreatedInThisAttempt
                ? "New identity is pending approval in whitelist mode."
                : "Identity is pending approval in whitelist mode.";
            EmitDenied(request.SessionId, request.IdentityId, reason, message);
            return new ServerAccessEvaluationResult(false, reason, message);
        }

        if (request.IdentityCreatedInThisAttempt && config.AccessMode == ServerAccessMode.Open)
        {
            EmitAllowed(
                request.SessionId,
                request.IdentityId,
                ServerAccessDecisionReason.AllowedNewIdentityInOpenMode,
                "New identity created in open mode.");
            return new ServerAccessEvaluationResult(true, ServerAccessDecisionReason.AllowedNewIdentityInOpenMode, null);
        }

        if (request.IdentityStatus == PlayerIdentityStatus.Pending && config.AccessMode == ServerAccessMode.Open)
        {
            EmitAllowed(
                request.SessionId,
                request.IdentityId,
                ServerAccessDecisionReason.AllowedPendingIdentityInOpenMode,
                "Pending identity allowed in open mode.");
            return new ServerAccessEvaluationResult(true, ServerAccessDecisionReason.AllowedPendingIdentityInOpenMode, null);
        }

        EmitAllowed(
            request.SessionId,
            request.IdentityId,
            ServerAccessDecisionReason.AllowedActiveIdentity,
            "Identity allowed by access policy.");
        return new ServerAccessEvaluationResult(true, ServerAccessDecisionReason.AllowedActiveIdentity, null);
    }

    public void EmitDuplicateActiveSessionDenied(Guid sessionId, string? identityId)
    {
        EmitDenied(
            sessionId,
            identityId,
            ServerAccessDecisionReason.DeniedDuplicateActiveSession,
            "Identity already has an active session.");
    }

    private async Task<bool> ConfigurationExistsAsync(CancellationToken ct)
    {
        var loaded = await _store.LoadAsync<ServerAccessControlConfigurationRecord>(
            JsonPersistenceDomains.Config,
            _options.ConfigDocumentId,
            validate: null,
            ct);
        return loaded is not null;
    }

    private ServerAccessControlConfigurationRecord CreateDefaultConfiguration()
    {
        var now = DateTimeOffset.UtcNow;
        return new ServerAccessControlConfigurationRecord
        {
            ConfigVersion = "access_control_v1",
            AccessMode = _options.DefaultAccessMode,
            DenyPendingIdentityInWhitelistMode = _options.DenyPendingIdentityInWhitelistMode,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static bool ValidateConfiguration(ServerAccessControlConfigurationRecord config)
    {
        if (config is null)
            return false;
        if (string.IsNullOrWhiteSpace(config.ConfigVersion))
            return false;
        if (!Enum.IsDefined(config.AccessMode))
            return false;
        return config.Metadata is not null;
    }

    private static ServerAccessControlConfigurationRecord Clone(ServerAccessControlConfigurationRecord source)
    {
        return new ServerAccessControlConfigurationRecord
        {
            ConfigVersion = source.ConfigVersion,
            AccessMode = source.AccessMode,
            DenyPendingIdentityInWhitelistMode = source.DenyPendingIdentityInWhitelistMode,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
        };
    }

    private void EmitAllowed(
        Guid sessionId,
        string? identityId,
        ServerAccessDecisionReason reason,
        string message)
    {
        Emit(
            ServerObservableEventType.AccessDecisionAllowed,
            ServerObservableSeverity.Information,
            message,
            sessionId,
            identityId,
            reason);
    }

    private void EmitDenied(
        Guid sessionId,
        string? identityId,
        ServerAccessDecisionReason reason,
        string message)
    {
        Emit(
            ServerObservableEventType.AccessDecisionDenied,
            ServerObservableSeverity.Warning,
            message,
            sessionId,
            identityId,
            reason);
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        string message,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        _observability.Emit(new ServerObservableEvent(
            Type: type,
            Component: ServerObservableComponent.Persistence,
            Severity: severity,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Message: message,
            Payload: payload));
        _logger.Debug("[access] {Message}", message);
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        string message,
        Guid? sessionId,
        string? identityId,
        ServerAccessDecisionReason reason,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        var mergedPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reason"] = reason.ToString(),
        };
        if (payload is not null)
        {
            foreach (var (k, v) in payload)
                mergedPayload[k] = v;
        }

        _observability.Emit(new ServerObservableEvent(
            Type: type,
            Component: ServerObservableComponent.Session,
            Severity: severity,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            SessionId: sessionId,
            IdentityId: identityId,
            Message: message,
            Payload: mergedPayload));
        _logger.Debug("[access] {Message} session_id={SessionId} identity_id={IdentityId} reason={Reason}",
            message,
            sessionId,
            identityId,
            reason);
    }
}
