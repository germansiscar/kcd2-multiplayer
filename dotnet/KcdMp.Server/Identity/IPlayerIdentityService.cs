namespace KcdMp.Server.Identity;

public interface IPlayerIdentityService
{
    Task<PlayerIdentityResolution> ResolveOrCreateAsync(PlayerIdentityClaim claim, CancellationToken ct = default);

    Task<PlayerIdentityRecord?> GetByInternalIdAsync(string internalId, CancellationToken ct = default);

    Task<IReadOnlyList<PlayerIdentityRecord>> ListAllAsync(CancellationToken ct = default);

    Task<bool> TrySetStatusAsync(string internalId, PlayerIdentityStatus status, CancellationToken ct = default);

    Task<bool> TryDeactivateAsync(string internalId, CancellationToken ct = default);
}
