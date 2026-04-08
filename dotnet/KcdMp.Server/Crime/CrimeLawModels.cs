using System.Text.Json.Serialization;

namespace KcdMp.Server.Crime;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CrimeType
{
    TheftFromConsciousCharacter = 0,
    LootFromUnconsciousCharacter = 1,
    UnauthorizedContainerAccess = 2,
    ConfiguredIllegalAction = 3,
    ManualStaffMark = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CrimeSource
{
    Automatic = 0,
    Manual = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CrimeTargetKind
{
    None = 0,
    Character = 1,
    Container = 2,
    Action = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterCrimeStatus
{
    Clean = 0,
    Wanted = 1,
}

public sealed class CrimeEventRecord
{
    public string CrimeEventId { get; set; } = "";
    public CrimeType CrimeType { get; set; } = CrimeType.ConfiguredIllegalAction;
    public CrimeSource Source { get; set; } = CrimeSource.Automatic;
    public CrimeTargetKind TargetKind { get; set; } = CrimeTargetKind.None;
    public string? TargetId { get; set; }
    public string? TargetIdentityId { get; set; }
    public string? TargetCharacterId { get; set; }
    public string? ActionCode { get; set; }
    public string? Reason { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CharacterCrimeRecord
{
    public string CharacterId { get; set; } = "";
    public string IdentityId { get; set; } = "";
    public CharacterCrimeStatus Status { get; set; } = CharacterCrimeStatus.Clean;
    public DateTimeOffset? WantedUntilUtc { get; set; }
    public DateTimeOffset? LastCrimeAtUtc { get; set; }
    public int TotalCrimeCount { get; set; }
    public List<CrimeEventRecord> Events { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastSavedAtUtc { get; set; }
}

public sealed class CrimeLawConfigurationRecord
{
    public string ConfigVersion { get; set; } = "crime_law_v1";
    public int WantedDurationSeconds { get; set; } = 3600;
    public int DedupWindowSeconds { get; set; } = 3;
    public int MaxEventsPerCharacter { get; set; } = 256;
    public bool AutoDetectConsciousCharacterTheft { get; set; } = true;
    public bool AutoDetectUnconsciousCharacterLoot { get; set; } = true;
    public bool AutoDetectUnauthorizedContainerAccess { get; set; } = true;
    public List<string> ConfiguredIllegalActions { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class CrimeLawOptions
{
    public string ConfigId { get; init; } = "crime_law_v1";
    public int WantedDurationSeconds { get; init; } = 3600;
    public int DedupWindowSeconds { get; init; } = 3;
    public int MaxEventsPerCharacter { get; init; } = 256;
    public bool AutoDetectConsciousCharacterTheft { get; init; } = true;
    public bool AutoDetectUnconsciousCharacterLoot { get; init; } = true;
    public bool AutoDetectUnauthorizedContainerAccess { get; init; } = true;
    public IReadOnlyCollection<string> ConfiguredIllegalActions { get; init; } = [];
}

public sealed record AutomaticCrimeRegistrationRequest(
    Guid? SessionId,
    string IdentityId,
    string CharacterId,
    CrimeType CrimeType,
    CrimeTargetKind TargetKind,
    string? TargetId = null,
    string? TargetIdentityId = null,
    string? TargetCharacterId = null,
    string? ActionCode = null,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record ManualCrimeRegistrationRequest(
    Guid? SessionId,
    string ActorIdentityId,
    string IdentityId,
    string CharacterId,
    CrimeType CrimeType,
    CrimeTargetKind TargetKind,
    string? TargetId = null,
    string? TargetIdentityId = null,
    string? TargetCharacterId = null,
    string? ActionCode = null,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record CrimeStateClearRequest(
    Guid? SessionId,
    string ActorIdentityId,
    string IdentityId,
    string CharacterId,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record IllegalActionCrimeRequest(
    Guid? SessionId,
    string IdentityId,
    string CharacterId,
    string ActionCode,
    CrimeTargetKind TargetKind,
    string? TargetId = null,
    string? TargetIdentityId = null,
    string? TargetCharacterId = null,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record CrimeRegistrationResult(
    bool Applied,
    string? DenialReason,
    CharacterCrimeRecord? Record,
    CrimeEventRecord? CrimeEvent,
    bool StatusChanged,
    string? PlayerFeedback);

public sealed record CrimeStateChangeResult(
    bool Applied,
    string? DenialReason,
    CharacterCrimeRecord? Record,
    bool StatusChanged,
    string? PlayerFeedback);
