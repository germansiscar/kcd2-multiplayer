using KcdMp.Server.Characters;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.Currency;

public sealed class CharacterCurrencyService : ICharacterCurrencyService
{
    private readonly IJsonPersistenceStore _store;
    private readonly IServerObservabilitySink _observability;
    private readonly CharacterCurrencyOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, LoadedCurrencyContext> _loadedBySession = [];
    private readonly Dictionary<string, Guid> _characterToSession = new(StringComparer.Ordinal);

    public CharacterCurrencyService(
        IJsonPersistenceStore store,
        IServerObservabilitySink observability,
        CharacterCurrencyOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new CharacterCurrencyOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<CharacterCurrencyLoadResult> LoadForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        Emit(
            ServerObservableEventType.CurrencyLoadStarted,
            ServerObservableSeverity.Information,
            "Currency load started.",
            sessionId,
            identityId,
            characterId);

        await _gate.WaitAsync(ct);
        try
        {
            if (_characterToSession.TryGetValue(characterId, out var ownerSessionId) && ownerSessionId != sessionId)
            {
                return new CharacterCurrencyLoadResult(
                    false,
                    "Currency already active in another session.",
                    null,
                    CreatedDefault: false);
            }

            if (_loadedBySession.TryGetValue(sessionId, out var existing))
            {
                if (!string.Equals(existing.CharacterId, characterId, StringComparison.Ordinal))
                {
                    return new CharacterCurrencyLoadResult(
                        false,
                        "Session already has a different loaded currency context.",
                        null,
                        CreatedDefault: false);
                }

                return new CharacterCurrencyLoadResult(
                    true,
                    null,
                    CloneCurrency(existing.Currency),
                    CreatedDefault: false);
            }
        }
        finally
        {
            _gate.Release();
        }

        CharacterCurrencyRecord currency;
        var createdDefault = false;
        try
        {
            var loaded = await _store.LoadAsync<CharacterCurrencyRecord>(
                JsonPersistenceDomains.Currency,
                characterId,
                validate: ValidateCurrency,
                ct);
            if (loaded is null)
            {
                createdDefault = true;
                currency = CreateDefaultCurrency(characterId);
            }
            else
            {
                currency = CloneCurrency(loaded);
            }
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.CurrencyLoadFailed,
                ServerObservableSeverity.Error,
                "Currency load failed.",
                sessionId,
                identityId,
                characterId,
                payload: new Dictionary<string, object?>
                {
                    ["exception"] = ex.Message,
                });
            return new CharacterCurrencyLoadResult(false, "Currency could not be loaded.", null, false);
        }

        if (createdDefault)
        {
            var activationSave = await SaveWithRetryAsync(
                sessionId,
                identityId,
                currency,
                CharacterLifecycleSaveReason.Activation,
                ct);
            if (!activationSave.Saved)
            {
                return new CharacterCurrencyLoadResult(
                    false,
                    "Currency activation save failed.",
                    null,
                    CreatedDefault: true);
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_characterToSession.TryGetValue(characterId, out var ownerSessionId) && ownerSessionId != sessionId)
            {
                return new CharacterCurrencyLoadResult(
                    false,
                    "Currency already active in another session.",
                    null,
                    createdDefault);
            }

            _loadedBySession[sessionId] = new LoadedCurrencyContext(
                sessionId,
                identityId,
                characterId,
                CloneCurrency(currency));
            _characterToSession[characterId] = sessionId;
        }
        finally
        {
            _gate.Release();
        }

        Emit(
            ServerObservableEventType.CurrencyLoadCompleted,
            ServerObservableSeverity.Information,
            "Currency load completed.",
            sessionId,
            identityId,
            characterId,
            payload: new Dictionary<string, object?>
            {
                ["created_default"] = createdDefault,
                ["balance"] = currency.Balance,
            });

        return new CharacterCurrencyLoadResult(true, null, CloneCurrency(currency), createdDefault);
    }

    public async Task<CharacterCurrencyRecord?> GetLoadedForSessionAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out var loaded))
                return null;
            return CloneCurrency(loaded.Currency);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CharacterCurrencyMutationResult> SetBalanceAsync(
        Guid sessionId,
        long balance,
        CancellationToken ct = default)
    {
        return MutateBalanceAsync(
            sessionId,
            balanceUpdater: _ => balance,
            reason: "set",
            payloadFactory: (current, next) => new Dictionary<string, object?>
            {
                ["previous_balance"] = current,
                ["new_balance"] = next,
            },
            ct);
    }

    public Task<CharacterCurrencyMutationResult> AddAsync(
        Guid sessionId,
        long amount,
        CancellationToken ct = default)
    {
        return MutateBalanceAsync(
            sessionId,
            balanceUpdater: current => current + amount,
            reason: "delta",
            payloadFactory: (current, next) => new Dictionary<string, object?>
            {
                ["delta"] = amount,
                ["previous_balance"] = current,
                ["new_balance"] = next,
            },
            ct);
    }

    public async Task<CharacterCurrencySaveResult> SaveForSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        LoadedCurrencyContext? loaded;
        await _gate.WaitAsync(ct);
        try
        {
            _loadedBySession.TryGetValue(sessionId, out loaded);
            if (loaded is null)
                return new CharacterCurrencySaveResult(true, null, 0);
        }
        finally
        {
            _gate.Release();
        }

        return await SaveWithRetryAsync(sessionId, loaded.IdentityId, loaded.Currency, reason, ct);
    }

    public async Task<CharacterCurrencySaveResult> SaveAndUnloadSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        LoadedCurrencyContext? loaded;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out loaded))
                return new CharacterCurrencySaveResult(true, null, 0);

            _loadedBySession.Remove(sessionId);
            _characterToSession.Remove(loaded.CharacterId);
        }
        finally
        {
            _gate.Release();
        }

        return await SaveWithRetryAsync(sessionId, loaded.IdentityId, loaded.Currency, reason, ct);
    }

    public async Task<int> SaveAndUnloadAllAsync(
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        List<LoadedCurrencyContext> loaded;
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
            var save = await SaveWithRetryAsync(entry.SessionId, entry.IdentityId, entry.Currency, reason, ct);
            if (save.Saved)
                savedCount++;
        }

        return savedCount;
    }

    private async Task<CharacterCurrencyMutationResult> MutateBalanceAsync(
        Guid sessionId,
        Func<long, long> balanceUpdater,
        string reason,
        Func<long, long, IReadOnlyDictionary<string, object?>> payloadFactory,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out var loaded))
                return new CharacterCurrencyMutationResult(false, "Currency not loaded for this session.", null);

            var currentBalance = loaded.Currency.Balance;
            long nextBalance;
            try
            {
                nextBalance = checked(balanceUpdater(currentBalance));
            }
            catch (OverflowException)
            {
                Emit(
                    ServerObservableEventType.CurrencyValidationFailed,
                    ServerObservableSeverity.Warning,
                    "Currency mutation rejected due to numeric overflow.",
                    sessionId,
                    loaded.IdentityId,
                    loaded.CharacterId,
                    payload: new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["current_balance"] = currentBalance,
                    });
                return new CharacterCurrencyMutationResult(false, "Currency overflow.", null);
            }

            if (nextBalance < 0)
            {
                Emit(
                    ServerObservableEventType.CurrencyValidationFailed,
                    ServerObservableSeverity.Warning,
                    "Currency mutation rejected because resulting balance is negative.",
                    sessionId,
                    loaded.IdentityId,
                    loaded.CharacterId,
                    payload: payloadFactory(currentBalance, nextBalance));
                return new CharacterCurrencyMutationResult(false, "Balance cannot be negative.", null);
            }

            loaded.Currency.Balance = nextBalance;
            loaded.Currency.UpdatedAtUtc = DateTimeOffset.UtcNow;

            Emit(
                ServerObservableEventType.CurrencyChanged,
                ServerObservableSeverity.Information,
                "Currency balance changed.",
                sessionId,
                loaded.IdentityId,
                loaded.CharacterId,
                payload: payloadFactory(currentBalance, nextBalance));

            return new CharacterCurrencyMutationResult(true, null, CloneCurrency(loaded.Currency));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CharacterCurrencySaveResult> SaveWithRetryAsync(
        Guid sessionId,
        string identityId,
        CharacterCurrencyRecord currency,
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
            currency.UpdatedAtUtc = now;
            currency.LastSavedAtUtc = now;

            Emit(
                ServerObservableEventType.CurrencySaveStarted,
                ServerObservableSeverity.Information,
                "Currency save started.",
                sessionId,
                identityId,
                currency.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["reason"] = reason.ToString(),
                    ["attempt"] = attempt,
                    ["max_attempts"] = maxAttempts,
                });

            try
            {
                await _store.SaveAsync(JsonPersistenceDomains.Currency, currency.CharacterId, currency, ct);
                Emit(
                    ServerObservableEventType.CurrencySaveCompleted,
                    ServerObservableSeverity.Information,
                    "Currency save completed.",
                    sessionId,
                    identityId,
                    currency.CharacterId,
                    payload: new Dictionary<string, object?>
                    {
                        ["reason"] = reason.ToString(),
                        ["attempt"] = attempt,
                    });
                return new CharacterCurrencySaveResult(true, null, attempts);
            }
            catch (Exception ex)
            {
                lastError = ex;
                Emit(
                    ServerObservableEventType.CurrencySaveFailed,
                    ServerObservableSeverity.Error,
                    "Currency save failed.",
                    sessionId,
                    identityId,
                    currency.CharacterId,
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
            "[currency] persistent save incident character_id={CharacterId} session_id={SessionId} reason={Reason} attempts={Attempts}",
            currency.CharacterId,
            sessionId,
            reason,
            attempts);
        return new CharacterCurrencySaveResult(false, lastError?.Message, attempts);
    }

    private CharacterCurrencyRecord CreateDefaultCurrency(string characterId)
    {
        if (_options.InitialBalance < 0)
            throw new PersistenceValidationException("Configured initial currency balance cannot be negative.");

        var now = DateTimeOffset.UtcNow;
        return new CharacterCurrencyRecord
        {
            CharacterId = characterId,
            Balance = _options.InitialBalance,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastSavedAtUtc = DateTimeOffset.MinValue,
        };
    }

    private static bool ValidateCurrency(CharacterCurrencyRecord record)
    {
        if (record is null)
            return false;
        if (string.IsNullOrWhiteSpace(record.CharacterId))
            return false;
        if (record.Balance < 0)
            return false;
        return true;
    }

    private static CharacterCurrencyRecord CloneCurrency(CharacterCurrencyRecord source)
    {
        return new CharacterCurrencyRecord
        {
            CharacterId = source.CharacterId,
            Balance = source.Balance,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            LastSavedAtUtc = source.LastSavedAtUtc,
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

    private sealed record LoadedCurrencyContext(
        Guid SessionId,
        string IdentityId,
        string CharacterId,
        CharacterCurrencyRecord Currency);
}
