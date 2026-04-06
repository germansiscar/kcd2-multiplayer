namespace KcdMp.Server.Characters;

public interface ICharacterProfileService
{
    Task<CharacterCreateResult> CreateAsync(
        string identityId,
        CharacterCreateRequest request,
        CancellationToken ct = default);

    Task<CharacterProfileRecord?> GetByInternalIdAsync(
        string characterId,
        CancellationToken ct = default);

    Task<IReadOnlyList<CharacterProfileRecord>> ListByIdentityAsync(
        string identityId,
        CancellationToken ct = default);

    Task<bool> TrySetStatusAsync(
        string characterId,
        CharacterProfileStatus status,
        CancellationToken ct = default);

    Task<bool> TryDisableAsync(
        string characterId,
        CancellationToken ct = default);

    Task<bool> TryDeleteAsync(
        string characterId,
        bool isAdminOperation,
        CancellationToken ct = default);
}
