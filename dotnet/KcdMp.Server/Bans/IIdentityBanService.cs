namespace KcdMp.Server.Bans;

public interface IIdentityBanService
{
    Task<IdentityBanAccessEvaluationResult> EvaluateAccessAsync(
        Guid sessionId,
        string identityId,
        CancellationToken ct = default);

    Task<IdentityBanApplyResult> ApplyBanAsync(
        IdentityBanApplyRequest request,
        Guid? sessionId = null,
        CancellationToken ct = default);

    Task<IdentityBanRevocationResult> RevokeActiveBanAsync(
        IdentityBanRevocationRequest request,
        Guid? sessionId = null,
        CancellationToken ct = default);

    Task<IdentityBanEntry?> GetActiveBanAsync(string identityId, CancellationToken ct = default);
}
