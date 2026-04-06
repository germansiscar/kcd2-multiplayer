namespace KcdMp.Server.Characters;

public interface ICharacterLifecycleService
{
    Task<CharacterLifecycleValidationResult> ValidateLightAsync(
        string identityId,
        string characterId,
        CancellationToken ct = default);

    Task<CharacterLifecycleLoadResult> LoadForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default);

    Task<CharacterLifecycleSaveResult> SaveAndUnloadSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);

    Task<int> SaveAndUnloadAllAsync(
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);
}
