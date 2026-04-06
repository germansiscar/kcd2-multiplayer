using System.Text.Json.Serialization;

namespace KcdMp.Server.Inventory;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryContainerType
{
    Primary = 0,
    Storage = 1,
    Temporary = 2,
}

public sealed class InventoryItemRecord
{
    public string InternalId { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string ItemRef { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public bool IsStackable { get; set; } = true;
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public List<string> Flags { get; set; } = [];
}

public sealed class InventoryContainerRecord
{
    public string ContainerId { get; set; } = "";
    public InventoryContainerType ContainerType { get; set; } = InventoryContainerType.Primary;
    public string DisplayName { get; set; } = "";
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public List<InventoryItemRecord> Items { get; set; } = [];
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class CharacterInventoryRecord
{
    public string CharacterId { get; set; } = "";
    public List<InventoryContainerRecord> Containers { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastSavedAtUtc { get; set; }
}

public sealed class CharacterInventoryOptions
{
    public string BaseContainerId { get; init; } = "main";
    public string BaseContainerDisplayName { get; init; } = "Main Inventory";

    /// <summary>
    /// Number of retries after the first failed save attempt.
    /// </summary>
    public int SaveRetryCount { get; init; } = 1;

    /// <summary>
    /// Delay between save retries.
    /// </summary>
    public TimeSpan SaveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}

public sealed record CharacterInventoryLoadResult(
    bool Loaded,
    string? DenialReason,
    CharacterInventoryRecord? Inventory,
    int DroppedContainerCount,
    int DroppedItemCount);

public sealed record CharacterInventorySaveResult(
    bool Saved,
    string? FailureReason,
    int Attempts);

public sealed record CharacterInventoryMutationResult(
    bool Applied,
    string? DenialReason,
    CharacterInventoryRecord? Inventory);
