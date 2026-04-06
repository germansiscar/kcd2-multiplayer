using System.Security.Cryptography;
using System.Text;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.Identity;

public sealed class PlayerIdentityService : IPlayerIdentityService
{
    private const string LookupIndexId = "identity_lookup_v1";

    private readonly IJsonPersistenceStore _store;
    private readonly IServerObservabilitySink _observability;
    private readonly ILogger _logger;
    private readonly PlayerIdentityOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlayerIdentityService(
        IJsonPersistenceStore store,
        IServerObservabilitySink observability,
        PlayerIdentityOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new PlayerIdentityOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<PlayerIdentityResolution> ResolveOrCreateAsync(PlayerIdentityClaim claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (string.IsNullOrWhiteSpace(claim.DisplayName))
            throw new ArgumentException("DisplayName cannot be empty.", nameof(claim));

        var (kind, rawExternalValue) = SelectExternalId(claim);
        var normalizedExternalValue = NormalizeExternalValue(kind, rawExternalValue);
        var externalLookupKey = BuildExternalLookupKey(kind, normalizedExternalValue);
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(ct);
        try
        {
            var index = await LoadLookupIndexAsync(ct);
            PlayerIdentityRecord? identity = null;
            var created = false;

            if (index.ExternalKeyToInternalId.TryGetValue(externalLookupKey, out var internalId))
                identity = await _store.LoadAsync<PlayerIdentityRecord>(JsonPersistenceDomains.Identity, internalId, ValidateIdentityRecord, ct);

            if (identity is null)
            {
                if (!_options.AutoCreateWhenMissing)
                {
                    Emit(
                        ServerObservableEventType.IdentityAccessDenied,
                        ServerObservableSeverity.Warning,
                        "Identity not found and auto-create is disabled.",
                        payload: new Dictionary<string, object?>
                        {
                            ["identification_kind"] = kind.ToString(),
                            ["identity_created"] = false,
                        });

                    return new PlayerIdentityResolution(
                        IsAllowed: false,
                        DenialReason: "Identity not found.",
                        Created: false,
                        Identity: null,
                        IdentificationKind: kind);
                }

                identity = CreateNewIdentity(claim.DisplayName.Trim(), kind, externalLookupKey, now);
                await _store.SaveAsync(JsonPersistenceDomains.Identity, identity.InternalId, identity, ct);
                index.ExternalKeyToInternalId[externalLookupKey] = identity.InternalId;
                index.UpdatedAtUtc = now;
                await _store.SaveAsync(JsonPersistenceDomains.Config, LookupIndexId, index, ct);
                created = true;

                Emit(
                    ServerObservableEventType.IdentityCreated,
                    ServerObservableSeverity.Information,
                    "Player identity created.",
                    identityId: identity.InternalId,
                    payload: new Dictionary<string, object?>
                    {
                        ["identification_kind"] = kind.ToString(),
                        ["status"] = identity.Status.ToString(),
                    });
            }
            else
            {
                identity.DisplayName = claim.DisplayName.Trim();
                identity.LastSeenAtUtc = now;
                identity.UpdatedAtUtc = now;
                await _store.SaveAsync(JsonPersistenceDomains.Identity, identity.InternalId, identity, ct);
            }

            if (identity.IsDeactivated || identity.Status == PlayerIdentityStatus.Blocked)
            {
                Emit(
                    ServerObservableEventType.IdentityAccessDenied,
                    ServerObservableSeverity.Warning,
                    "Blocked identity denied.",
                    identityId: identity.InternalId,
                    payload: new Dictionary<string, object?>
                    {
                        ["status"] = identity.Status.ToString(),
                        ["is_deactivated"] = identity.IsDeactivated,
                        ["identification_kind"] = kind.ToString(),
                    });

                return new PlayerIdentityResolution(
                    IsAllowed: false,
                    DenialReason: "Identity is blocked.",
                    Created: created,
                    Identity: identity,
                    IdentificationKind: kind);
            }

            if (_options.RequireWhitelistForPendingIdentity && identity.Status == PlayerIdentityStatus.Pending)
            {
                Emit(
                    ServerObservableEventType.IdentityAccessDenied,
                    ServerObservableSeverity.Warning,
                    "Pending identity denied by whitelist policy.",
                    identityId: identity.InternalId,
                    payload: new Dictionary<string, object?>
                    {
                        ["status"] = identity.Status.ToString(),
                        ["identification_kind"] = kind.ToString(),
                    });

                return new PlayerIdentityResolution(
                    IsAllowed: false,
                    DenialReason: "Identity is pending approval.",
                    Created: created,
                    Identity: identity,
                    IdentificationKind: kind);
            }

            Emit(
                ServerObservableEventType.IdentityResolved,
                ServerObservableSeverity.Information,
                "Player identity resolved.",
                identityId: identity.InternalId,
                payload: new Dictionary<string, object?>
                {
                    ["identification_kind"] = kind.ToString(),
                    ["identity_created"] = created,
                    ["status"] = identity.Status.ToString(),
                });

            return new PlayerIdentityResolution(
                IsAllowed: true,
                DenialReason: null,
                Created: created,
                Identity: identity,
                IdentificationKind: kind);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PlayerIdentityRecord?> GetByInternalIdAsync(string internalId, CancellationToken ct = default)
        => _store.LoadAsync<PlayerIdentityRecord>(JsonPersistenceDomains.Identity, internalId, ValidateIdentityRecord, ct);

    public async Task<IReadOnlyList<PlayerIdentityRecord>> ListAllAsync(CancellationToken ct = default)
    {
        var index = await LoadLookupIndexAsync(ct);
        var uniqueIds = index.ExternalKeyToInternalId
            .Values
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var output = new List<PlayerIdentityRecord>();
        foreach (var internalId in uniqueIds)
        {
            ct.ThrowIfCancellationRequested();
            var identity = await _store.LoadAsync<PlayerIdentityRecord>(JsonPersistenceDomains.Identity, internalId, ValidateIdentityRecord, ct);
            if (identity is not null)
                output.Add(identity);
        }

        return output
            .OrderBy(x => x.CreatedAtUtc)
            .ToArray();
    }

    public async Task<bool> TrySetStatusAsync(string internalId, PlayerIdentityStatus status, CancellationToken ct = default)
    {
        var changed = false;
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(ct);
        try
        {
            var identity = await _store.LoadAsync<PlayerIdentityRecord>(JsonPersistenceDomains.Identity, internalId, ValidateIdentityRecord, ct);
            if (identity is null)
                return false;

            if (identity.Status != status)
            {
                identity.Status = status;
                identity.UpdatedAtUtc = now;
                await _store.SaveAsync(JsonPersistenceDomains.Identity, identity.InternalId, identity, ct);
                changed = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Emit(
                ServerObservableEventType.IdentityStatusChanged,
                ServerObservableSeverity.Information,
                "Player identity status changed.",
                identityId: internalId,
                payload: new Dictionary<string, object?> { ["status"] = status.ToString() });
        }

        return true;
    }

    public async Task<bool> TryDeactivateAsync(string internalId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(ct);
        try
        {
            var identity = await _store.LoadAsync<PlayerIdentityRecord>(JsonPersistenceDomains.Identity, internalId, ValidateIdentityRecord, ct);
            if (identity is null)
                return false;

            if (!identity.IsDeactivated)
            {
                identity.IsDeactivated = true;
                identity.Status = PlayerIdentityStatus.Blocked;
                identity.UpdatedAtUtc = now;
                await _store.SaveAsync(JsonPersistenceDomains.Identity, identity.InternalId, identity, ct);
            }
        }
        finally
        {
            _gate.Release();
        }

        Emit(
            ServerObservableEventType.IdentityStatusChanged,
            ServerObservableSeverity.Information,
            "Player identity deactivated.",
            identityId: internalId,
            payload: new Dictionary<string, object?> { ["status"] = PlayerIdentityStatus.Blocked.ToString(), ["is_deactivated"] = true });

        return true;
    }

    private async Task<PlayerIdentityLookupIndex> LoadLookupIndexAsync(CancellationToken ct)
    {
        var index = await _store.LoadAsync<PlayerIdentityLookupIndex>(
            JsonPersistenceDomains.Config,
            LookupIndexId,
            x => x.ExternalKeyToInternalId is not null,
            ct);

        return index ?? new PlayerIdentityLookupIndex { UpdatedAtUtc = DateTimeOffset.UtcNow };
    }

    private PlayerIdentityRecord CreateNewIdentity(
        string displayName,
        PlayerIdentityExternalKind kind,
        string externalLookupKey,
        DateTimeOffset now)
    {
        var status = _options.RequireWhitelistForPendingIdentity
            ? _options.NewIdentityStatusWhenWhitelistEnabled
            : _options.NewIdentityStatusWhenWhitelistDisabled;

        return new PlayerIdentityRecord
        {
            InternalId = $"pid_{Guid.NewGuid():N}",
            Status = status,
            ExternalKind = kind,
            ExternalKeyHash = externalLookupKey,
            DisplayName = displayName,
            CharacterIds = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastSeenAtUtc = now,
            IsDeactivated = false,
        };
    }

    private static bool ValidateIdentityRecord(PlayerIdentityRecord identity)
    {
        return !string.IsNullOrWhiteSpace(identity.InternalId)
               && !string.IsNullOrWhiteSpace(identity.ExternalKeyHash)
               && !string.IsNullOrWhiteSpace(identity.DisplayName);
    }

    private static (PlayerIdentityExternalKind kind, string value) SelectExternalId(PlayerIdentityClaim claim)
    {
        if (!string.IsNullOrWhiteSpace(claim.SteamId))
            return (PlayerIdentityExternalKind.SteamId, claim.SteamId!);

        if (!string.IsNullOrWhiteSpace(claim.PersistentToken))
            return (PlayerIdentityExternalKind.PersistentToken, claim.PersistentToken!);

        return (PlayerIdentityExternalKind.PlayerNameFallback, claim.DisplayName);
    }

    private static string NormalizeExternalValue(PlayerIdentityExternalKind kind, string value)
    {
        var trimmed = value.Trim();
        return kind switch
        {
            PlayerIdentityExternalKind.SteamId => trimmed.ToLowerInvariant(),
            PlayerIdentityExternalKind.PlayerNameFallback => trimmed.ToLowerInvariant(),
            _ => trimmed,
        };
    }

    private static string BuildExternalLookupKey(PlayerIdentityExternalKind kind, string normalizedValue)
    {
        var raw = $"{kind}:{normalizedValue}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hash = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{kind}:{hash}";
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        string message,
        string? identityId = null,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        _observability.Emit(new ServerObservableEvent(
            Type: type,
            Component: ServerObservableComponent.Persistence,
            Severity: severity,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            IdentityId: identityId,
            Message: message,
            Payload: payload));
        _logger.Debug("[identity] {Message} identity_id={IdentityId}", message, identityId);
    }
}
