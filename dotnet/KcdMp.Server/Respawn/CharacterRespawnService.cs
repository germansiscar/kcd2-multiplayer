using KcdMp.Server.Characters;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.Respawn;

public sealed class CharacterRespawnService : ICharacterRespawnService
{
    private readonly IJsonPersistenceStore _store;
    private readonly IServerObservabilitySink _observability;
    private readonly ICharacterRespawnApplier _respawnApplier;
    private readonly CharacterRespawnOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, LoadedRespawnContext> _loadedBySession = [];
    private readonly Dictionary<string, Guid> _characterToSession = new(StringComparer.Ordinal);

    public CharacterRespawnService(
        IJsonPersistenceStore store,
        IServerObservabilitySink observability,
        CharacterRespawnOptions? options = null,
        ICharacterRespawnApplier? respawnApplier = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new CharacterRespawnOptions();
        _respawnApplier = respawnApplier ?? new NoOpCharacterRespawnApplier();
        _logger = logger ?? Log.Logger;
    }

    public async Task<CharacterRespawnLoadResult> LoadForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        Emit(
            ServerObservableEventType.RespawnLoadStarted,
            ServerObservableSeverity.Information,
            "Respawn lifecycle load started.",
            sessionId,
            identityId,
            characterId);

        await _gate.WaitAsync(ct);
        try
        {
            if (_characterToSession.TryGetValue(characterId, out var ownerSessionId) && ownerSessionId != sessionId)
            {
                return new CharacterRespawnLoadResult(
                    false,
                    "Respawn lifecycle already active in another session.",
                    null,
                    CreatedDefault: false,
                    RecoveredFromTransientState: false);
            }

            if (_loadedBySession.TryGetValue(sessionId, out var existing))
            {
                if (!string.Equals(existing.CharacterId, characterId, StringComparison.Ordinal))
                {
                    return new CharacterRespawnLoadResult(
                        false,
                        "Session already has a different loaded respawn lifecycle.",
                        null,
                        CreatedDefault: false,
                        RecoveredFromTransientState: false);
                }

                return new CharacterRespawnLoadResult(
                    true,
                    null,
                    CloneLifecycle(existing.Lifecycle),
                    CreatedDefault: false,
                    RecoveredFromTransientState: false);
            }
        }
        finally
        {
            _gate.Release();
        }

        CharacterDefeatLifecycleRecord lifecycle;
        var createdDefault = false;
        try
        {
            var loaded = await _store.LoadAsync<CharacterDefeatLifecycleRecord>(
                JsonPersistenceDomains.Respawn,
                characterId,
                validate: ValidateLifecycle,
                ct);
            if (loaded is null)
            {
                createdDefault = true;
                lifecycle = CreateDefaultLifecycle(characterId);
            }
            else
            {
                lifecycle = CloneLifecycle(loaded);
            }
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.RespawnLoadFailed,
                ServerObservableSeverity.Error,
                "Respawn lifecycle load failed.",
                sessionId,
                identityId,
                characterId,
                payload: new Dictionary<string, object?>
                {
                    ["exception"] = ex.Message,
                });
            return new CharacterRespawnLoadResult(false, "Respawn lifecycle could not be loaded.", null, false, false);
        }

        var recoveredFromTransientState = false;
        if (lifecycle.State != CharacterDefeatState.Alive)
        {
            recoveredFromTransientState = true;
            ApplyCanonicalRespawnState(
                lifecycle,
                CharacterRespawnTrigger.RecoveryOnLoad,
                CharacterDefeatEventType.RespawnApplied,
                DateTimeOffset.UtcNow);
            Emit(
                ServerObservableEventType.RespawnApplied,
                ServerObservableSeverity.Information,
                "Recovered transient defeat state on session load by forcing respawn.",
                sessionId,
                identityId,
                characterId,
                payload: new Dictionary<string, object?>
                {
                    ["trigger"] = CharacterRespawnTrigger.RecoveryOnLoad.ToString(),
                });
        }

        if (createdDefault || recoveredFromTransientState)
        {
            var activationSave = await SaveWithRetryAsync(
                sessionId,
                identityId,
                lifecycle,
                CharacterLifecycleSaveReason.Activation,
                ct);
            if (!activationSave.Saved)
            {
                return new CharacterRespawnLoadResult(
                    false,
                    "Respawn lifecycle activation save failed.",
                    null,
                    createdDefault,
                    recoveredFromTransientState);
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_characterToSession.TryGetValue(characterId, out var ownerSessionId) && ownerSessionId != sessionId)
            {
                return new CharacterRespawnLoadResult(
                    false,
                    "Respawn lifecycle already active in another session.",
                    null,
                    createdDefault,
                    recoveredFromTransientState);
            }

            _loadedBySession[sessionId] = new LoadedRespawnContext(
                sessionId,
                identityId,
                characterId,
                CloneLifecycle(lifecycle));
            _characterToSession[characterId] = sessionId;
        }
        finally
        {
            _gate.Release();
        }

        Emit(
            ServerObservableEventType.RespawnLoadCompleted,
            ServerObservableSeverity.Information,
            "Respawn lifecycle load completed.",
            sessionId,
            identityId,
            characterId,
            payload: new Dictionary<string, object?>
            {
                ["created_default"] = createdDefault,
                ["recovered_from_transient_state"] = recoveredFromTransientState,
                ["state"] = lifecycle.State.ToString(),
            });

        return new CharacterRespawnLoadResult(
            true,
            null,
            CloneLifecycle(lifecycle),
            createdDefault,
            recoveredFromTransientState);
    }

    public async Task<CharacterDefeatLifecycleRecord?> GetLoadedForSessionAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out var loaded))
                return null;
            return CloneLifecycle(loaded.Lifecycle);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CharacterRespawnActionResult> RegisterDefeatAsync(
        Guid sessionId,
        int healthAfterHit,
        CancellationToken ct = default)
    {
        if (healthAfterHit > 0)
            return new CharacterRespawnActionResult(false, "Health is above zero.", null);

        LoadedRespawnContext? loaded;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out loaded))
                return new CharacterRespawnActionResult(false, "Respawn lifecycle not loaded for this session.", null);

            var now = DateTimeOffset.UtcNow;
            loaded.Lifecycle.State = CharacterDefeatState.Unconscious;
            loaded.Lifecycle.LastDefeatAtUtc = now;
            loaded.Lifecycle.UnconsciousUntilUtc = now + _options.UnconsciousDuration;
            loaded.Lifecycle.LastEvent = CharacterDefeatEventType.UnconsciousEntered;
            loaded.Lifecycle.UpdatedAtUtc = now;

            Emit(
                ServerObservableEventType.DefeatDetected,
                ServerObservableSeverity.Information,
                "Defeat detected and canonicalized by server.",
                loaded.SessionId,
                loaded.IdentityId,
                loaded.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["health_after_hit"] = healthAfterHit,
                });
            Emit(
                ServerObservableEventType.UnconsciousEntered,
                ServerObservableSeverity.Information,
                "Character entered unconscious state.",
                loaded.SessionId,
                loaded.IdentityId,
                loaded.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["unconscious_until_utc"] = loaded.Lifecycle.UnconsciousUntilUtc,
                });
        }
        finally
        {
            _gate.Release();
        }

        var save = await SaveForSessionAsync(sessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
        if (!save.Saved)
            return new CharacterRespawnActionResult(false, "Defeat state save failed.", null);

        var snapshot = await GetLoadedForSessionAsync(sessionId, ct);
        return new CharacterRespawnActionResult(true, null, snapshot);
    }

    public async Task<CharacterRespawnActionResult> TryRecoverByHealerAsync(
        Guid sessionId,
        string healerIdentityId,
        bool healerIsQualified,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(healerIdentityId);

        if (!healerIsQualified)
            return new CharacterRespawnActionResult(false, "Healer role is required.", null);

        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out var loaded))
                return new CharacterRespawnActionResult(false, "Respawn lifecycle not loaded for this session.", null);

            if (loaded.Lifecycle.State != CharacterDefeatState.Unconscious)
                return new CharacterRespawnActionResult(false, "Character is not unconscious.", null);

            var now = DateTimeOffset.UtcNow;
            if (loaded.Lifecycle.UnconsciousUntilUtc.HasValue && now >= loaded.Lifecycle.UnconsciousUntilUtc.Value)
                return new CharacterRespawnActionResult(false, "Unconscious window already expired.", null);

            loaded.Lifecycle.State = CharacterDefeatState.Alive;
            loaded.Lifecycle.UnconsciousUntilUtc = null;
            loaded.Lifecycle.LastEvent = CharacterDefeatEventType.HealerRecovered;
            loaded.Lifecycle.UpdatedAtUtc = now;

            Emit(
                ServerObservableEventType.HealerRecoveryApplied,
                ServerObservableSeverity.Information,
                "Character recovered by healer without respawn.",
                loaded.SessionId,
                loaded.IdentityId,
                loaded.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["healer_identity_id"] = healerIdentityId,
                });
        }
        finally
        {
            _gate.Release();
        }

        var save = await SaveForSessionAsync(sessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
        if (!save.Saved)
            return new CharacterRespawnActionResult(false, "Healer recovery save failed.", null);

        var snapshot = await GetLoadedForSessionAsync(sessionId, ct);
        return new CharacterRespawnActionResult(true, null, snapshot);
    }

    public async Task<CharacterRespawnActionResult> ProcessUnconsciousTimeoutAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var timeoutState = await GetLoadedForSessionAsync(sessionId, ct);
        if (timeoutState is null)
            return new CharacterRespawnActionResult(false, "Respawn lifecycle not loaded for this session.", null);

        if (timeoutState.State != CharacterDefeatState.Unconscious)
            return new CharacterRespawnActionResult(false, "Character is not unconscious.", timeoutState);

        var now = DateTimeOffset.UtcNow;
        if (!timeoutState.UnconsciousUntilUtc.HasValue || now < timeoutState.UnconsciousUntilUtc.Value)
            return new CharacterRespawnActionResult(false, "Unconscious timeout has not expired yet.", timeoutState);

        await _gate.WaitAsync(ct);
        try
        {
            if (_loadedBySession.TryGetValue(sessionId, out var loaded))
            {
                loaded.Lifecycle.LastEvent = CharacterDefeatEventType.UnconsciousExpired;
                loaded.Lifecycle.UpdatedAtUtc = now;

                Emit(
                    ServerObservableEventType.UnconsciousExpired,
                    ServerObservableSeverity.Information,
                    "Unconscious window expired.",
                    loaded.SessionId,
                    loaded.IdentityId,
                    loaded.CharacterId,
                    payload: new Dictionary<string, object?>
                    {
                        ["unconscious_until_utc"] = loaded.Lifecycle.UnconsciousUntilUtc,
                    });
            }
        }
        finally
        {
            _gate.Release();
        }

        return await ApplyRespawnForLoadedSessionAsync(sessionId, CharacterRespawnTrigger.UnconsciousTimeout, CharacterLifecycleSaveReason.DomainEvent, ct);
    }

    public async Task<CharacterRespawnActionResult> CancelWaitAndRespawnAsync(
        Guid sessionId,
        string cancelledByIdentityId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelledByIdentityId);

        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out var loaded))
                return new CharacterRespawnActionResult(false, "Respawn lifecycle not loaded for this session.", null);

            if (loaded.Lifecycle.State != CharacterDefeatState.Unconscious)
                return new CharacterRespawnActionResult(false, "Character is not unconscious.", CloneLifecycle(loaded.Lifecycle));

            loaded.Lifecycle.LastEvent = CharacterDefeatEventType.UnconsciousCancelled;
            loaded.Lifecycle.UpdatedAtUtc = DateTimeOffset.UtcNow;

            Emit(
                ServerObservableEventType.UnconsciousCancelled,
                ServerObservableSeverity.Information,
                "Unconscious wait cancelled by player.",
                loaded.SessionId,
                loaded.IdentityId,
                loaded.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["cancelled_by_identity_id"] = cancelledByIdentityId,
                });
        }
        finally
        {
            _gate.Release();
        }

        return await ApplyRespawnForLoadedSessionAsync(sessionId, CharacterRespawnTrigger.WaitCancelled, CharacterLifecycleSaveReason.DomainEvent, ct);
    }

    public async Task<CharacterRespawnSaveResult> SaveForSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        LoadedRespawnContext? loaded;
        await _gate.WaitAsync(ct);
        try
        {
            _loadedBySession.TryGetValue(sessionId, out loaded);
            if (loaded is null)
                return new CharacterRespawnSaveResult(true, null, 0);
        }
        finally
        {
            _gate.Release();
        }

        return await SaveWithRetryAsync(sessionId, loaded.IdentityId, loaded.Lifecycle, reason, ct);
    }

    public async Task<CharacterRespawnSaveResult> SaveAndUnloadSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        LoadedRespawnContext? loaded;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out loaded))
                return new CharacterRespawnSaveResult(true, null, 0);

            _loadedBySession.Remove(sessionId);
            _characterToSession.Remove(loaded.CharacterId);
        }
        finally
        {
            _gate.Release();
        }

        if ((reason == CharacterLifecycleSaveReason.NetworkDisconnect || reason == CharacterLifecycleSaveReason.Shutdown)
            && loaded.Lifecycle.State != CharacterDefeatState.Alive)
        {
            var trigger = reason == CharacterLifecycleSaveReason.Shutdown
                ? CharacterRespawnTrigger.Shutdown
                : CharacterRespawnTrigger.Disconnect;
            ApplyCanonicalRespawnState(loaded.Lifecycle, trigger, CharacterDefeatEventType.RespawnApplied, DateTimeOffset.UtcNow);

            Emit(
                ServerObservableEventType.RespawnApplied,
                ServerObservableSeverity.Information,
                "Forced respawn due to disconnect/shutdown while in post-defeat flow.",
                loaded.SessionId,
                loaded.IdentityId,
                loaded.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["trigger"] = trigger.ToString(),
                    ["forced"] = true,
                });
        }

        return await SaveWithRetryAsync(sessionId, loaded.IdentityId, loaded.Lifecycle, reason, ct);
    }

    public async Task<int> SaveAndUnloadAllAsync(
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        List<LoadedRespawnContext> loaded;
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

            if ((reason == CharacterLifecycleSaveReason.NetworkDisconnect || reason == CharacterLifecycleSaveReason.Shutdown)
                && entry.Lifecycle.State != CharacterDefeatState.Alive)
            {
                var trigger = reason == CharacterLifecycleSaveReason.Shutdown
                    ? CharacterRespawnTrigger.Shutdown
                    : CharacterRespawnTrigger.Disconnect;
                ApplyCanonicalRespawnState(entry.Lifecycle, trigger, CharacterDefeatEventType.RespawnApplied, DateTimeOffset.UtcNow);
            }

            var save = await SaveWithRetryAsync(entry.SessionId, entry.IdentityId, entry.Lifecycle, reason, ct);
            if (save.Saved)
                savedCount++;
        }

        return savedCount;
    }

    private async Task<CharacterRespawnActionResult> ApplyRespawnForLoadedSessionAsync(
        Guid sessionId,
        CharacterRespawnTrigger trigger,
        CharacterLifecycleSaveReason saveReason,
        CancellationToken ct)
    {
        LoadedRespawnContext? loaded;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out loaded))
                return new CharacterRespawnActionResult(false, "Respawn lifecycle not loaded for this session.", null);

            if (loaded.Lifecycle.State != CharacterDefeatState.Unconscious && loaded.Lifecycle.State != CharacterDefeatState.PendingRespawn)
                return new CharacterRespawnActionResult(false, "Character is not in a respawnable post-defeat state.", CloneLifecycle(loaded.Lifecycle));
        }
        finally
        {
            _gate.Release();
        }

        var request = new CharacterRespawnApplyRequest(
            SessionId: loaded.SessionId,
            IdentityId: loaded.IdentityId,
            CharacterId: loaded.CharacterId,
            Trigger: trigger,
            RespawnPolicyId: _options.DefaultRespawnPolicyId,
            RespawnPointId: _options.DefaultRespawnPointId,
            SaveReason: saveReason);

        CharacterRespawnApplyResult applyResult;
        try
        {
            applyResult = await _respawnApplier.ApplyAsync(request, ct);
        }
        catch (Exception ex)
        {
            applyResult = new CharacterRespawnApplyResult(false, ex.Message);
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out loaded))
                return new CharacterRespawnActionResult(false, "Respawn lifecycle not loaded for this session.", null);

            if (applyResult.Applied)
            {
                ApplyCanonicalRespawnState(loaded.Lifecycle, trigger, CharacterDefeatEventType.RespawnApplied, DateTimeOffset.UtcNow);
                Emit(
                    ServerObservableEventType.RespawnApplied,
                    ServerObservableSeverity.Information,
                    "Respawn applied.",
                    loaded.SessionId,
                    loaded.IdentityId,
                    loaded.CharacterId,
                    payload: new Dictionary<string, object?>
                    {
                        ["trigger"] = trigger.ToString(),
                        ["respawn_policy_id"] = _options.DefaultRespawnPolicyId,
                        ["respawn_point_id"] = _options.DefaultRespawnPointId,
                    });
            }
            else
            {
                loaded.Lifecycle.State = CharacterDefeatState.PendingRespawn;
                loaded.Lifecycle.LastEvent = CharacterDefeatEventType.RespawnApplyFailed;
                loaded.Lifecycle.UpdatedAtUtc = DateTimeOffset.UtcNow;
                Emit(
                    ServerObservableEventType.RespawnApplyFailed,
                    ServerObservableSeverity.Error,
                    "Respawn could not be fully applied and character is pending respawn.",
                    loaded.SessionId,
                    loaded.IdentityId,
                    loaded.CharacterId,
                    payload: new Dictionary<string, object?>
                    {
                        ["trigger"] = trigger.ToString(),
                        ["failure_reason"] = applyResult.FailureReason,
                    });
            }
        }
        finally
        {
            _gate.Release();
        }

        var save = await SaveForSessionAsync(sessionId, saveReason, ct);
        if (!save.Saved)
            return new CharacterRespawnActionResult(false, "Respawn lifecycle save failed.", null);

        var snapshot = await GetLoadedForSessionAsync(sessionId, ct);
        if (applyResult.Applied)
            return new CharacterRespawnActionResult(true, null, snapshot);

        return new CharacterRespawnActionResult(false, applyResult.FailureReason ?? "Respawn apply failed.", snapshot);
    }

    private async Task<CharacterRespawnSaveResult> SaveWithRetryAsync(
        Guid sessionId,
        string identityId,
        CharacterDefeatLifecycleRecord lifecycle,
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

            var now = DateTimeOffset.UtcNow;
            lifecycle.UpdatedAtUtc = now;
            lifecycle.LastSavedAtUtc = now;

            Emit(
                ServerObservableEventType.RespawnSaveStarted,
                ServerObservableSeverity.Information,
                "Respawn lifecycle save started.",
                sessionId,
                identityId,
                lifecycle.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["reason"] = reason.ToString(),
                    ["attempt"] = attempt,
                    ["max_attempts"] = maxAttempts,
                });

            try
            {
                await _store.SaveAsync(JsonPersistenceDomains.Respawn, lifecycle.CharacterId, lifecycle, ct);
                Emit(
                    ServerObservableEventType.RespawnSaveCompleted,
                    ServerObservableSeverity.Information,
                    "Respawn lifecycle save completed.",
                    sessionId,
                    identityId,
                    lifecycle.CharacterId,
                    payload: new Dictionary<string, object?>
                    {
                        ["reason"] = reason.ToString(),
                        ["attempt"] = attempt,
                    });

                return new CharacterRespawnSaveResult(true, null, attempts);
            }
            catch (Exception ex)
            {
                lastError = ex;
                Emit(
                    ServerObservableEventType.RespawnSaveFailed,
                    ServerObservableSeverity.Error,
                    "Respawn lifecycle save failed.",
                    sessionId,
                    identityId,
                    lifecycle.CharacterId,
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
            "[respawn] persistent save incident character_id={CharacterId} session_id={SessionId} reason={Reason} attempts={Attempts}",
            lifecycle.CharacterId,
            sessionId,
            reason,
            attempts);
        return new CharacterRespawnSaveResult(false, lastError?.Message, attempts);
    }

    private CharacterDefeatLifecycleRecord CreateDefaultLifecycle(string characterId)
    {
        if (_options.UnconsciousDuration <= TimeSpan.Zero)
            throw new PersistenceValidationException("UnconsciousDuration must be greater than zero.");

        var now = DateTimeOffset.UtcNow;
        return new CharacterDefeatLifecycleRecord
        {
            CharacterId = characterId,
            State = CharacterDefeatState.Alive,
            LastEvent = CharacterDefeatEventType.None,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastSavedAtUtc = DateTimeOffset.MinValue,
            LastRespawnPolicyId = _options.DefaultRespawnPolicyId,
            LastRespawnPointId = _options.DefaultRespawnPointId,
        };
    }

    private static bool ValidateLifecycle(CharacterDefeatLifecycleRecord record)
    {
        if (record is null)
            return false;
        if (string.IsNullOrWhiteSpace(record.CharacterId))
            return false;
        if (!Enum.IsDefined(record.State))
            return false;
        if (!Enum.IsDefined(record.LastEvent))
            return false;
        return true;
    }

    private void ApplyCanonicalRespawnState(
        CharacterDefeatLifecycleRecord lifecycle,
        CharacterRespawnTrigger trigger,
        CharacterDefeatEventType resultingEvent,
        DateTimeOffset now)
    {
        lifecycle.State = CharacterDefeatState.Alive;
        lifecycle.LastEvent = resultingEvent;
        lifecycle.UnconsciousUntilUtc = null;
        lifecycle.LastRespawnAtUtc = now;
        lifecycle.LastRespawnPolicyId = _options.DefaultRespawnPolicyId;
        lifecycle.LastRespawnPointId = _options.DefaultRespawnPointId;
        lifecycle.UpdatedAtUtc = now;
        lifecycle.Metadata["last_respawn_trigger"] = trigger.ToString();
    }

    private static CharacterDefeatLifecycleRecord CloneLifecycle(CharacterDefeatLifecycleRecord source)
    {
        return new CharacterDefeatLifecycleRecord
        {
            CharacterId = source.CharacterId,
            State = source.State,
            LastEvent = source.LastEvent,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            LastSavedAtUtc = source.LastSavedAtUtc,
            LastDefeatAtUtc = source.LastDefeatAtUtc,
            UnconsciousUntilUtc = source.UnconsciousUntilUtc,
            LastRespawnAtUtc = source.LastRespawnAtUtc,
            LastRespawnPolicyId = source.LastRespawnPolicyId,
            LastRespawnPointId = source.LastRespawnPointId,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
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

    private sealed record LoadedRespawnContext(
        Guid SessionId,
        string IdentityId,
        string CharacterId,
        CharacterDefeatLifecycleRecord Lifecycle);
}
