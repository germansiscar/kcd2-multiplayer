using KcdMp.Server.Respawn;
using System.Text.Json.Serialization;

namespace KcdMp.Server.InventoryRules;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryCharacterMode
{
    PersistentNonLootable = 0,
    PersistentLootableWhenUnconscious = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryContainerKind
{
    CharacterInventory = 0,
    Chest = 1,
    Generic = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryCharacterRuleState
{
    Normal = 0,
    Protected = 1,
    Lootable = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryRuleTrigger
{
    SessionLoad = 0,
    DefeatEntered = 1,
    HealerRecovered = 2,
    RespawnApplied = 3,
    RespawnPending = 4,
    Shutdown = 5,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryContainerAccessState
{
    Denied = 0,
    KeyAuthorized = 1,
    OpenAuthorized = 2,
    LockpickAuthorized = 3,
    PolicyAuthorized = 4,
}

public sealed class InventoryContainerRuleRecord
{
    public string ContainerTypeId { get; set; } = "";
    public InventoryContainerKind ContainerKind { get; set; } = InventoryContainerKind.Generic;
    public bool AffectedByDefeat { get; set; } = true;
    public bool RequiresValidKey { get; set; } = false;
    public bool AllowAccessWhenFullyOpen { get; set; } = false;
    public bool AllowAccessWhenLockpickSucceeded { get; set; } = false;
    public bool AllowMultipleKeyHolders { get; set; } = true;
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class InventoryRulesConfigurationRecord
{
    public string ConfigVersion { get; set; } = "inventory_rules_v1";
    public InventoryCharacterMode CharacterMode { get; set; } = InventoryCharacterMode.PersistentNonLootable;
    public Dictionary<string, InventoryContainerRuleRecord> ContainerRules { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class CharacterInventoryRuleStateRecord
{
    public string CharacterId { get; set; } = "";
    public InventoryCharacterRuleState State { get; set; } = InventoryCharacterRuleState.Normal;
    public bool IsLootable { get; set; }
    public CharacterDefeatState LastDefeatState { get; set; } = CharacterDefeatState.Alive;
    public InventoryRuleTrigger LastTrigger { get; set; } = InventoryRuleTrigger.SessionLoad;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastSavedAtUtc { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class InventoryRulesOptions
{
    public string ConfigDocumentId { get; init; } = "inventory_rules_v1";
    public int SaveRetryCount { get; init; } = 1;
    public TimeSpan SaveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}

public sealed record InventoryRuleApplyResult(
    bool Applied,
    string? DenialReason,
    CharacterInventoryRuleStateRecord? State,
    bool Changed);

public sealed record InventoryContainerAccessEvaluationRequest(
    string ContainerTypeId,
    InventoryContainerKind ContainerKind,
    bool HasValidKey,
    bool IsFullyOpen,
    bool LockpickSucceeded);

public sealed record InventoryContainerAccessEvaluationResult(
    bool Allowed,
    InventoryContainerAccessState AccessState,
    string RuleId,
    string? DenialReason);
