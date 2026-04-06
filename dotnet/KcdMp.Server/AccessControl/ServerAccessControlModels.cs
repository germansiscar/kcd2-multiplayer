using System.Text.Json.Serialization;
using KcdMp.Server.Identity;

namespace KcdMp.Server.AccessControl;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServerAccessMode
{
    Open = 0,
    Whitelist = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServerAccessDecisionReason
{
    AllowedActiveIdentity = 0,
    AllowedPendingIdentityInOpenMode = 1,
    AllowedNewIdentityInOpenMode = 2,
    DeniedBlockedIdentity = 3,
    DeniedPendingIdentityInWhitelistMode = 4,
    DeniedNewIdentityInWhitelistMode = 5,
    DeniedDuplicateActiveSession = 6,
}

public sealed class ServerAccessControlConfigurationRecord
{
    public string ConfigVersion { get; set; } = "access_control_v1";
    public ServerAccessMode AccessMode { get; set; } = ServerAccessMode.Open;
    public bool DenyPendingIdentityInWhitelistMode { get; set; } = true;
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ServerAccessControlOptions
{
    public string ConfigDocumentId { get; init; } = "access_control_v1";
    public ServerAccessMode DefaultAccessMode { get; init; } = ServerAccessMode.Open;
    public bool DenyPendingIdentityInWhitelistMode { get; init; } = true;
}

public sealed record ServerAccessEvaluationResult(
    bool IsAllowed,
    ServerAccessDecisionReason Reason,
    string? DenialReason);

public sealed record ServerAccessIdentityEvaluationRequest(
    Guid SessionId,
    string? IdentityId,
    PlayerIdentityStatus IdentityStatus,
    bool IsDeactivated,
    bool IdentityCreatedInThisAttempt);
