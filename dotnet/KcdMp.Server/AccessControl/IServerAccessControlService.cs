namespace KcdMp.Server.AccessControl;

public interface IServerAccessControlService
{
    Task<ServerAccessControlConfigurationRecord> GetActiveConfigurationAsync(CancellationToken ct = default);

    Task<ServerAccessEvaluationResult> EvaluateIdentityAccessAsync(
        ServerAccessIdentityEvaluationRequest request,
        CancellationToken ct = default);

    void EmitDuplicateActiveSessionDenied(Guid sessionId, string? identityId);
}
