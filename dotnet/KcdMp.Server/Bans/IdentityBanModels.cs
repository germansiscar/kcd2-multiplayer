using System.Text.Json.Serialization;

namespace KcdMp.Server.Bans;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IdentityBanType
{
    Temporary = 0,
    Permanent = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IdentityBanStatus
{
    Active = 0,
    Expired = 1,
    Revoked = 2,
}

public sealed class IdentityBanEntry
{
    public string BanId { get; set; } = "";
    public string IdentityId { get; set; } = "";
    public IdentityBanType Type { get; set; } = IdentityBanType.Permanent;
    public IdentityBanStatus Status { get; set; } = IdentityBanStatus.Active;
    public string Reason { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string? Summary { get; set; }
    public string? Notes { get; set; }
    public List<string> AuditEventIds { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? ExpiredAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedByActorId { get; set; }
    public string? RevocationReason { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class IdentityBanHistoryRecord
{
    public string ConfigVersion { get; set; } = "identity_bans_v1";
    public string IdentityId { get; set; } = "";
    public string? ActiveBanId { get; set; }
    public List<IdentityBanEntry> Bans { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class IdentityBanOptions
{
    public string UserFacingDeniedMessage { get; init; } = "Access denied by server moderation policy.";
    public int MaxUserFacingMessageLength { get; init; } = 96;
}

public sealed record IdentityBanApplyRequest(
    string IdentityId,
    IdentityBanType Type,
    TimeSpan? Duration,
    string Reason,
    string ActorId,
    string? Summary = null,
    string? Notes = null,
    IReadOnlyCollection<string>? AuditEventIds = null);

public sealed record IdentityBanApplyResult(
    bool Applied,
    string? DenialReason,
    IdentityBanEntry? Ban);

public sealed record IdentityBanRevocationRequest(
    string IdentityId,
    string ActorId,
    string RevocationReason);

public sealed record IdentityBanRevocationResult(
    bool Revoked,
    string? DenialReason,
    IdentityBanEntry? Ban);

public sealed record IdentityBanAccessEvaluationResult(
    bool IsDenied,
    string? DenialReason,
    IdentityBanEntry? ActiveBan);
