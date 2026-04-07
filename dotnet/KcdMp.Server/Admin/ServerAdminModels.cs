using KcdMp.Server.Bans;
using KcdMp.Server.Characters;
using KcdMp.Server.Identity;
using KcdMp.Server.Sessions;
using KcdMp.Server.Audit;

namespace KcdMp.Server.Admin;

public sealed record AdminActionResult(
    bool Success,
    string? Error);

public sealed record AdminActionResult<T>(
    bool Success,
    string? Error,
    T? Value);

public sealed record AdminBanDetails(
    string IdentityId,
    string? ActiveBanId,
    IReadOnlyList<IdentityBanEntry> Entries);

public sealed record AdminAuditQuery(
    string? IdentityId = null,
    string? CharacterId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? EventType = null);

public sealed record AdminSessionSnapshot(
    Guid SessionId,
    string? IdentityId,
    string? CharacterId,
    string? RemoteEndpoint,
    ServerSessionState State,
    ServerSessionAuthState AuthState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActivityAtUtc);

public sealed record AdminPlayerMessage(
    string Message,
    string Code);

public sealed record AdminBanApplyRequest(
    string IdentityId,
    IdentityBanType Type,
    TimeSpan? Duration,
    string Reason,
    string? Summary = null,
    string? Notes = null,
    IReadOnlyCollection<string>? AuditEventIds = null);

public sealed record AdminAuditQueryResult(
    IReadOnlyList<ServerAuditEventRecord> Events,
    int TotalMatched);
