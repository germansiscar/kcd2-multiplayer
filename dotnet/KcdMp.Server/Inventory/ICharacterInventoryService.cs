using KcdMp.Server.Characters;

namespace KcdMp.Server.Inventory;

public interface ICharacterInventoryService
{
    Task<CharacterInventoryLoadResult> LoadForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default);

    Task<CharacterInventoryRecord?> GetLoadedForSessionAsync(
        Guid sessionId,
        CancellationToken ct = default);

    Task<CharacterInventoryMutationResult> UpsertItemAsync(
        Guid sessionId,
        string containerId,
        InventoryItemRecord item,
        CancellationToken ct = default);

    Task<CharacterInventoryMutationResult> RemoveItemAsync(
        Guid sessionId,
        string containerId,
        string itemInternalId,
        CancellationToken ct = default);

    Task<CharacterInventorySaveResult> SaveForSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);

    Task<CharacterInventorySaveResult> SaveAndUnloadSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);

    Task<int> SaveAndUnloadAllAsync(
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);
}
