using KcdMp.Server.Inventory;
using KcdMp.Server.Respawn;
using System.Text.Json.Serialization;

namespace KcdMp.Server.Loot;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChestLockState
{
    Open = 0,
    ClosedUnlocked = 1,
    ClosedLocked = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LootTargetKind
{
    Character = 0,
    Chest = 1,
}

public sealed class LootableChestPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class LootableChestRecord
{
    public string ChestId { get; set; } = "";
    public string ContainerTypeId { get; set; } = "chest";
    public string KeyId { get; set; } = "";
    public ChestLockState State { get; set; } = ChestLockState.ClosedLocked;
    public LootableChestPosition Position { get; set; } = new();
    public List<InventoryItemRecord> Items { get; set; } = [];
    public long CurrencyBalance { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastSavedAtUtc { get; set; }
}

public sealed class LootableInventoryOptions
{
    public string DefaultDestinationContainerId { get; init; } = "main";
    public double MaxInteractionDistanceMeters { get; init; } = 3.5;
    public int SaveRetryCount { get; init; } = 1;
    public TimeSpan SaveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    // Test hook for deterministic target lock contention checks.
    public TimeSpan SimulatedTransferDelay { get; init; } = TimeSpan.Zero;
}

public sealed record LootTransferSummary(
    LootTargetKind TargetKind,
    string TargetId,
    string ItemInternalId,
    int QuantityTransferred,
    long CurrencyTransferred);

public sealed record LootTransferResult(
    bool Applied,
    string? DenialReason,
    LootTransferSummary? Summary);

public sealed record ChestUpsertResult(
    bool Applied,
    string? DenialReason,
    LootableChestRecord? Chest);

public sealed record LootCharacterItemTransferRequest(
    Guid LooterSessionId,
    Guid TargetSessionId,
    string LooterIdentityId,
    string LooterCharacterId,
    string TargetIdentityId,
    string TargetCharacterId,
    string SourceContainerId,
    string DestinationContainerId,
    string ItemInternalId,
    int Quantity,
    double DistanceMeters,
    bool StealSucceeded,
    CharacterDefeatState? ExpectedTargetState);

public sealed record LootCharacterCurrencyTransferRequest(
    Guid LooterSessionId,
    Guid TargetSessionId,
    string LooterIdentityId,
    string LooterCharacterId,
    string TargetIdentityId,
    string TargetCharacterId,
    long Amount,
    double DistanceMeters,
    bool StealSucceeded,
    CharacterDefeatState? ExpectedTargetState);

public sealed record LootChestItemTransferRequest(
    Guid LooterSessionId,
    string LooterIdentityId,
    string LooterCharacterId,
    string ChestId,
    string DestinationContainerId,
    string ItemInternalId,
    int Quantity,
    double DistanceMeters,
    bool HasValidKey,
    bool IsFullyOpen,
    bool LockpickSucceeded,
    ChestLockState? ExpectedChestState);

public sealed record LootChestCurrencyTransferRequest(
    Guid LooterSessionId,
    string LooterIdentityId,
    string LooterCharacterId,
    string ChestId,
    long Amount,
    double DistanceMeters,
    bool HasValidKey,
    bool IsFullyOpen,
    bool LockpickSucceeded,
    ChestLockState? ExpectedChestState);
