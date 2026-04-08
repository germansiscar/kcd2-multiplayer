namespace KcdMp.Server.Economy;

public sealed class EconomyAppliedOptions
{
    public string MoneyContainerId { get; init; } = "main";
    public string MoneyItemInternalId { get; init; } = "kcdmp_currency_money";
    public string MoneyItemType { get; init; } = "currency";
    public string MoneyItemRef { get; init; } = "kcdmp.currency.money";
    public string MoneyAmountMetadataKey { get; init; } = "amount";
    public double MaxTransferDistanceMeters { get; init; } = 3.5;
}

public sealed record EconomyMoneySyncResult(
    bool Applied,
    string? DenialReason,
    long Balance);

public sealed record EconomyDirectTransferRequest(
    Guid SenderSessionId,
    Guid ReceiverSessionId,
    string SenderIdentityId,
    string SenderCharacterId,
    string ReceiverIdentityId,
    string ReceiverCharacterId,
    long Amount,
    double DistanceMeters);

public sealed record EconomyDirectTransferResult(
    bool Applied,
    string? DenialReason,
    long SenderBalance,
    long ReceiverBalance);
