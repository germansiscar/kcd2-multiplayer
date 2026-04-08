using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.Crime;

public sealed class CrimeLawService : ICrimeLawService
{
    private readonly IJsonPersistenceStore _store;
    private readonly IServerObservabilitySink _observability;
    private readonly CrimeLawOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CrimeLawConfigurationRecord? _cachedConfiguration;

    public CrimeLawService(
        IJsonPersistenceStore store,
        IServerObservabilitySink observability,
        CrimeLawOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new CrimeLawOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<CrimeLawConfigurationRecord> GetActiveConfigurationAsync(CancellationToken ct = default)
    {
        var cached = _cachedConfiguration;
        if (cached is not null)
            return CloneConfig(cached);

        await _gate.WaitAsync(ct);
        try
        {
            if (_cachedConfiguration is not null)
                return CloneConfig(_cachedConfiguration);

            var loaded = await _store.LoadAsync<CrimeLawConfigurationRecord>(
                JsonPersistenceDomains.Config,
                _options.ConfigId,
                ValidateConfig,
                ct);

            if (loaded is null)
            {
                loaded = CreateDefaultConfiguration();
                await _store.SaveAsync(JsonPersistenceDomains.Config, _options.ConfigId, loaded, ct);
            }

            _cachedConfiguration = CloneConfig(loaded);
            return CloneConfig(_cachedConfiguration);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CharacterCrimeRecord?> GetCharacterRecordAsync(
        string characterId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        var loaded = await _store.LoadAsync<CharacterCrimeRecord>(
            JsonPersistenceDomains.Crime,
            characterId,
            ValidateRecord,
            ct);
        if (loaded is null)
            return null;

        var config = await GetActiveConfigurationAsync(ct);
        var normalized = NormalizeAndExpire(loaded, config, DateTimeOffset.UtcNow);
        if (!ReferenceEquals(loaded, normalized))
            await SaveRecordAsync(normalized, ct);

        return CloneRecord(normalized);
    }

    public Task<CrimeRegistrationResult> RegisterAutomaticCrimeAsync(
        AutomaticCrimeRegistrationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RegisterCoreAsync(
            sessionId: request.SessionId,
            actorIdentityId: null,
            request.IdentityId,
            request.CharacterId,
            request.CrimeType,
            CrimeSource.Automatic,
            request.TargetKind,
            request.TargetId,
            request.TargetIdentityId,
            request.TargetCharacterId,
            request.ActionCode,
            request.Reason,
            request.Metadata,
            ct);
    }

    public Task<CrimeRegistrationResult> RegisterManualCrimeAsync(
        ManualCrimeRegistrationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RegisterCoreAsync(
            sessionId: request.SessionId,
            actorIdentityId: request.ActorIdentityId,
            request.IdentityId,
            request.CharacterId,
            request.CrimeType,
            CrimeSource.Manual,
            request.TargetKind,
            request.TargetId,
            request.TargetIdentityId,
            request.TargetCharacterId,
            request.ActionCode,
            request.Reason,
            request.Metadata,
            ct);
    }

    public async Task<CrimeRegistrationResult> RegisterIllegalActionIfConfiguredAsync(
        IllegalActionCrimeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = await GetActiveConfigurationAsync(ct);
        var normalizedCode = request.ActionCode.Trim();
        if (config.ConfiguredIllegalActions.All(x => !string.Equals(x, normalizedCode, StringComparison.OrdinalIgnoreCase)))
            return new CrimeRegistrationResult(false, "Illegal action is not configured.", null, null, false, null);

        return await RegisterCoreAsync(
            request.SessionId,
            actorIdentityId: null,
            request.IdentityId,
            request.CharacterId,
            CrimeType.ConfiguredIllegalAction,
            CrimeSource.Automatic,
            request.TargetKind,
            request.TargetId,
            request.TargetIdentityId,
            request.TargetCharacterId,
            normalizedCode,
            request.Reason,
            request.Metadata,
            ct);
    }

    public async Task<CrimeStateChangeResult> ClearCharacterStateAsync(
        CrimeStateClearRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorIdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CharacterId);

        var config = await GetActiveConfigurationAsync(ct);
        var record = await LoadOrCreateRecordAsync(request.IdentityId, request.CharacterId, ct);
        var now = DateTimeOffset.UtcNow;
        var normalized = NormalizeAndExpire(record, config, now);
        var statusChanged = normalized.Status != CharacterCrimeStatus.Clean || normalized.WantedUntilUtc is not null;

        normalized.Status = CharacterCrimeStatus.Clean;
        normalized.WantedUntilUtc = null;
        normalized.UpdatedAtUtc = now;
        MergeMetadata(normalized.Metadata, request.Metadata);
        if (!string.IsNullOrWhiteSpace(request.Reason))
            normalized.Metadata["state_clear_reason"] = request.Reason.Trim();
        normalized.Metadata["last_state_cleared_by_actor_id"] = request.ActorIdentityId.Trim();

        try
        {
            await SaveRecordAsync(normalized, ct);
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.CrimeRegistrationFailed,
                ServerObservableSeverity.Error,
                "Crime state clear failed.",
                request.SessionId,
                request.IdentityId,
                request.CharacterId,
                new Dictionary<string, object?>
                {
                    ["actor_id"] = request.ActorIdentityId,
                    ["reason"] = request.Reason,
                    ["exception"] = ex.Message,
                });
            return new CrimeStateChangeResult(false, "Crime state could not be cleared.", null, false, null);
        }

        Emit(
            ServerObservableEventType.CrimeAdminAction,
            ServerObservableSeverity.Information,
            "Crime state manually cleared.",
            request.SessionId,
            request.IdentityId,
            request.CharacterId,
            new Dictionary<string, object?>
            {
                ["actor_id"] = request.ActorIdentityId,
                ["action"] = "clear_state",
                ["reason"] = request.Reason,
            });

        if (statusChanged)
        {
            Emit(
                ServerObservableEventType.CrimeStateUpdated,
                ServerObservableSeverity.Information,
                "Crime status updated.",
                request.SessionId,
                request.IdentityId,
                request.CharacterId,
                new Dictionary<string, object?>
                {
                    ["status"] = normalized.Status.ToString(),
                    ["wanted_until_utc"] = normalized.WantedUntilUtc,
                });
        }

        return new CrimeStateChangeResult(
            true,
            null,
            CloneRecord(normalized),
            statusChanged,
            statusChanged ? "Tu estado criminal ha sido limpiado por administración." : null);
    }

    private async Task<CrimeRegistrationResult> RegisterCoreAsync(
        Guid? sessionId,
        string? actorIdentityId,
        string identityId,
        string characterId,
        CrimeType crimeType,
        CrimeSource source,
        CrimeTargetKind targetKind,
        string? targetId,
        string? targetIdentityId,
        string? targetCharacterId,
        string? actionCode,
        string? reason,
        IReadOnlyDictionary<string, object?>? metadata,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        var config = await GetActiveConfigurationAsync(ct);
        if (!IsCrimeTypeEnabled(config, crimeType))
            return new CrimeRegistrationResult(false, "Crime type is disabled by configuration.", null, null, false, null);

        var record = await LoadOrCreateRecordAsync(identityId, characterId, ct);
        var now = DateTimeOffset.UtcNow;
        var normalized = NormalizeAndExpire(record, config, now);
        var fingerprint = BuildFingerprint(crimeType, source, targetKind, targetId, targetCharacterId, actionCode);
        var dedupWindow = TimeSpan.FromSeconds(Math.Max(0, config.DedupWindowSeconds));
        if (IsDuplicate(normalized, fingerprint, now, dedupWindow))
        {
            Emit(
                ServerObservableEventType.CrimeDetected,
                ServerObservableSeverity.Warning,
                "Duplicate crime event ignored.",
                sessionId,
                identityId,
                characterId,
                new Dictionary<string, object?>
                {
                    ["crime_type"] = crimeType.ToString(),
                    ["source"] = source.ToString(),
                    ["target_kind"] = targetKind.ToString(),
                    ["target_id"] = targetId,
                    ["dedup_window_seconds"] = config.DedupWindowSeconds,
                });
            return new CrimeRegistrationResult(false, "Duplicate crime ignored.", CloneRecord(normalized), null, false, null);
        }

        Emit(
            ServerObservableEventType.CrimeDetected,
            ServerObservableSeverity.Information,
            "Crime detected.",
            sessionId,
            identityId,
            characterId,
            new Dictionary<string, object?>
            {
                ["crime_type"] = crimeType.ToString(),
                ["source"] = source.ToString(),
                ["target_kind"] = targetKind.ToString(),
                ["target_id"] = targetId,
                ["target_identity_id"] = targetIdentityId,
                ["target_character_id"] = targetCharacterId,
                ["action_code"] = actionCode,
            });

        var wasWanted = normalized.Status == CharacterCrimeStatus.Wanted && (!normalized.WantedUntilUtc.HasValue || normalized.WantedUntilUtc.Value > now);
        var wantedUntil = now.AddSeconds(Math.Max(1, config.WantedDurationSeconds));
        var crimeEvent = new CrimeEventRecord
        {
            CrimeEventId = $"crime_evt_{Guid.NewGuid():N}",
            CrimeType = crimeType,
            Source = source,
            TargetKind = targetKind,
            TargetId = targetId?.Trim(),
            TargetIdentityId = targetIdentityId?.Trim(),
            TargetCharacterId = targetCharacterId?.Trim(),
            ActionCode = actionCode?.Trim(),
            Reason = reason?.Trim(),
            Fingerprint = fingerprint,
            OccurredAtUtc = now,
            Metadata = ToStringMetadata(metadata),
        };

        normalized.Events.Add(crimeEvent);
        var maxEvents = Math.Max(1, config.MaxEventsPerCharacter);
        if (normalized.Events.Count > maxEvents)
            normalized.Events = normalized.Events.Skip(normalized.Events.Count - maxEvents).ToList();
        normalized.TotalCrimeCount = checked(normalized.TotalCrimeCount + 1);
        normalized.LastCrimeAtUtc = now;
        normalized.Status = CharacterCrimeStatus.Wanted;
        normalized.WantedUntilUtc = wantedUntil;
        normalized.UpdatedAtUtc = now;
        if (!string.IsNullOrWhiteSpace(actorIdentityId))
            normalized.Metadata["last_manual_actor_id"] = actorIdentityId.Trim();
        MergeMetadata(normalized.Metadata, metadata);

        try
        {
            await SaveRecordAsync(normalized, ct);
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.CrimeRegistrationFailed,
                ServerObservableSeverity.Error,
                "Crime registration failed.",
                sessionId,
                identityId,
                characterId,
                new Dictionary<string, object?>
                {
                    ["crime_type"] = crimeType.ToString(),
                    ["exception"] = ex.Message,
                });
            return new CrimeRegistrationResult(false, "Crime could not be persisted.", null, null, false, null);
        }

        Emit(
            ServerObservableEventType.CrimeRegistered,
            ServerObservableSeverity.Information,
            "Crime registered.",
            sessionId,
            identityId,
            characterId,
            new Dictionary<string, object?>
            {
                ["crime_event_id"] = crimeEvent.CrimeEventId,
                ["crime_type"] = crimeType.ToString(),
                ["source"] = source.ToString(),
                ["target_kind"] = targetKind.ToString(),
                ["target_id"] = targetId,
            });

        var statusChanged = !wasWanted;
        Emit(
            source == CrimeSource.Manual ? ServerObservableEventType.CrimeAdminAction : ServerObservableEventType.CrimeStateUpdated,
            ServerObservableSeverity.Information,
            source == CrimeSource.Manual ? "Crime manually marked by staff." : "Crime status updated.",
            sessionId,
            identityId,
            characterId,
            new Dictionary<string, object?>
            {
                ["status"] = normalized.Status.ToString(),
                ["wanted_until_utc"] = normalized.WantedUntilUtc,
                ["actor_id"] = actorIdentityId,
            });

        var feedback = statusChanged
            ? "Has cometido un crimen. Estado criminal activo."
            : "Crimen registrado. Tu estado criminal sigue activo.";

        return new CrimeRegistrationResult(
            true,
            null,
            CloneRecord(normalized),
            CloneCrimeEvent(crimeEvent),
            statusChanged,
            feedback);
    }

    private async Task<CharacterCrimeRecord> LoadOrCreateRecordAsync(
        string identityId,
        string characterId,
        CancellationToken ct)
    {
        var loaded = await _store.LoadAsync<CharacterCrimeRecord>(
            JsonPersistenceDomains.Crime,
            characterId,
            ValidateRecord,
            ct);
        if (loaded is not null)
            return loaded;

        var now = DateTimeOffset.UtcNow;
        return new CharacterCrimeRecord
        {
            CharacterId = characterId.Trim(),
            IdentityId = identityId.Trim(),
            Status = CharacterCrimeStatus.Clean,
            WantedUntilUtc = null,
            LastCrimeAtUtc = null,
            TotalCrimeCount = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastSavedAtUtc = DateTimeOffset.MinValue,
        };
    }

    private CharacterCrimeRecord NormalizeAndExpire(
        CharacterCrimeRecord source,
        CrimeLawConfigurationRecord config,
        DateTimeOffset nowUtc)
    {
        var normalized = source;
        if (normalized.Status == CharacterCrimeStatus.Wanted
            && normalized.WantedUntilUtc.HasValue
            && normalized.WantedUntilUtc.Value <= nowUtc)
        {
            normalized = CloneRecord(source);
            normalized.Status = CharacterCrimeStatus.Clean;
            normalized.WantedUntilUtc = null;
            normalized.UpdatedAtUtc = nowUtc;
        }

        if (normalized.Events.Count > Math.Max(1, config.MaxEventsPerCharacter))
        {
            if (ReferenceEquals(normalized, source))
                normalized = CloneRecord(source);
            var maxEvents = Math.Max(1, config.MaxEventsPerCharacter);
            normalized.Events = normalized.Events.Skip(normalized.Events.Count - maxEvents).ToList();
        }

        return normalized;
    }

    private async Task SaveRecordAsync(CharacterCrimeRecord record, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        record.UpdatedAtUtc = now;
        record.LastSavedAtUtc = now;
        await _store.SaveAsync(JsonPersistenceDomains.Crime, record.CharacterId, record, ct);
    }

    private static bool IsCrimeTypeEnabled(CrimeLawConfigurationRecord config, CrimeType crimeType)
    {
        return crimeType switch
        {
            CrimeType.TheftFromConsciousCharacter => config.AutoDetectConsciousCharacterTheft,
            CrimeType.LootFromUnconsciousCharacter => config.AutoDetectUnconsciousCharacterLoot,
            CrimeType.UnauthorizedContainerAccess => config.AutoDetectUnauthorizedContainerAccess,
            _ => true,
        };
    }

    private static bool IsDuplicate(
        CharacterCrimeRecord record,
        string fingerprint,
        DateTimeOffset nowUtc,
        TimeSpan dedupWindow)
    {
        if (dedupWindow <= TimeSpan.Zero)
            return false;

        var threshold = nowUtc - dedupWindow;
        return record.Events.Any(x =>
            string.Equals(x.Fingerprint, fingerprint, StringComparison.Ordinal)
            && x.OccurredAtUtc >= threshold);
    }

    private static string BuildFingerprint(
        CrimeType crimeType,
        CrimeSource source,
        CrimeTargetKind targetKind,
        string? targetId,
        string? targetCharacterId,
        string? actionCode)
    {
        return string.Join('|',
            crimeType.ToString(),
            source.ToString(),
            targetKind.ToString(),
            targetId?.Trim() ?? "",
            targetCharacterId?.Trim() ?? "",
            actionCode?.Trim() ?? "");
    }

    private CrimeLawConfigurationRecord CreateDefaultConfiguration()
    {
        var now = DateTimeOffset.UtcNow;
        return new CrimeLawConfigurationRecord
        {
            ConfigVersion = _options.ConfigId,
            WantedDurationSeconds = Math.Max(1, _options.WantedDurationSeconds),
            DedupWindowSeconds = Math.Max(0, _options.DedupWindowSeconds),
            MaxEventsPerCharacter = Math.Max(1, _options.MaxEventsPerCharacter),
            AutoDetectConsciousCharacterTheft = _options.AutoDetectConsciousCharacterTheft,
            AutoDetectUnconsciousCharacterLoot = _options.AutoDetectUnconsciousCharacterLoot,
            AutoDetectUnauthorizedContainerAccess = _options.AutoDetectUnauthorizedContainerAccess,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ConfiguredIllegalActions = _options.ConfiguredIllegalActions
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static bool ValidateConfig(CrimeLawConfigurationRecord config)
    {
        if (config is null)
            return false;
        if (string.IsNullOrWhiteSpace(config.ConfigVersion))
            return false;
        if (config.WantedDurationSeconds <= 0 || config.DedupWindowSeconds < 0 || config.MaxEventsPerCharacter <= 0)
            return false;
        if (config.ConfiguredIllegalActions.Any(string.IsNullOrWhiteSpace))
            return false;
        return true;
    }

    private static bool ValidateRecord(CharacterCrimeRecord record)
    {
        if (record is null)
            return false;
        if (string.IsNullOrWhiteSpace(record.CharacterId) || string.IsNullOrWhiteSpace(record.IdentityId))
            return false;
        if (!Enum.IsDefined(record.Status))
            return false;
        if (record.TotalCrimeCount < 0)
            return false;
        if (record.Events.Any(x => !ValidateCrimeEvent(x)))
            return false;
        return true;
    }

    private static bool ValidateCrimeEvent(CrimeEventRecord evt)
    {
        if (evt is null)
            return false;
        if (string.IsNullOrWhiteSpace(evt.CrimeEventId))
            return false;
        if (!Enum.IsDefined(evt.CrimeType) || !Enum.IsDefined(evt.Source) || !Enum.IsDefined(evt.TargetKind))
            return false;
        if (string.IsNullOrWhiteSpace(evt.Fingerprint))
            return false;
        return true;
    }

    private static Dictionary<string, string> ToStringMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is null)
            return result;

        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
                continue;
            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                result[key.Trim()] = text.Trim();
        }

        return result;
    }

    private static void MergeMetadata(
        Dictionary<string, string> destination,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return;

        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
                continue;
            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                destination[key.Trim()] = text.Trim();
        }
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        string message,
        Guid? sessionId,
        string identityId,
        string characterId,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        _observability.Emit(new ServerObservableEvent(
            Type: type,
            Component: ServerObservableComponent.Persistence,
            Severity: severity,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            SessionId: sessionId,
            IdentityId: identityId,
            CharacterId: characterId,
            Message: message,
            Payload: payload));
    }

    private static CrimeLawConfigurationRecord CloneConfig(CrimeLawConfigurationRecord source)
    {
        return new CrimeLawConfigurationRecord
        {
            ConfigVersion = source.ConfigVersion,
            WantedDurationSeconds = source.WantedDurationSeconds,
            DedupWindowSeconds = source.DedupWindowSeconds,
            MaxEventsPerCharacter = source.MaxEventsPerCharacter,
            AutoDetectConsciousCharacterTheft = source.AutoDetectConsciousCharacterTheft,
            AutoDetectUnconsciousCharacterLoot = source.AutoDetectUnconsciousCharacterLoot,
            AutoDetectUnauthorizedContainerAccess = source.AutoDetectUnauthorizedContainerAccess,
            ConfiguredIllegalActions = source.ConfiguredIllegalActions.ToList(),
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
        };
    }

    private static CharacterCrimeRecord CloneRecord(CharacterCrimeRecord source)
    {
        return new CharacterCrimeRecord
        {
            CharacterId = source.CharacterId,
            IdentityId = source.IdentityId,
            Status = source.Status,
            WantedUntilUtc = source.WantedUntilUtc,
            LastCrimeAtUtc = source.LastCrimeAtUtc,
            TotalCrimeCount = source.TotalCrimeCount,
            Events = source.Events.Select(CloneCrimeEvent).ToList(),
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            LastSavedAtUtc = source.LastSavedAtUtc,
        };
    }

    private static CrimeEventRecord CloneCrimeEvent(CrimeEventRecord source)
    {
        return new CrimeEventRecord
        {
            CrimeEventId = source.CrimeEventId,
            CrimeType = source.CrimeType,
            Source = source.Source,
            TargetKind = source.TargetKind,
            TargetId = source.TargetId,
            TargetIdentityId = source.TargetIdentityId,
            TargetCharacterId = source.TargetCharacterId,
            ActionCode = source.ActionCode,
            Reason = source.Reason,
            Fingerprint = source.Fingerprint,
            OccurredAtUtc = source.OccurredAtUtc,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
        };
    }
}
