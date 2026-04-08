using KcdMp.Server.Observability;

namespace KcdMp.Server.Audit;

public enum ServerAuditCategory
{
    Access = 0,
    Identity = 1,
    Character = 2,
    Inventory = 3,
    Currency = 4,
    Respawn = 5,
    Persistence = 6,
    Error = 7,
    Communication = 8,
}

public enum ServerAuditResult
{
    Ok = 0,
    Fail = 1,
}

public sealed record ServerAuditEventRecord(
    string AuditEventId,
    DateTimeOffset TimestampUtc,
    string EventType,
    ServerAuditCategory Category,
    ServerObservableSeverity Severity,
    ServerAuditResult Result,
    Guid? SessionId = null,
    string? IdentityId = null,
    string? CharacterId = null,
    string? ActorId = null,
    string? TargetId = null,
    IReadOnlyDictionary<string, object?>? Payload = null,
    string? Message = null);

