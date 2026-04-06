using KcdMp.Server.Characters;

namespace KcdMp.Server.Currency;

public interface ICharacterCurrencyService
{
    Task<CharacterCurrencyLoadResult> LoadForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default);

    Task<CharacterCurrencyRecord?> GetLoadedForSessionAsync(
        Guid sessionId,
        CancellationToken ct = default);

    Task<CharacterCurrencyMutationResult> SetBalanceAsync(
        Guid sessionId,
        long balance,
        CancellationToken ct = default);

    Task<CharacterCurrencyMutationResult> AddAsync(
        Guid sessionId,
        long amount,
        CancellationToken ct = default);

    Task<CharacterCurrencySaveResult> SaveForSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);

    Task<CharacterCurrencySaveResult> SaveAndUnloadSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);

    Task<int> SaveAndUnloadAllAsync(
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default);
}
