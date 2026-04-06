using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.Characters;

public sealed class CharacterLifecycleService : ICharacterLifecycleService
{
    private readonly IJsonPersistenceStore _store;
    private readonly ICharacterProfileService _characters;
    private readonly IServerObservabilitySink _observability;
    private readonly CharacterLifecycleOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, LoadedCharacterContext> _loadedBySession = [];
    private readonly Dictionary<string, Guid> _characterToSession = new(StringComparer.Ordinal);

    public CharacterLifecycleService(
        IJsonPersistenceStore store,
        ICharacterProfileService characters,
        IServerObservabilitySink observability,
        CharacterLifecycleOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _characters = characters ?? throw new ArgumentNullException(nameof(characters));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new CharacterLifecycleOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<CharacterLifecycleValidationResult> ValidateLightAsync(
        string identityId,
        string characterId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        var character = await _characters.GetByInternalIdAsync(characterId, ct);
        if (character is null)
            return new CharacterLifecycleValidationResult(false, "Character does not exist.");
        if (character.IsDeleted)
            return new CharacterLifecycleValidationResult(false, "Character is deleted.");
        if (!string.Equals(character.IdentityId, identityId, StringComparison.Ordinal))
            return new CharacterLifecycleValidationResult(false, "Character is not owned by this identity.");
        if (character.Status == CharacterProfileStatus.Disabled)
            return new CharacterLifecycleValidationResult(false, "Character is disabled.");

        return new CharacterLifecycleValidationResult(true, null);
    }

    public async Task<CharacterLifecycleLoadResult> LoadForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        Emit(
            ServerObservableEventType.CharacterLoadStarted,
            ServerObservableSeverity.Information,
            "Character load started.",
            sessionId,
            identityId,
            characterId);

        var validation = await ValidateLightAsync(identityId, characterId, ct);
        if (!validation.IsAllowed)
        {
            Emit(
                ServerObservableEventType.CharacterLoadFailed,
                ServerObservableSeverity.Warning,
                "Character load denied by light validation.",
                sessionId,
                identityId,
                characterId,
                payload: new Dictionary<string, object?>
                {
                    ["reason"] = validation.DenialReason,
                });
            return new CharacterLifecycleLoadResult(false, validation.DenialReason, null);
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_characterToSession.TryGetValue(characterId, out var ownerSessionId) && ownerSessionId != sessionId)
            {
                const string reason = "Character is already active in another session.";
                Emit(
                    ServerObservableEventType.CharacterLoadFailed,
                    ServerObservableSeverity.Warning,
                    "Character load rejected because character is already active.",
                    sessionId,
                    identityId,
                    characterId,
                    payload: new Dictionary<string, object?> { ["active_session_id"] = ownerSessionId });
                return new CharacterLifecycleLoadResult(false, reason, null);
            }

            if (_loadedBySession.TryGetValue(sessionId, out var existing))
            {
                if (!string.Equals(existing.CharacterId, characterId, StringComparison.Ordinal))
                {
                    const string reason = "Session already has a different active character.";
                    Emit(
                        ServerObservableEventType.CharacterLoadFailed,
                        ServerObservableSeverity.Warning,
                        "Character load rejected because session already has an active character.",
                        sessionId,
                        identityId,
                        characterId,
                        payload: new Dictionary<string, object?> { ["existing_character_id"] = existing.CharacterId });
                    return new CharacterLifecycleLoadResult(false, reason, null);
                }

                return new CharacterLifecycleLoadResult(true, null, CloneRecord(existing.Profile));
            }
        }
        finally
        {
            _gate.Release();
        }

        CharacterProfileRecord profile;
        try
        {
            profile = await _store.LoadAsync<CharacterProfileRecord>(
                    JsonPersistenceDomains.Characters,
                    characterId,
                    ValidateLoadedRecord,
                    ct)
                ?? throw new PersistenceLoadException($"Character profile '{characterId}' not found.");
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.CharacterLoadFailed,
                ServerObservableSeverity.Error,
                "Character load failed.",
                sessionId,
                identityId,
                characterId,
                payload: new Dictionary<string, object?> { ["reason"] = ex.Message });
            return new CharacterLifecycleLoadResult(false, "Character profile could not be loaded.", null);
        }

        if (!string.Equals(profile.IdentityId, identityId, StringComparison.Ordinal))
        {
            Emit(
                ServerObservableEventType.CharacterLoadFailed,
                ServerObservableSeverity.Warning,
                "Character load failed due to ownership mismatch.",
                sessionId,
                identityId,
                characterId);
            return new CharacterLifecycleLoadResult(false, "Character ownership mismatch.", null);
        }

        var now = DateTimeOffset.UtcNow;
        profile.LastActivityAtUtc = now;
        profile.UpdatedAtUtc = now;
        var activationSave = await SaveWithRetryAsync(sessionId, profile, CharacterLifecycleSaveReason.Activation, ct);
        if (!activationSave.Saved)
            return new CharacterLifecycleLoadResult(false, "Character activation save failed.", null);

        await _gate.WaitAsync(ct);
        try
        {
            if (_characterToSession.TryGetValue(characterId, out var ownerSessionId) && ownerSessionId != sessionId)
                return new CharacterLifecycleLoadResult(false, "Character is already active in another session.", null);

            _loadedBySession[sessionId] = new LoadedCharacterContext(
                sessionId,
                identityId,
                characterId,
                CloneRecord(profile));
            _characterToSession[characterId] = sessionId;
        }
        finally
        {
            _gate.Release();
        }

        Emit(
            ServerObservableEventType.CharacterLoadCompleted,
            ServerObservableSeverity.Information,
            "Character load completed.",
            sessionId,
            identityId,
            characterId);

        return new CharacterLifecycleLoadResult(true, null, CloneRecord(profile));
    }

    public async Task<CharacterLifecycleSaveResult> SaveAndUnloadSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        LoadedCharacterContext? loaded;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out loaded))
                return new CharacterLifecycleSaveResult(true, null, 0);

            _loadedBySession.Remove(sessionId);
            _characterToSession.Remove(loaded.CharacterId);
        }
        finally
        {
            _gate.Release();
        }

        return await SaveWithRetryAsync(sessionId, loaded.Profile, reason, ct);
    }

    public async Task<int> SaveAndUnloadAllAsync(
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        List<LoadedCharacterContext> loaded;
        await _gate.WaitAsync(ct);
        try
        {
            loaded = _loadedBySession.Values.ToList();
            _loadedBySession.Clear();
            _characterToSession.Clear();
        }
        finally
        {
            _gate.Release();
        }

        var savedCount = 0;
        foreach (var entry in loaded)
        {
            ct.ThrowIfCancellationRequested();
            var saved = await SaveWithRetryAsync(entry.SessionId, entry.Profile, reason, ct);
            if (saved.Saved)
                savedCount++;
        }

        return savedCount;
    }

    private async Task<CharacterLifecycleSaveResult> SaveWithRetryAsync(
        Guid sessionId,
        CharacterProfileRecord profile,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct)
    {
        var attempts = 0;
        Exception? lastError = null;

        var maxAttempts = Math.Max(1, _options.SaveRetryCount + 1);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            attempts = attempt;
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
            profile.LastActivityAtUtc = profile.UpdatedAtUtc;

            Emit(
                ServerObservableEventType.CharacterSaveStarted,
                ServerObservableSeverity.Information,
                "Character save started.",
                sessionId,
                profile.IdentityId,
                profile.InternalId,
                payload: new Dictionary<string, object?>
                {
                    ["reason"] = reason.ToString(),
                    ["attempt"] = attempt,
                    ["max_attempts"] = maxAttempts,
                });

            try
            {
                await _store.SaveAsync(JsonPersistenceDomains.Characters, profile.InternalId, profile, ct);
                Emit(
                    ServerObservableEventType.CharacterSaveCompleted,
                    ServerObservableSeverity.Information,
                    "Character save completed.",
                    sessionId,
                    profile.IdentityId,
                    profile.InternalId,
                    payload: new Dictionary<string, object?>
                    {
                        ["reason"] = reason.ToString(),
                        ["attempt"] = attempt,
                    });

                return new CharacterLifecycleSaveResult(true, null, attempts);
            }
            catch (Exception ex)
            {
                lastError = ex;
                Emit(
                    ServerObservableEventType.CharacterSaveFailed,
                    ServerObservableSeverity.Error,
                    "Character save failed.",
                    sessionId,
                    profile.IdentityId,
                    profile.InternalId,
                    payload: new Dictionary<string, object?>
                    {
                        ["reason"] = reason.ToString(),
                        ["attempt"] = attempt,
                        ["max_attempts"] = maxAttempts,
                        ["exception"] = ex.Message,
                    });

                if (attempt < maxAttempts && _options.SaveRetryDelay > TimeSpan.Zero)
                    await Task.Delay(_options.SaveRetryDelay, ct);
            }
        }

        _logger.Warning(
            "[character-lifecycle] persistent save incident character_id={CharacterId} session_id={SessionId} reason={Reason} attempts={Attempts}",
            profile.InternalId,
            sessionId,
            reason,
            attempts);
        return new CharacterLifecycleSaveResult(false, lastError?.Message, attempts);
    }

    private static bool ValidateLoadedRecord(CharacterProfileRecord record)
    {
        return !string.IsNullOrWhiteSpace(record.InternalId)
               && !string.IsNullOrWhiteSpace(record.IdentityId)
               && !string.IsNullOrWhiteSpace(record.FullName)
               && !string.IsNullOrWhiteSpace(record.ModelKey)
               && !record.IsDeleted
               && record.Status != CharacterProfileStatus.Disabled;
    }

    private static CharacterProfileRecord CloneRecord(CharacterProfileRecord source)
    {
        return new CharacterProfileRecord
        {
            InternalId = source.InternalId,
            IdentityId = source.IdentityId,
            FullName = source.FullName,
            NormalizedFullName = source.NormalizedFullName,
            ModelKey = source.ModelKey,
            Status = source.Status,
            IsDeleted = source.IsDeleted,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            LastActivityAtUtc = source.LastActivityAtUtc,
        };
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        string message,
        Guid? sessionId = null,
        string? identityId = null,
        string? characterId = null,
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

    private sealed record LoadedCharacterContext(
        Guid SessionId,
        string IdentityId,
        string CharacterId,
        CharacterProfileRecord Profile);
}
