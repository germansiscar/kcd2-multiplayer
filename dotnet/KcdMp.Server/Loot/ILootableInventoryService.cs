namespace KcdMp.Server.Loot;

public interface ILootableInventoryService
{
    Task<ChestUpsertResult> UpsertChestAsync(
        LootableChestRecord chest,
        CancellationToken ct = default);

    Task<LootableChestRecord?> GetChestAsync(
        string chestId,
        CancellationToken ct = default);

    Task<LootTransferResult> TransferItemFromCharacterAsync(
        LootCharacterItemTransferRequest request,
        CancellationToken ct = default);

    Task<LootTransferResult> TransferCurrencyFromCharacterAsync(
        LootCharacterCurrencyTransferRequest request,
        CancellationToken ct = default);

    Task<LootTransferResult> TransferItemFromChestAsync(
        LootChestItemTransferRequest request,
        CancellationToken ct = default);

    Task<LootTransferResult> TransferCurrencyFromChestAsync(
        LootChestCurrencyTransferRequest request,
        CancellationToken ct = default);
}
