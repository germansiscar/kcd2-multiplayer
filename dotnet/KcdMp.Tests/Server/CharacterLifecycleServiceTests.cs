using System.Collections.Concurrent;
using KcdMp.Server.Characters;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class CharacterLifecycleServiceTests
{
    [Fact]
    public async Task LoadForSessionAsync_BlocksSameCharacterInAnotherSessionUntilUnloaded()
    {
        var root = CreateTempDir();
        try
        {
            var initial = CreateCharacter("cid_henry", "pid_henry", CharacterProfileStatus.Active);
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            await store.SaveAsync(JsonPersistenceDomains.Characters, initial.InternalId, initial);

            var sink = new InMemoryObservabilitySink();
            var lifecycle = new CharacterLifecycleService(
                store,
                new StubCharacterProfileService(initial),
                sink,
                new CharacterLifecycleOptions { SaveRetryCount = 0 });

            var s1 = Guid.NewGuid();
            var s2 = Guid.NewGuid();

            var firstLoad = await lifecycle.LoadForSessionAsync(s1, "pid_henry", "cid_henry");
            Assert.True(firstLoad.Loaded);

            var secondLoad = await lifecycle.LoadForSessionAsync(s2, "pid_henry", "cid_henry");
            Assert.False(secondLoad.Loaded);
            Assert.Equal("Character is already active in another session.", secondLoad.DenialReason);

            var unload = await lifecycle.SaveAndUnloadSessionAsync(s1, CharacterLifecycleSaveReason.SessionClosed);
            Assert.True(unload.Saved);

            var thirdLoad = await lifecycle.LoadForSessionAsync(s2, "pid_henry", "cid_henry");
            Assert.True(thirdLoad.Loaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndUnloadSessionAsync_RetriesAfterTransientFailure()
    {
        var character = CreateCharacter("cid_hans", "pid_hans", CharacterProfileStatus.Active);
        var store = new FlakyCharacterStore(character);
        var sink = new InMemoryObservabilitySink();
        var lifecycle = new CharacterLifecycleService(
            store,
            new StubCharacterProfileService(character),
            sink,
            new CharacterLifecycleOptions
            {
                SaveRetryCount = 1,
                SaveRetryDelay = TimeSpan.Zero,
            });

        var sessionId = Guid.NewGuid();
        var load = await lifecycle.LoadForSessionAsync(sessionId, character.IdentityId, character.InternalId);
        Assert.True(load.Loaded);

        store.FailNextSaves(1);
        var saved = await lifecycle.SaveAndUnloadSessionAsync(sessionId, CharacterLifecycleSaveReason.NetworkDisconnect);

        Assert.True(saved.Saved);
        Assert.Equal(2, saved.Attempts);
        Assert.Contains(
            sink.Events,
            evt => evt.Type == ServerObservableEventType.CharacterSaveFailed && evt.SessionId == sessionId);
        Assert.Contains(
            sink.Events,
            evt => evt.Type == ServerObservableEventType.CharacterSaveCompleted && evt.SessionId == sessionId);
    }

    [Fact]
    public async Task ValidateLightAsync_RejectsWrongOwnerAndDisabledCharacter()
    {
        var active = CreateCharacter("cid_active", "pid_owner", CharacterProfileStatus.Active);
        var disabled = CreateCharacter("cid_disabled", "pid_owner", CharacterProfileStatus.Disabled);
        var service = new CharacterLifecycleService(
            new FlakyCharacterStore(active),
            new StubCharacterProfileService(active, disabled),
            new InMemoryObservabilitySink());

        var wrongOwner = await service.ValidateLightAsync("pid_other", active.InternalId);
        Assert.False(wrongOwner.IsAllowed);
        Assert.Equal("Character is not owned by this identity.", wrongOwner.DenialReason);

        var disabledResult = await service.ValidateLightAsync("pid_owner", disabled.InternalId);
        Assert.False(disabledResult.IsAllowed);
        Assert.Equal("Character is disabled.", disabledResult.DenialReason);
    }

    private static CharacterProfileRecord CreateCharacter(string id, string identityId, CharacterProfileStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new CharacterProfileRecord
        {
            InternalId = id,
            IdentityId = identityId,
            FullName = $"Name {id}",
            NormalizedFullName = $"name {id}",
            ModelKey = "model_a",
            Status = status,
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastActivityAtUtc = now,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_character_lifecycle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class StubCharacterProfileService : ICharacterProfileService
    {
        private readonly Dictionary<string, CharacterProfileRecord> _records;

        public StubCharacterProfileService(params CharacterProfileRecord[] records)
        {
            _records = records.ToDictionary(x => x.InternalId, x => x, StringComparer.Ordinal);
        }

        public Task<CharacterCreateResult> CreateAsync(
            string identityId,
            CharacterCreateRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CharacterProfileRecord?> GetByInternalIdAsync(string characterId, CancellationToken ct = default)
        {
            _records.TryGetValue(characterId, out var value);
            return Task.FromResult(value);
        }

        public Task<IReadOnlyList<CharacterProfileRecord>> ListByIdentityAsync(string identityId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> TrySetStatusAsync(string characterId, CharacterProfileStatus status, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> TryDisableAsync(string characterId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> TryDeleteAsync(string characterId, bool isAdminOperation, CancellationToken ct = default)
            => throw new NotSupportedException();
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

    private sealed class FlakyCharacterStore : IJsonPersistenceStore
    {
        private readonly object _sync = new();
        private CharacterProfileRecord _record;
        private int _remainingFailures;

        public FlakyCharacterStore(CharacterProfileRecord initial)
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
            if (!string.Equals(domain, JsonPersistenceDomains.Characters, StringComparison.Ordinal))
                return Task.FromResult<T?>(default);
            if (!string.Equals(internalId, _record.InternalId, StringComparison.Ordinal))
                return Task.FromResult<T?>(default);

            var boxed = (T)(object)Clone(_record);
            if (validate is not null && !validate(boxed))
                throw new PersistenceValidationException("Validation failed.");
            return Task.FromResult<T?>(boxed);
        }

        public Task SaveAsync<T>(string domain, string internalId, T value, CancellationToken ct = default)
        {
            if (!string.Equals(domain, JsonPersistenceDomains.Characters, StringComparison.Ordinal))
                return Task.CompletedTask;

            lock (_sync)
            {
                if (_remainingFailures > 0)
                {
                    _remainingFailures--;
                    throw new IOException("Simulated transient save failure.");
                }

                _record = Clone((CharacterProfileRecord)(object)value!);
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

        private static CharacterProfileRecord Clone(CharacterProfileRecord source)
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
    }
}
