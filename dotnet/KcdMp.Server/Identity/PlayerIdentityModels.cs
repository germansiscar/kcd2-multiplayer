using KcdMp.Server.AccessControl;
using System.Text.Json.Serialization;

namespace KcdMp.Server.Identity;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlayerIdentityStatus
{
    Active = 0,
    Blocked = 1,
    Pending = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlayerIdentityExternalKind
{
    SteamId = 0,
    PersistentToken = 1,
    PlayerNameFallback = 2,
}

public sealed class PlayerIdentityRecord
{
    public string InternalId { get; set; } = "";
    public PlayerIdentityStatus Status { get; set; } = PlayerIdentityStatus.Active;
    public PlayerIdentityExternalKind ExternalKind { get; set; } = PlayerIdentityExternalKind.PlayerNameFallback;
    public string ExternalKeyHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> CharacterIds { get; set; } = [];
    public bool IsDeactivated { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
}

public sealed class PlayerIdentityLookupIndex
{
    public Dictionary<string, string> ExternalKeyToInternalId { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed record PlayerIdentityClaim(
    string DisplayName,
    string? PersistentToken,
    string? SteamId);

public sealed class PlayerIdentityOptions
{
    public bool AutoCreateWhenMissing { get; init; } = true;
    public PlayerIdentityStatus NewIdentityStatusWhenOpenMode { get; init; } = PlayerIdentityStatus.Active;
    public PlayerIdentityStatus NewIdentityStatusWhenWhitelistMode { get; init; } = PlayerIdentityStatus.Pending;
}

public sealed record PlayerIdentityResolution(
    bool IsAllowed,
    string? DenialReason,
    bool Created,
    PlayerIdentityRecord? Identity,
    PlayerIdentityExternalKind IdentificationKind);
