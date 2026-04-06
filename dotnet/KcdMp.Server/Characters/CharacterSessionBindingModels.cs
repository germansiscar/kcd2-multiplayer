namespace KcdMp.Server.Characters;

public sealed class CharacterSessionBindingOptions
{
    public bool RequireCharacterOnConnect { get; init; } = false;
    public bool AutoSelectMostRecentCharacterWhenMissing { get; init; } = true;
}

public sealed record CharacterSessionBindingResult(
    bool IsAllowed,
    string? DenialReason,
    string? CharacterId);
