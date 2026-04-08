using System.Collections.Concurrent;
using KcdMp.Server.Currency;
using KcdMp.Server.Inventory;
using KcdMp.Server.InventoryRules;
using KcdMp.Server.Loot;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using KcdMp.Server.Respawn;

namespace KcdMp.Tests.Server;

public sealed class LootableInventoryServiceTests
{
    [Fact]
    public async Task TransferItemFromCharacterAsync_UnconsciousTarget_TransfersStackAndPersists()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            SeedLootableConfig(store);

            var inventory = new CharacterInventoryService(store, sink);
            var currency = new CharacterCurrencyService(store, sink);
            var rules = new InventoryRulesConfigurationService(store, sink);
            var respawn = new CharacterRespawnService(store, sink, inventoryRulesService: rules);
            var loot = new LootableInventoryService(store, inventory, currency, respawn, rules, sink);

            var looterSession = Guid.NewGuid();
            var targetSession = Guid.NewGuid();
            await inventory.LoadForSessionAsync(looterSession, "pid_looter", "cid_looter");
            await inventory.LoadForSessionAsync(targetSession, "pid_target", "cid_target");
            await respawn.LoadForSessionAsync(looterSession, "pid_looter", "cid_looter");
            await respawn.LoadForSessionAsync(targetSession, "pid_target", "cid_target");
            await respawn.RegisterDefeatAsync(targetSession, 0);

            await inventory.UpsertItemAsync(
                targetSession,
                "main",
                new InventoryItemRecord
                {
                    InternalId = "it_apple",
                    ItemType = "consumable",
                    ItemRef = "food.apple",
                    Quantity = 5,
                    IsStackable = true,
                });

            var result = await loot.TransferItemFromCharacterAsync(new LootCharacterItemTransferRequest(
                LooterSessionId: looterSession,
                TargetSessionId: targetSession,
                LooterIdentityId: "pid_looter",
                LooterCharacterId: "cid_looter",
                TargetIdentityId: "pid_target",
                TargetCharacterId: "cid_target",
                SourceContainerId: "main",
                DestinationContainerId: "main",
                ItemInternalId: "it_apple",
                Quantity: 3,
                DistanceMeters: 1.2,
                StealSucceeded: false,
                ExpectedTargetState: CharacterDefeatState.Unconscious));

            Assert.True(result.Applied);
            Assert.NotNull(result.Summary);
            Assert.Equal(3, result.Summary!.QuantityTransferred);

            var targetInventory = await inventory.GetLoadedForSessionAsync(targetSession);
            var looterInventory = await inventory.GetLoadedForSessionAsync(looterSession);
            Assert.NotNull(targetInventory);
            Assert.NotNull(looterInventory);

            var remaining = targetInventory!.Containers.SelectMany(c => c.Items).Single(x => x.InternalId == "it_apple");
            Assert.Equal(2, remaining.Quantity);

            var looterApples = looterInventory!.Containers.SelectMany(c => c.Items).Where(x => x.ItemRef == "food.apple").ToList();
            Assert.Single(looterApples);
            Assert.Equal(3, looterApples[0].Quantity);

            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.LootItemTransferred);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransferItemFromCharacterAsync_ConsciousWithoutSteal_IsDenied()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            SeedLootableConfig(store);

            var inventory = new CharacterInventoryService(store, sink);
            var currency = new CharacterCurrencyService(store, sink);
            var rules = new InventoryRulesConfigurationService(store, sink);
            var respawn = new CharacterRespawnService(store, sink, inventoryRulesService: rules);
            var loot = new LootableInventoryService(store, inventory, currency, respawn, rules, sink);

            var looterSession = Guid.NewGuid();
            var targetSession = Guid.NewGuid();
            await inventory.LoadForSessionAsync(looterSession, "pid_looter", "cid_looter");
            await inventory.LoadForSessionAsync(targetSession, "pid_target", "cid_target");
            await respawn.LoadForSessionAsync(looterSession, "pid_looter", "cid_looter");
            await respawn.LoadForSessionAsync(targetSession, "pid_target", "cid_target");
            await inventory.UpsertItemAsync(
                targetSession,
                "main",
                new InventoryItemRecord
                {
                    InternalId = "it_ring",
                    ItemType = "misc",
                    ItemRef = "trinket.ring",
                    Quantity = 1,
                    IsStackable = false,
                });

            var result = await loot.TransferItemFromCharacterAsync(new LootCharacterItemTransferRequest(
                looterSession,
                targetSession,
                "pid_looter",
                "cid_looter",
                "pid_target",
                "cid_target",
                "main",
                "main",
                "it_ring",
                1,
                1.0,
                StealSucceeded: false,
                ExpectedTargetState: CharacterDefeatState.Alive));

            Assert.False(result.Applied);
            Assert.Equal("Target is not currently lootable.", result.DenialReason);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.LootDenied);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransferCurrencyFromCharacterAsync_ConsciousWithSteal_UpdatesPersistentBalances()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            SeedLootableConfig(store);

            var inventory = new CharacterInventoryService(store, sink);
            var currency = new CharacterCurrencyService(store, sink, new CharacterCurrencyOptions { InitialBalance = 0 });
            var rules = new InventoryRulesConfigurationService(store, sink);
            var respawn = new CharacterRespawnService(store, sink, inventoryRulesService: rules);
            var loot = new LootableInventoryService(store, inventory, currency, respawn, rules, sink);

            var looterSession = Guid.NewGuid();
            var targetSession = Guid.NewGuid();
            await inventory.LoadForSessionAsync(looterSession, "pid_looter", "cid_looter");
            await inventory.LoadForSessionAsync(targetSession, "pid_target", "cid_target");
            await respawn.LoadForSessionAsync(looterSession, "pid_looter", "cid_looter");
            await respawn.LoadForSessionAsync(targetSession, "pid_target", "cid_target");
            await currency.LoadForSessionAsync(looterSession, "pid_looter", "cid_looter");
            await currency.LoadForSessionAsync(targetSession, "pid_target", "cid_target");
            await currency.SetBalanceAsync(targetSession, 120);
            await currency.SetBalanceAsync(looterSession, 10);

            var result = await loot.TransferCurrencyFromCharacterAsync(new LootCharacterCurrencyTransferRequest(
                looterSession,
                targetSession,
                "pid_looter",
                "cid_looter",
                "pid_target",
                "cid_target",
                Amount: 45,
                DistanceMeters: 1.1,
                StealSucceeded: true,
                ExpectedTargetState: CharacterDefeatState.Alive));

            Assert.True(result.Applied);
            Assert.NotNull(result.Summary);
            Assert.Equal(45, result.Summary!.CurrencyTransferred);

            var looterCurrency = await currency.GetLoadedForSessionAsync(looterSession);
            var targetCurrency = await currency.GetLoadedForSessionAsync(targetSession);
            Assert.Equal(55, looterCurrency!.Balance);
            Assert.Equal(75, targetCurrency!.Balance);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransferItemFromChestAsync_WithValidKey_TransfersAndConcurrentAccessIsBlocked()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            SeedLootableConfig(store);

            var inventory = new CharacterInventoryService(store, sink);
            var currency = new CharacterCurrencyService(store, sink);
            var rules = new InventoryRulesConfigurationService(store, sink);
            var respawn = new CharacterRespawnService(store, sink, inventoryRulesService: rules);
            var loot = new LootableInventoryService(
                store,
                inventory,
                currency,
                respawn,
                rules,
                sink,
                new LootableInventoryOptions { SimulatedTransferDelay = TimeSpan.FromMilliseconds(200) });

            var looterSession = Guid.NewGuid();
            await inventory.LoadForSessionAsync(looterSession, "pid_looter", "cid_looter");
            await respawn.LoadForSessionAsync(looterSession, "pid_looter", "cid_looter");

            var chest = new LootableChestRecord
            {
                ChestId = "chest_home",
                ContainerTypeId = "chest",
                KeyId = "key_home",
                State = ChestLockState.ClosedLocked,
                CurrencyBalance = 0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Items =
                [
                    new InventoryItemRecord
                    {
                        InternalId = "it_book",
                        ItemType = "book",
                        ItemRef = "book.herbalism",
                        Quantity = 1,
                        IsStackable = false,
                    },
                ],
            };
            var seeded = await loot.UpsertChestAsync(chest);
            Assert.True(seeded.Applied);

            var first = loot.TransferItemFromChestAsync(new LootChestItemTransferRequest(
                looterSession,
                "pid_looter",
                "cid_looter",
                "chest_home",
                "main",
                "it_book",
                1,
                1.0,
                HasValidKey: true,
                IsFullyOpen: false,
                LockpickSucceeded: false,
                ExpectedChestState: ChestLockState.ClosedLocked));

            await Task.Delay(20);

            var second = await loot.TransferItemFromChestAsync(new LootChestItemTransferRequest(
                looterSession,
                "pid_looter",
                "cid_looter",
                "chest_home",
                "main",
                "it_book",
                1,
                1.0,
                HasValidKey: true,
                IsFullyOpen: false,
                LockpickSucceeded: false,
                ExpectedChestState: ChestLockState.ClosedLocked));

            var firstResult = await first;
            Assert.True(firstResult.Applied);
            Assert.False(second.Applied);
            Assert.Equal("Container is currently in use.", second.DenialReason);

            var updatedChest = await loot.GetChestAsync("chest_home");
            Assert.NotNull(updatedChest);
            Assert.Empty(updatedChest!.Items);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.LootAccessConflict);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SeedLootableConfig(IJsonPersistenceStore store)
    {
        var config = new InventoryRulesConfigurationRecord
        {
            ConfigVersion = "inventory_rules_v1",
            CharacterMode = InventoryCharacterMode.PersistentLootableWhenUnconscious,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ContainerRules = new Dictionary<string, InventoryContainerRuleRecord>(StringComparer.Ordinal)
            {
                ["character_main"] = new()
                {
                    ContainerTypeId = "character_main",
                    ContainerKind = InventoryContainerKind.CharacterInventory,
                },
                ["chest"] = new()
                {
                    ContainerTypeId = "chest",
                    ContainerKind = InventoryContainerKind.Chest,
                    AffectedByDefeat = false,
                    RequiresValidKey = true,
                    AllowAccessWhenFullyOpen = true,
                    AllowAccessWhenLockpickSucceeded = true,
                },
            },
        };
        store.SaveAsync(JsonPersistenceDomains.Config, "inventory_rules_v1", config).GetAwaiter().GetResult();
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_loot_{Guid.NewGuid():N}");
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
