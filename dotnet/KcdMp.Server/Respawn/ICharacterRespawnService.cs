using KcdMp.Server.Characters;

namespace KcdMp.Server.Respawn;

public interface ICharacterRespawnService
{
    Task<CharacterRespawnLoadResult> LoadForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default);

    Task<CharacterDefeatLifecycleRecord?> GetLoadedForSessionAsync(
        Guid sessionId,
        CancellationToken ct = default);

    Task<CharacterRespawnActionResult> RegisterDefeatAsync(
        Guid sessionId,
        int healthAfterHit,
        CancellationToken ct = default);

    Task<CharacterRespawnActionResult> TryRecoverByHealerAsync(
        Guid sessionId,
        string healerIdentityId,
        bool healerIsQualified,
        CancellationToken ct = default);

    Task<CharacterRespawnActionResult> ProcessUnconsciousTimeoutAsync(
        Guid sessionId,
        CancellationToken ct = default);

    Task<CharacterRespawnActionResult> CancelWaitAndRespawnAsync(
        Guid sessionId,
        string cancelledByIdentityId,
        CancellationToken ct = default);

    Task<CharacterRespawnSaveResult> SaveForSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);

    Task<CharacterRespawnSaveResult> SaveAndUnloadSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);

    Task<int> SaveAndUnloadAllAsync(
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);
}
