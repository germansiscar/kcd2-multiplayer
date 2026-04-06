using System.Collections.Concurrent;
using KcdMp.Server.InventoryRules;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using KcdMp.Server.Respawn;

namespace KcdMp.Tests.Server;

public sealed class InventoryRulesConfigurationServiceTests
{
    [Fact]
    public async Task GetActiveConfigurationAsync_WhenMissing_CreatesDefaultConfig()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new InventoryRulesConfigurationService(store, sink);

            var config = await service.GetActiveConfigurationAsync();

            Assert.Equal(InventoryCharacterMode.PersistentNonLootable, config.CharacterMode);
            Assert.True(config.ContainerRules.ContainsKey("character_main"));
            Assert.True(config.ContainerRules.ContainsKey("chest"));
            Assert.False(config.ContainerRules["chest"].AffectedByDefeat);
            Assert.True(config.ContainerRules["chest"].RequiresValidKey);

            var persisted = await store.LoadAsync<InventoryRulesConfigurationRecord>(
                JsonPersistenceDomains.Config,
                "inventory_rules_v1");
            Assert.NotNull(persisted);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.InventoryRulesConfigLoaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyCharacterDefeatStateAsync_WithLootableMode_ChangesToLootableOnlyDuringUnconscious()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
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
            await store.SaveAsync(JsonPersistenceDomains.Config, "inventory_rules_v1", config);

            var service = new InventoryRulesConfigurationService(store, sink);
            var sessionId = Guid.NewGuid();

            var unconscious = await service.ApplyCharacterDefeatStateAsync(
                sessionId,
                "pid_henry",
                "cid_henry",
                CharacterDefeatState.Unconscious,
                InventoryRuleTrigger.DefeatEntered);
            Assert.True(unconscious.Applied);
            Assert.NotNull(unconscious.State);
            Assert.True(unconscious.State!.IsLootable);
            Assert.Equal(InventoryCharacterRuleState.Lootable, unconscious.State.State);

            var respawned = await service.ApplyCharacterDefeatStateAsync(
                sessionId,
                "pid_henry",
                "cid_henry",
                CharacterDefeatState.Alive,
                InventoryRuleTrigger.RespawnApplied);
            Assert.True(respawned.Applied);
            Assert.NotNull(respawned.State);
            Assert.False(respawned.State!.IsLootable);
            Assert.Equal(InventoryCharacterRuleState.Normal, respawned.State.State);

            var persisted = await store.LoadAsync<CharacterInventoryRuleStateRecord>(
                JsonPersistenceDomains.InventoryRules,
                "cid_henry");
            Assert.NotNull(persisted);
            Assert.False(persisted!.IsLootable);
            Assert.Equal(CharacterDefeatState.Alive, persisted.LastDefeatState);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.InventoryRuleApplied);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.InventoryLootabilityChanged);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateContainerAccessAsync_ChestRule_AllowsKeyOrOpenOrLockpick()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new InventoryRulesConfigurationService(store, sink);
            await service.GetActiveConfigurationAsync();

            var denied = await service.EvaluateContainerAccessAsync(new InventoryContainerAccessEvaluationRequest(
                "chest",
                InventoryContainerKind.Chest,
                HasValidKey: false,
                IsFullyOpen: false,
                LockpickSucceeded: false));
            Assert.False(denied.Allowed);

            var key = await service.EvaluateContainerAccessAsync(new InventoryContainerAccessEvaluationRequest(
                "chest",
                InventoryContainerKind.Chest,
                HasValidKey: true,
                IsFullyOpen: false,
                LockpickSucceeded: false));
            Assert.True(key.Allowed);
            Assert.Equal(InventoryContainerAccessState.KeyAuthorized, key.AccessState);

            var open = await service.EvaluateContainerAccessAsync(new InventoryContainerAccessEvaluationRequest(
                "chest",
                InventoryContainerKind.Chest,
                HasValidKey: false,
                IsFullyOpen: true,
                LockpickSucceeded: false));
            Assert.True(open.Allowed);
            Assert.Equal(InventoryContainerAccessState.OpenAuthorized, open.AccessState);

            var lockpick = await service.EvaluateContainerAccessAsync(new InventoryContainerAccessEvaluationRequest(
                "chest",
                InventoryContainerKind.Chest,
                HasValidKey: false,
                IsFullyOpen: false,
                LockpickSucceeded: true));
            Assert.True(lockpick.Allowed);
            Assert.Equal(InventoryContainerAccessState.LockpickAuthorized, lockpick.AccessState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetActiveConfigurationAsync_WhenInvalidConfig_ThrowsAndEmitsValidationEvent()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var malformed = new InventoryRulesConfigurationRecord
            {
                ConfigVersion = "inventory_rules_v1",
                CharacterMode = InventoryCharacterMode.PersistentNonLootable,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ContainerRules = new Dictionary<string, InventoryContainerRuleRecord>(StringComparer.Ordinal)
                {
                    ["invalid"] = new()
                    {
                        ContainerTypeId = "",
                    },
                },
            };
            await store.SaveAsync(JsonPersistenceDomains.Config, "inventory_rules_v1", malformed);

            var service = new InventoryRulesConfigurationService(store, sink);
            await Assert.ThrowsAsync<PersistenceValidationException>(() => service.GetActiveConfigurationAsync());
            Assert.Contains(
                sink.Events,
                evt => evt.Type == ServerObservableEventType.InventoryRulesConfigValidationFailed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_inventory_rules_{Guid.NewGuid():N}");
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
