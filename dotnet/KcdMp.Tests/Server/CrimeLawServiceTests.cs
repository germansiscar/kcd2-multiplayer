using System.Collections.Concurrent;
using KcdMp.Server.Crime;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class CrimeLawServiceTests
{
    [Fact]
    public async Task RegisterAutomaticCrimeAsync_SetsWantedAndPersists()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CrimeLawService(store, sink);

            var result = await service.RegisterAutomaticCrimeAsync(
                new AutomaticCrimeRegistrationRequest(
                    SessionId: Guid.NewGuid(),
                    IdentityId: "pid_1",
                    CharacterId: "cid_1",
                    CrimeType: CrimeType.TheftFromConsciousCharacter,
                    TargetKind: CrimeTargetKind.Character,
                    TargetId: "cid_target",
                    TargetIdentityId: "pid_target",
                    TargetCharacterId: "cid_target",
                    ActionCode: "loot_character_item"));

            Assert.True(result.Applied);
            Assert.NotNull(result.Record);
            Assert.NotNull(result.CrimeEvent);
            Assert.Equal(CharacterCrimeStatus.Wanted, result.Record!.Status);
            Assert.Equal(1, result.Record.TotalCrimeCount);
            Assert.Contains(sink.Events, x => x.Type == ServerObservableEventType.CrimeRegistered);

            var loaded = await service.GetCharacterRecordAsync("cid_1");
            Assert.NotNull(loaded);
            Assert.Equal(CharacterCrimeStatus.Wanted, loaded!.Status);
            Assert.Single(loaded.Events);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegisterAutomaticCrimeAsync_DedupsWithinConfiguredWindow()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CrimeLawService(
                store,
                sink,
                new CrimeLawOptions
                {
                    DedupWindowSeconds = 60,
                });

            var first = await service.RegisterAutomaticCrimeAsync(
                new AutomaticCrimeRegistrationRequest(
                    SessionId: Guid.NewGuid(),
                    IdentityId: "pid_2",
                    CharacterId: "cid_2",
                    CrimeType: CrimeType.UnauthorizedContainerAccess,
                    TargetKind: CrimeTargetKind.Container,
                    TargetId: "chest_a",
                    ActionCode: "loot_chest_item"));

            var second = await service.RegisterAutomaticCrimeAsync(
                new AutomaticCrimeRegistrationRequest(
                    SessionId: Guid.NewGuid(),
                    IdentityId: "pid_2",
                    CharacterId: "cid_2",
                    CrimeType: CrimeType.UnauthorizedContainerAccess,
                    TargetKind: CrimeTargetKind.Container,
                    TargetId: "chest_a",
                    ActionCode: "loot_chest_item"));

            Assert.True(first.Applied);
            Assert.False(second.Applied);
            Assert.Equal("Duplicate crime ignored.", second.DenialReason);

            var loaded = await service.GetCharacterRecordAsync("cid_2");
            Assert.NotNull(loaded);
            Assert.Single(loaded!.Events);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClearCharacterStateAsync_ResetsWantedStatus()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CrimeLawService(store, sink);

            var marked = await service.RegisterManualCrimeAsync(
                new ManualCrimeRegistrationRequest(
                    SessionId: null,
                    ActorIdentityId: "admin_1",
                    IdentityId: "pid_3",
                    CharacterId: "cid_3",
                    CrimeType: CrimeType.ManualStaffMark,
                    TargetKind: CrimeTargetKind.None,
                    Reason: "manual_review"));
            Assert.True(marked.Applied);
            Assert.Equal(CharacterCrimeStatus.Wanted, marked.Record!.Status);

            var cleared = await service.ClearCharacterStateAsync(
                new CrimeStateClearRequest(
                    SessionId: null,
                    ActorIdentityId: "admin_1",
                    IdentityId: "pid_3",
                    CharacterId: "cid_3",
                    Reason: "served_time"));

            Assert.True(cleared.Applied);
            Assert.NotNull(cleared.Record);
            Assert.Equal(CharacterCrimeStatus.Clean, cleared.Record!.Status);
            Assert.Null(cleared.Record.WantedUntilUtc);
            Assert.Contains(sink.Events, x => x.Type == ServerObservableEventType.CrimeAdminAction);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_crime_{Guid.NewGuid():N}");
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
}
