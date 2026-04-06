using System.Collections.Concurrent;
using KcdMp.Server.Characters;
using KcdMp.Server.Currency;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class CharacterCurrencyServiceTests
{
    [Fact]
    public async Task LoadForSessionAsync_WhenMissing_CreatesDefaultBalanceAndPersists()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterCurrencyService(
                store,
                sink,
                new CharacterCurrencyOptions { InitialBalance = 125 });

            var sessionId = Guid.NewGuid();
            var result = await service.LoadForSessionAsync(sessionId, "pid_henry", "cid_henry");

            Assert.True(result.Loaded);
            Assert.True(result.CreatedDefault);
            Assert.NotNull(result.Currency);
            Assert.Equal("cid_henry", result.Currency!.CharacterId);
            Assert.Equal(125, result.Currency.Balance);

            var persisted = await store.LoadAsync<CharacterCurrencyRecord>(JsonPersistenceDomains.Currency, "cid_henry");
            Assert.NotNull(persisted);
            Assert.Equal(125, persisted!.Balance);

            Assert.Contains(
                sink.Events,
                evt => evt.Type == ServerObservableEventType.CurrencyLoadCompleted && evt.SessionId == sessionId);
            Assert.Contains(
                sink.Events,
                evt => evt.Type == ServerObservableEventType.CurrencySaveCompleted && evt.SessionId == sessionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddAsync_WhenResultWouldBeNegative_IsRejected()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterCurrencyService(
                store,
                sink,
                new CharacterCurrencyOptions { InitialBalance = 10 });

            var sessionId = Guid.NewGuid();
            var loaded = await service.LoadForSessionAsync(sessionId, "pid_henry", "cid_henry");
            Assert.True(loaded.Loaded);

            var mutation = await service.AddAsync(sessionId, -11);

            Assert.False(mutation.Applied);
            Assert.Equal("Balance cannot be negative.", mutation.DenialReason);

            var current = await service.GetLoadedForSessionAsync(sessionId);
            Assert.NotNull(current);
            Assert.Equal(10, current!.Balance);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.CurrencyValidationFailed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddAsync_AndSaveUnload_PersistsAcrossSessions()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterCurrencyService(
                store,
                sink,
                new CharacterCurrencyOptions { InitialBalance = 25 });

            var s1 = Guid.NewGuid();
            var s2 = Guid.NewGuid();

            var loaded = await service.LoadForSessionAsync(s1, "pid_henry", "cid_henry");
            Assert.True(loaded.Loaded);

            var add = await service.AddAsync(s1, 40);
            Assert.True(add.Applied);
            Assert.Equal(65, add.Currency!.Balance);

            var saved = await service.SaveAndUnloadSessionAsync(s1, CharacterLifecycleSaveReason.SessionClosed);
            Assert.True(saved.Saved);

            var reloaded = await service.LoadForSessionAsync(s2, "pid_henry", "cid_henry");
            Assert.True(reloaded.Loaded);
            Assert.NotNull(reloaded.Currency);
            Assert.Equal(65, reloaded.Currency!.Balance);

            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.CurrencyChanged);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndUnloadSessionAsync_RetriesAfterTransientFailure()
    {
        var currency = new CharacterCurrencyRecord
        {
            CharacterId = "cid_hans",
            Balance = 90,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastSavedAtUtc = DateTimeOffset.UtcNow,
        };
        var store = new FlakyCurrencyStore(currency);
        var sink = new InMemoryObservabilitySink();
        var service = new CharacterCurrencyService(
            store,
            sink,
            new CharacterCurrencyOptions
            {
                InitialBalance = 90,
                SaveRetryCount = 1,
                SaveRetryDelay = TimeSpan.Zero,
            });

        var sessionId = Guid.NewGuid();
        var load = await service.LoadForSessionAsync(sessionId, "pid_hans", "cid_hans");
        Assert.True(load.Loaded);

        store.FailNextSaves(1);
        var saved = await service.SaveAndUnloadSessionAsync(sessionId, CharacterLifecycleSaveReason.NetworkDisconnect);

        Assert.True(saved.Saved);
        Assert.Equal(2, saved.Attempts);
        Assert.Contains(
            sink.Events,
            evt => evt.Type == ServerObservableEventType.CurrencySaveFailed && evt.SessionId == sessionId);
        Assert.Contains(
            sink.Events,
            evt => evt.Type == ServerObservableEventType.CurrencySaveCompleted && evt.SessionId == sessionId);
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_currency_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class InMemoryObservabilitySink : IServerObservabilitySink
    {
        private readonly ConcurrentQueue<ServerObservableEvent> _events = new();

        public IReadOnlyCollection<ServerObservableEvent> Events => _events.ToArray();

        public void Emit(ServerObservableEvent observableEvent)
        {
            _events.Enqueue(observableEvent);
        }
    }

    private sealed class FlakyCurrencyStore : IJsonPersistenceStore
    {
        private readonly object _sync = new();
        private CharacterCurrencyRecord _record;
        private int _remainingFailures;

        public FlakyCurrencyStore(CharacterCurrencyRecord initial)
        {
            _record = Clone(initial);
        }

        public void FailNextSaves(int count)
        {
            lock (_sync)
                _remainingFailures = Math.Max(0, count);
        }

        public Task<T?> LoadAsync<T>(
            string domain,
            string internalId,
            Func<T, bool>? validate = null,
            CancellationToken ct = default)
        {
            if (!string.Equals(domain, JsonPersistenceDomains.Currency, StringComparison.Ordinal))
                return Task.FromResult<T?>(default);
            if (!string.Equals(internalId, _record.CharacterId, StringComparison.Ordinal))
                return Task.FromResult<T?>(default);

            var boxed = (T)(object)Clone(_record);
            if (validate is not null && !validate(boxed))
                throw new PersistenceValidationException("Validation failed.");
            return Task.FromResult<T?>(boxed);
        }

        public Task SaveAsync<T>(string domain, string internalId, T value, CancellationToken ct = default)
        {
            if (!string.Equals(domain, JsonPersistenceDomains.Currency, StringComparison.Ordinal))
                return Task.CompletedTask;

            lock (_sync)
            {
                if (_remainingFailures > 0)
                {
                    _remainingFailures--;
                    throw new IOException("Simulated transient save failure.");
                }

                _record = Clone((CharacterCurrencyRecord)(object)value!);
            }

            return Task.CompletedTask;
        }

        public async Task<T> UpdateAsync<T>(
            string domain,
            string internalId,
            Func<T?, T> update,
            Func<T, bool>? validate = null,
            CancellationToken ct = default)
        {
            var current = await LoadAsync<T>(domain, internalId, validate: null, ct);
            var next = update(current);
            if (validate is not null && !validate(next))
                throw new PersistenceValidationException("Validation failed.");
            await SaveAsync(domain, internalId, next, ct);
            return next;
        }

        private static CharacterCurrencyRecord Clone(CharacterCurrencyRecord source)
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
    }
}
