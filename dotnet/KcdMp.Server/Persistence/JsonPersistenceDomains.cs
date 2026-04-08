namespace KcdMp.Server.Persistence;

/// <summary>
/// Canonical storage domains for server-side persistence layout.
/// </summary>
public static class JsonPersistenceDomains
{
    public const string Identity = "identity";
    public const string Characters = "characters";
    public const string Inventory = "inventory";
    public const string InventoryRules = "inventory_rules";
    public const string WorldContainers = "world_containers";
    public const string Currency = "currency";
    public const string Respawn = "respawn";
    public const string Crime = "crime";
    public const string Bans = "bans";
    public const string Economy = "economy";
    public const string Audit = "audit";
    public const string Config = "config";

    public static IReadOnlyList<string> Default { get; } =
    [
        Identity,
        Characters,
        Inventory,
        InventoryRules,
        WorldContainers,
        Currency,
        Respawn,
        Crime,
        Bans,
        Economy,
        Audit,
        Config,
    ];
}
