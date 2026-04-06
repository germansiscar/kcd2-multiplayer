using System.Collections.Concurrent;
using KcdMp.Server.Characters;
using KcdMp.Server.Inventory;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class CharacterInventoryServiceTests
{
    [Fact]
    public async Task LoadForSessionAsync_WhenMissing_CreatesBaseContainerAndPersists()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterInventoryService(store, sink);

            var sessionId = Guid.NewGuid();
            var result = await service.LoadForSessionAsync(sessionId, "pid_henry", "cid_henry");

            Assert.True(result.Loaded);
            Assert.NotNull(result.Inventory);
            var baseContainer = Assert.Single(result.Inventory!.Containers);
            Assert.Equal("main", baseContainer.ContainerId);
            Assert.Equal(InventoryContainerType.Primary, baseContainer.ContainerType);
            Assert.Equal("cid_henry", result.Inventory.CharacterId);

            var persisted = await store.LoadAsync<CharacterInventoryRecord>(JsonPersistenceDomains.Inventory, "cid_henry");
            Assert.NotNull(persisted);
            Assert.Contains(
                sink.Events,
                evt => evt.Type == ServerObservableEventType.InventoryLoadCompleted && evt.SessionId == sessionId);
            Assert.Contains(
                sink.Events,
                evt => evt.Type == ServerObservableEventType.InventorySaveCompleted && evt.SessionId == sessionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadForSessionAsync_DropsInvalidItemsButKeepsValid()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var malformed = new CharacterInventoryRecord
            {
                CharacterId = "cid_corrupt",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastSavedAtUtc = DateTimeOffset.UtcNow,
                Containers =
                [
                    new InventoryContainerRecord
                    {
                        ContainerId = "main",
                        ContainerType = InventoryContainerType.Primary,
                        DisplayName = "Main",
                        Items =
                        [
                            new InventoryItemRecord
                            {
                                InternalId = "it_valid",
                                ItemType = "weapon",
                                ItemRef = "sword.long",
                                Quantity = 1,
                                IsStackable = false,
                            },
                            new InventoryItemRecord
                            {
                                InternalId = "it_bad_qty",
                                ItemType = "consumable",
                                ItemRef = "food.bread",
                                Quantity = -2,
                                IsStackable = true,
                            },
                            new InventoryItemRecord
                            {
                                InternalId = "it_bad_stack",
                                ItemType = "weapon",
                                ItemRef = "axe",
                                Quantity = 2,
                                IsStackable = false,
                            },
                        ],
                    },
                ],
            };

            await store.SaveAsync(JsonPersistenceDomains.Inventory, "cid_corrupt", malformed);

            var sink = new InMemoryObservabilitySink();
            var service = new CharacterInventoryService(store, sink);
            var result = await service.LoadForSessionAsync(Guid.NewGuid(), "pid_henry", "cid_corrupt");

            Assert.True(result.Loaded);
            Assert.Equal(2, result.DroppedItemCount);
            Assert.Equal(0, result.DroppedContainerCount);

            var loadedContainer = Assert.Single(result.Inventory!.Containers);
            var loadedItem = Assert.Single(loadedContainer.Items);
            Assert.Equal("it_valid", loadedItem.InternalId);

            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.InventoryValidationFailed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpsertItemAsync_AndSaveUnload_PersistsAcrossSessions()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new CharacterInventoryService(store, sink);

            var s1 = Guid.NewGuid();
            var s2 = Guid.NewGuid();

            var loaded = await service.LoadForSessionAsync(s1, "pid_henry", "cid_henry");
            Assert.True(loaded.Loaded);

            var item = new InventoryItemRecord
            {
                InternalId = "it_lockpick_01",
                ItemType = "tool",
                ItemRef = "tool.lockpick",
                Quantity = 5,
                IsStackable = true,
            };

            var mainMutation = await service.UpsertItemAsync(s1, "main", item);
            Assert.True(mainMutation.Applied);

            var chestMutation = await service.UpsertItemAsync(
                s1,
                "chest_home",
                new InventoryItemRecord
                {
                    InternalId = "it_book_01",
                    ItemType = "book",
                    ItemRef = "book.herbalism",
                    Quantity = 1,
                    IsStackable = false,
                });
            Assert.True(chestMutation.Applied);

            var saved = await service.SaveAndUnloadSessionAsync(s1, CharacterLifecycleSaveReason.SessionClosed);
            Assert.True(saved.Saved);

            var reloaded = await service.LoadForSessionAsync(s2, "pid_henry", "cid_henry");
            Assert.True(reloaded.Loaded);
            Assert.NotNull(reloaded.Inventory);

            var main = Assert.Single(reloaded.Inventory!.Containers.Where(x => x.ContainerId == "main"));
            var persistedStack = Assert.Single(main.Items.Where(x => x.InternalId == "it_lockpick_01"));
            Assert.Equal(5, persistedStack.Quantity);

            var chest = Assert.Single(reloaded.Inventory.Containers.Where(x => x.ContainerId == "chest_home"));
            Assert.Single(chest.Items.Where(x => x.InternalId == "it_book_01"));

            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.InventoryChanged);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_inventory_{Guid.NewGuid():N}");
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
