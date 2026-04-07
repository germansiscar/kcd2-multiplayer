using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.Bans;

public sealed class IdentityBanService : IIdentityBanService
{
    private readonly IJsonPersistenceStore _store;
    private readonly IServerObservabilitySink _observability;
    private readonly IdentityBanOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IdentityBanService(
        IJsonPersistenceStore store,
        IServerObservabilitySink observability,
        IdentityBanOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new IdentityBanOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<IdentityBanAccessEvaluationResult> EvaluateAccessAsync(
        Guid sessionId,
        string identityId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        var history = await LoadHistoryAsync(identityId, ct);
        var activeBan = await NormalizeAndPersistIfChangedAsync(history, sessionId, identityId, ct);
        if (activeBan is null)
            return new IdentityBanAccessEvaluationResult(false, null, null);

        var denialReason = GetUserFacingDenialReason(activeBan);
        Emit(
            ServerObservableEventType.BanAccessDenied,
            ServerObservableSeverity.Warning,
            "Access denied because identity has an active ban.",
            sessionId,
            identityId,
            payload: new Dictionary<string, object?>
            {
                ["ban_id"] = activeBan.BanId,
                ["ban_type"] = activeBan.Type.ToString(),
                ["reason_summary"] = denialReason,
            });
        return new IdentityBanAccessEvaluationResult(true, denialReason, CloneEntry(activeBan));
    }

    public async Task<IdentityBanApplyResult> ApplyBanAsync(
        IdentityBanApplyRequest request,
        Guid? sessionId = null,
        CancellationToken ct = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdentityId))
            return new IdentityBanApplyResult(false, "IdentityId is required.", null);
        if (string.IsNullOrWhiteSpace(request.Reason))
            return new IdentityBanApplyResult(false, "Ban reason is required.", null);
        if (string.IsNullOrWhiteSpace(request.ActorId))
            return new IdentityBanApplyResult(false, "ActorId is required.", null);
        if (request.Type == IdentityBanType.Temporary && (!request.Duration.HasValue || request.Duration.Value <= TimeSpan.Zero))
            return new IdentityBanApplyResult(false, "Temporary ban requires a positive duration.", null);

        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var history = await LoadHistoryUnsafeAsync(request.IdentityId, ct);
            var active = NormalizeHistoryUnsafe(history, now, request.IdentityId, sessionId);
            if (active is not null)
            {
                return new IdentityBanApplyResult(
                    false,
                    "Identity already has an active ban.",
                    CloneEntry(active));
            }

            var ban = new IdentityBanEntry
            {
                BanId = $"ban_{Guid.NewGuid():N}",
                IdentityId = request.IdentityId.Trim(),
                Type = request.Type,
                Status = IdentityBanStatus.Active,
                Reason = request.Reason.Trim(),
                ActorId = request.ActorId.Trim(),
                Summary = TrimOrNull(request.Summary),
                Notes = TrimOrNull(request.Notes),
                AuditEventIds = request.AuditEventIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [],
                CreatedAtUtc = now,
                ExpiresAtUtc = request.Type == IdentityBanType.Temporary ? now + request.Duration!.Value : null,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
            };

            history.Bans.Add(ban);
            history.ActiveBanId = ban.BanId;
            history.UpdatedAtUtc = now;

            await _store.SaveAsync(JsonPersistenceDomains.Bans, request.IdentityId, history, ct);
            Emit(
                ServerObservableEventType.BanApplied,
                ServerObservableSeverity.Warning,
                "Identity ban applied.",
                sessionId,
                request.IdentityId,
                payload: new Dictionary<string, object?>
                {
                    ["ban_id"] = ban.BanId,
                    ["ban_type"] = ban.Type.ToString(),
                    ["actor_id"] = ban.ActorId,
                    ["expires_at_utc"] = ban.ExpiresAtUtc,
                });
            return new IdentityBanApplyResult(true, null, CloneEntry(ban));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IdentityBanRevocationResult> RevokeActiveBanAsync(
        IdentityBanRevocationRequest request,
        Guid? sessionId = null,
        CancellationToken ct = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdentityId))
            return new IdentityBanRevocationResult(false, "IdentityId is required.", null);
        if (string.IsNullOrWhiteSpace(request.ActorId))
            return new IdentityBanRevocationResult(false, "ActorId is required.", null);
        if (string.IsNullOrWhiteSpace(request.RevocationReason))
            return new IdentityBanRevocationResult(false, "Revocation reason is required.", null);

        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var history = await LoadHistoryUnsafeAsync(request.IdentityId, ct);
            var active = NormalizeHistoryUnsafe(history, now, request.IdentityId, sessionId);
            if (active is null)
                return new IdentityBanRevocationResult(false, "Identity does not have an active ban.", null);

            active.Status = IdentityBanStatus.Revoked;
            active.RevokedAtUtc = now;
            active.RevokedByActorId = request.ActorId.Trim();
            active.RevocationReason = request.RevocationReason.Trim();
            history.ActiveBanId = null;
            history.UpdatedAtUtc = now;

            await _store.SaveAsync(JsonPersistenceDomains.Bans, request.IdentityId, history, ct);
            Emit(
                ServerObservableEventType.BanRevoked,
                ServerObservableSeverity.Information,
                "Identity ban revoked.",
                sessionId,
                request.IdentityId,
                payload: new Dictionary<string, object?>
                {
                    ["ban_id"] = active.BanId,
                    ["actor_id"] = active.RevokedByActorId,
                    ["revocation_reason"] = active.RevocationReason,
                });
            return new IdentityBanRevocationResult(true, null, CloneEntry(active));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IdentityBanEntry?> GetActiveBanAsync(string identityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        var history = await LoadHistoryAsync(identityId, ct);
        var active = await NormalizeAndPersistIfChangedAsync(history, null, identityId, ct);
        return active is null ? null : CloneEntry(active);
    }

    private async Task<IdentityBanHistoryRecord> LoadHistoryAsync(string identityId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoadHistoryUnsafeAsync(identityId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IdentityBanEntry?> NormalizeAndPersistIfChangedAsync(
        IdentityBanHistoryRecord history,
        Guid? sessionId,
        string identityId,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var active = NormalizeHistoryUnsafe(history, now, identityId, sessionId);
            if (history.UpdatedAtUtc == now)
                await _store.SaveAsync(JsonPersistenceDomains.Bans, identityId, history, ct);
            return active;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IdentityBanHistoryRecord> LoadHistoryUnsafeAsync(string identityId, CancellationToken ct)
    {
        var loaded = await _store.LoadAsync<IdentityBanHistoryRecord>(
            JsonPersistenceDomains.Bans,
            identityId,
            validate: ValidateHistoryRecord,
            ct);
        if (loaded is null)
        {
            return new IdentityBanHistoryRecord
            {
                ConfigVersion = "identity_bans_v1",
                IdentityId = identityId.Trim(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Bans = [],
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
            };
        }

        if (!string.Equals(loaded.IdentityId, identityId, StringComparison.Ordinal))
            throw new PersistenceValidationException($"Ban record identity mismatch for '{identityId}'.");

        return CloneHistory(loaded);
    }

    private IdentityBanEntry? NormalizeHistoryUnsafe(
        IdentityBanHistoryRecord history,
        DateTimeOffset now,
        string identityId,
        Guid? sessionId)
    {
        var changed = false;
        IdentityBanEntry? active = null;
        foreach (var ban in history.Bans)
        {
            if (ban.Type == IdentityBanType.Temporary
                && ban.Status == IdentityBanStatus.Active
                && ban.ExpiresAtUtc.HasValue
                && ban.ExpiresAtUtc.Value <= now)
            {
                ban.Status = IdentityBanStatus.Expired;
                ban.ExpiredAtUtc = now;
                changed = true;
                Emit(
                    ServerObservableEventType.BanExpired,
                    ServerObservableSeverity.Information,
                    "Temporary identity ban expired.",
                    sessionId,
                    identityId,
                    payload: new Dictionary<string, object?>
                    {
                        ["ban_id"] = ban.BanId,
                        ["expired_at_utc"] = now,
                    });
            }

            if (ban.Status == IdentityBanStatus.Active)
            {
                if (active is not null)
                    throw new PersistenceValidationException($"Identity '{identityId}' has multiple active bans.");
                active = ban;
            }
        }

        if (active is null && !string.IsNullOrWhiteSpace(history.ActiveBanId))
        {
            history.ActiveBanId = null;
            changed = true;
        }
        else if (active is not null && !string.Equals(history.ActiveBanId, active.BanId, StringComparison.Ordinal))
        {
            history.ActiveBanId = active.BanId;
            changed = true;
        }

        if (changed)
            history.UpdatedAtUtc = now;
        return active;
    }

    private string GetUserFacingDenialReason(IdentityBanEntry activeBan)
    {
        var preferred = string.IsNullOrWhiteSpace(activeBan.Summary)
            ? _options.UserFacingDeniedMessage
            : activeBan.Summary!.Trim();
        if (_options.MaxUserFacingMessageLength <= 0 || preferred.Length <= _options.MaxUserFacingMessageLength)
            return preferred;
        return preferred[.._options.MaxUserFacingMessageLength];
    }

    private static bool ValidateHistoryRecord(IdentityBanHistoryRecord history)
    {
        if (history is null)
            return false;
        if (string.IsNullOrWhiteSpace(history.ConfigVersion))
            return false;
        if (string.IsNullOrWhiteSpace(history.IdentityId))
            return false;
        if (history.Bans is null)
            return false;

        var activeCount = 0;
        foreach (var ban in history.Bans)
        {
            if (ban is null)
                return false;
            if (string.IsNullOrWhiteSpace(ban.BanId) || string.IsNullOrWhiteSpace(ban.IdentityId))
                return false;
            if (!string.Equals(ban.IdentityId, history.IdentityId, StringComparison.Ordinal))
                return false;
            if (string.IsNullOrWhiteSpace(ban.Reason) || string.IsNullOrWhiteSpace(ban.ActorId))
                return false;
            if (ban.Status == IdentityBanStatus.Active)
                activeCount++;
            if (ban.Type == IdentityBanType.Temporary && !ban.ExpiresAtUtc.HasValue && ban.Status == IdentityBanStatus.Active)
                return false;
        }

        if (activeCount > 1)
            return false;
        if (activeCount == 0 && !string.IsNullOrWhiteSpace(history.ActiveBanId))
            return false;
        if (activeCount == 1 && string.IsNullOrWhiteSpace(history.ActiveBanId))
            return false;
        return history.Metadata is not null;
    }

    private static IdentityBanHistoryRecord CloneHistory(IdentityBanHistoryRecord source)
    {
        return new IdentityBanHistoryRecord
        {
            ConfigVersion = source.ConfigVersion,
            IdentityId = source.IdentityId,
            ActiveBanId = source.ActiveBanId,
            Bans = source.Bans.Select(CloneEntry).ToList(),
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
        };
    }

    private static IdentityBanEntry CloneEntry(IdentityBanEntry source)
    {
        return new IdentityBanEntry
        {
            BanId = source.BanId,
            IdentityId = source.IdentityId,
            Type = source.Type,
            Status = source.Status,
            Reason = source.Reason,
            ActorId = source.ActorId,
            Summary = source.Summary,
            Notes = source.Notes,
            AuditEventIds = source.AuditEventIds.ToList(),
            CreatedAtUtc = source.CreatedAtUtc,
            ExpiresAtUtc = source.ExpiresAtUtc,
            ExpiredAtUtc = source.ExpiredAtUtc,
            RevokedAtUtc = source.RevokedAtUtc,
            RevokedByActorId = source.RevokedByActorId,
            RevocationReason = source.RevocationReason,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
        };
    }

    private static string? TrimOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        string message,
        Guid? sessionId = null,
        string? identityId = null,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        _observability.Emit(new ServerObservableEvent(
            Type: type,
            Component: ServerObservableComponent.Persistence,
            Severity: severity,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            SessionId: sessionId,
            IdentityId: identityId,
            Message: message,
            Payload: payload));
        _logger.Debug("[bans] {Message}", message);
    }
}
