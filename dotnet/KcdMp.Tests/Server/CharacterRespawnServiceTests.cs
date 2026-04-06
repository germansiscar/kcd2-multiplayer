using System.Collections.Concurrent;
using KcdMp.Server.Characters;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using KcdMp.Server.Respawn;

namespace KcdMp.Tests.Server;

public sealed class CharacterRespawnServiceTests
{
    [Fact]
    public async Task LoadForSessionAsync_WhenMissing_CreatesDefaultLifecycle()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterRespawnService(store, sink);

            var sessionId = Guid.NewGuid();
            var result = await service.LoadForSessionAsync(sessionId, "pid_henry", "cid_henry");

            Assert.True(result.Loaded);
            Assert.True(result.CreatedDefault);
            Assert.NotNull(result.Lifecycle);
            Assert.Equal(CharacterDefeatState.Alive, result.Lifecycle!.State);

            var persisted = await store.LoadAsync<CharacterDefeatLifecycleRecord>(JsonPersistenceDomains.Respawn, "cid_henry");
            Assert.NotNull(persisted);
            Assert.Equal(CharacterDefeatState.Alive, persisted!.State);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.RespawnLoadCompleted);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.RespawnSaveCompleted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegisterDefeatAsync_WhenHealthZero_TransitionsToUnconscious()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var options = new CharacterRespawnOptions { UnconsciousDuration = TimeSpan.FromMinutes(5) };
            var service = new CharacterRespawnService(store, sink, options);

            var sessionId = Guid.NewGuid();
            await service.LoadForSessionAsync(sessionId, "pid_henry", "cid_henry");
            var defeat = await service.RegisterDefeatAsync(sessionId, 0);

            Assert.True(defeat.Applied);
            Assert.NotNull(defeat.Lifecycle);
            Assert.Equal(CharacterDefeatState.Unconscious, defeat.Lifecycle!.State);
            Assert.Equal(CharacterDefeatEventType.UnconsciousEntered, defeat.Lifecycle.LastEvent);
            Assert.True(defeat.Lifecycle.UnconsciousUntilUtc.HasValue);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.DefeatDetected);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.UnconsciousEntered);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TryRecoverByHealerAsync_WhenQualified_AvoidsRespawn()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterRespawnService(store, sink);

            var sessionId = Guid.NewGuid();
            await service.LoadForSessionAsync(sessionId, "pid_henry", "cid_henry");
            await service.RegisterDefeatAsync(sessionId, 0);
            var healed = await service.TryRecoverByHealerAsync(sessionId, "pid_healer", healerIsQualified: true);

            Assert.True(healed.Applied);
            Assert.NotNull(healed.Lifecycle);
            Assert.Equal(CharacterDefeatState.Alive, healed.Lifecycle!.State);
            Assert.Equal(CharacterDefeatEventType.HealerRecovered, healed.Lifecycle.LastEvent);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.HealerRecoveryApplied);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessUnconsciousTimeoutAsync_WhenExpired_Respawns()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterRespawnService(
                store,
                sink,
                new CharacterRespawnOptions { UnconsciousDuration = TimeSpan.FromMilliseconds(1) });

            var sessionId = Guid.NewGuid();
            await service.LoadForSessionAsync(sessionId, "pid_henry", "cid_henry");
            await service.RegisterDefeatAsync(sessionId, 0);
            await Task.Delay(10);

            var timeout = await service.ProcessUnconsciousTimeoutAsync(sessionId);

            Assert.True(timeout.Applied);
            Assert.NotNull(timeout.Lifecycle);
            Assert.Equal(CharacterDefeatState.Alive, timeout.Lifecycle!.State);
            Assert.Equal(CharacterDefeatEventType.RespawnApplied, timeout.Lifecycle.LastEvent);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.UnconsciousExpired);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.RespawnApplied);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelWaitAndRespawnAsync_WhenApplierFails_SetsPendingRespawn()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterRespawnService(
                store,
                sink,
                respawnApplier: new FailingRespawnApplier());

            var sessionId = Guid.NewGuid();
            await service.LoadForSessionAsync(sessionId, "pid_henry", "cid_henry");
            await service.RegisterDefeatAsync(sessionId, 0);

            var cancelled = await service.CancelWaitAndRespawnAsync(sessionId, "pid_henry");

            Assert.False(cancelled.Applied);
            Assert.NotNull(cancelled.Lifecycle);
            Assert.Equal(CharacterDefeatState.PendingRespawn, cancelled.Lifecycle!.State);
            Assert.Equal(CharacterDefeatEventType.RespawnApplyFailed, cancelled.Lifecycle.LastEvent);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.UnconsciousCancelled);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.RespawnApplyFailed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndUnloadSessionAsync_WhenDisconnectingWhileUnconscious_ForcesRespawn()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterRespawnService(store, sink);

            var sessionId = Guid.NewGuid();
            await service.LoadForSessionAsync(sessionId, "pid_henry", "cid_henry");
            await service.RegisterDefeatAsync(sessionId, 0);

            var save = await service.SaveAndUnloadSessionAsync(sessionId, CharacterLifecycleSaveReason.NetworkDisconnect);

            Assert.True(save.Saved);

            var persisted = await store.LoadAsync<CharacterDefeatLifecycleRecord>(JsonPersistenceDomains.Respawn, "cid_henry");
            Assert.NotNull(persisted);
            Assert.Equal(CharacterDefeatState.Alive, persisted!.State);
            Assert.Equal(CharacterDefeatEventType.RespawnApplied, persisted.LastEvent);
            Assert.Equal("Disconnect", persisted.Metadata["last_respawn_trigger"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_respawn_{Guid.NewGuid():N}");
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

    private sealed class FailingRespawnApplier : ICharacterRespawnApplier
    {
        public Task<CharacterRespawnApplyResult> ApplyAsync(CharacterRespawnApplyRequest request, CancellationToken ct = default)
            => Task.FromResult(new CharacterRespawnApplyResult(false, "respawn apply failed"));
    }
}
