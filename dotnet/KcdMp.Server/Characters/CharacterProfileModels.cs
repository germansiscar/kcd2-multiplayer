using System.Text.Json.Serialization;

namespace KcdMp.Server.Characters;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterProfileStatus
{
    Active = 0,
    Inactive = 1,
    Disabled = 2,
}

public sealed class CharacterProfileRecord
{
    public string InternalId { get; set; } = "";
    public string IdentityId { get; set; } = "";
    public string FullName { get; set; } = "";
    public string NormalizedFullName { get; set; } = "";
    public string ModelKey { get; set; } = "";
    public CharacterProfileStatus Status { get; set; } = CharacterProfileStatus.Inactive;
    public bool IsDeleted { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastActivityAtUtc { get; set; }
}

public sealed class CharacterNameLookupIndex
{
    public Dictionary<string, string> NameToCharacterId { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed record CharacterCreateRequest(
    string GivenName,
    string FamilyName,
    string ModelKey,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record CharacterCreateResult(
    bool Created,
    string? DenialReason,
    CharacterProfileRecord? Character);
