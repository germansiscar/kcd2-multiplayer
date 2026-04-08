namespace KcdMp.Server.Economy;

public interface IEconomyAppliedService
{
    EconomyAppliedOptions Options { get; }

    Task<EconomyMoneySyncResult> EnsureMoneyObjectForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default);

    Task<EconomyDirectTransferResult> TransferDirectAsync(
        EconomyDirectTransferRequest request,
        CancellationToken ct = default);
}
