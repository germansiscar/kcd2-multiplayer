namespace KcdMp.Server.Characters;

public interface ICharacterSessionBindingService
{
    Task<CharacterSessionBindingResult> BindAsync(
        string identityId,
        string? preferredCharacterId,
        CancellationToken ct = default);
}
