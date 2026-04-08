using KcdMp.Server.Bans;
using KcdMp.Server.Characters;
using KcdMp.Server.Crime;
using KcdMp.Server.Identity;

namespace KcdMp.Server.Admin;

public interface IServerAdminService
{
    Task<AdminActionResult<IReadOnlyList<PlayerIdentityRecord>>> ListPendingWhitelistAsync(
        string adminIdentityId,
        CancellationToken ct = default);

    Task<AdminActionResult> ApproveWhitelistIdentityAsync(
        string adminIdentityId,
        string targetIdentityId,
        CancellationToken ct = default);

    Task<AdminActionResult> RejectWhitelistIdentityAsync(
        string adminIdentityId,
        string targetIdentityId,
        CancellationToken ct = default);

    Task<AdminActionResult<PlayerIdentityRecord>> GetIdentityAsync(
        string adminIdentityId,
        string targetIdentityId,
        CancellationToken ct = default);

    Task<AdminActionResult> SetIdentityStatusAsync(
        string adminIdentityId,
        string targetIdentityId,
        PlayerIdentityStatus status,
        CancellationToken ct = default);

    Task<AdminActionResult> DeactivateIdentityAsync(
        string adminIdentityId,
        string targetIdentityId,
        CancellationToken ct = default);

    Task<AdminActionResult<IReadOnlyList<CharacterProfileRecord>>> ListCharactersByIdentityAsync(
        string adminIdentityId,
        string identityId,
        CancellationToken ct = default);

    Task<AdminActionResult> DisableCharacterAsync(
        string adminIdentityId,
        string characterId,
        CancellationToken ct = default);

    Task<AdminActionResult> ArchiveCharacterAsync(
        string adminIdentityId,
        string characterId,
        CancellationToken ct = default);

    Task<AdminActionResult> DeleteCharacterAsync(
        string adminIdentityId,
        string characterId,
        CancellationToken ct = default);

    Task<AdminActionResult<IReadOnlyList<AdminSessionSnapshot>>> ListActiveSessionsAsync(
        string adminIdentityId,
        CancellationToken ct = default);

    Task<AdminActionResult<AdminSessionSnapshot>> InspectSessionAsync(
        string adminIdentityId,
        Guid sessionId,
        CancellationToken ct = default);

    Task<AdminActionResult> KickSessionAsync(
        string adminIdentityId,
        Guid sessionId,
        AdminPlayerMessage? playerMessage = null,
        CancellationToken ct = default);

    Task<AdminActionResult<IReadOnlyList<IdentityBanEntry>>> ListBansAsync(
        string adminIdentityId,
        bool includeInactive = true,
        CancellationToken ct = default);

    Task<AdminActionResult<AdminBanDetails>> GetBanDetailsAsync(
        string adminIdentityId,
        string identityId,
        CancellationToken ct = default);

    Task<AdminActionResult<IdentityBanEntry>> ApplyBanAsync(
        string adminIdentityId,
        AdminBanApplyRequest request,
        CancellationToken ct = default);

    Task<AdminActionResult<IdentityBanEntry>> RevokeBanAsync(
        string adminIdentityId,
        string identityId,
        string revocationReason,
        CancellationToken ct = default);

    Task<AdminActionResult<AdminAuditQueryResult>> QueryAuditAsync(
        string adminIdentityId,
        AdminAuditQuery query,
        CancellationToken ct = default);

    Task<AdminActionResult<CharacterCrimeRecord>> GetCrimeStateAsync(
        string adminIdentityId,
        string characterId,
        CancellationToken ct = default);

    Task<AdminActionResult<CharacterCrimeRecord>> MarkCrimeAsync(
        string adminIdentityId,
        AdminCrimeMarkRequest request,
        CancellationToken ct = default);

    Task<AdminActionResult<CharacterCrimeRecord>> ClearCrimeStateAsync(
        string adminIdentityId,
        string identityId,
        string characterId,
        string reason,
        CancellationToken ct = default);
}

