using KcdMp.Server.Identity;
using Serilog;

namespace KcdMp.Server.Characters;

public sealed class CharacterSessionBindingService : ICharacterSessionBindingService
{
    private readonly ICharacterProfileService _characters;
    private readonly IPlayerIdentityService _identities;
    private readonly CharacterSessionBindingOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CharacterSessionBindingService(
        ICharacterProfileService characters,
        IPlayerIdentityService identities,
        CharacterSessionBindingOptions? options = null,
        ILogger? logger = null)
    {
        _characters = characters ?? throw new ArgumentNullException(nameof(characters));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _options = options ?? new CharacterSessionBindingOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<CharacterSessionBindingResult> BindAsync(
        string identityId,
        string? preferredCharacterId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        var preferred = string.IsNullOrWhiteSpace(preferredCharacterId) ? null : preferredCharacterId.Trim();

        await _gate.WaitAsync(ct);
        try
        {
            var identity = await _identities.GetByInternalIdAsync(identityId, ct);
            if (identity is null || identity.IsDeactivated || identity.Status == PlayerIdentityStatus.Blocked)
                return Denied("Identity not available.");

            var ownedCharacters = (await _characters.ListByIdentityAsync(identityId, ct)).ToList();

            if (preferred is not null)
            {
                var preferredCharacter = await _characters.GetByInternalIdAsync(preferred, ct);
                if (preferredCharacter is null || preferredCharacter.IsDeleted)
                    return Denied("Requested character does not exist.");
                if (!string.Equals(preferredCharacter.IdentityId, identityId, StringComparison.Ordinal))
                    return Denied("Requested character is not owned by this identity.");
                if (preferredCharacter.Status == CharacterProfileStatus.Disabled)
                    return Denied("Requested character is disabled.");
            }

            var selected = ResolveSelectedCharacterAsync(preferred, ownedCharacters);
            if (selected is null && preferred is null && ownedCharacters.Count == 0 && !_options.RequireCharacterOnConnect)
            {
                var bootstrapCharacter = await TryCreateBootstrapCharacterAsync(identity, ct);
                if (bootstrapCharacter is not null)
                {
                    ownedCharacters.Add(bootstrapCharacter);
                    selected = bootstrapCharacter;
                    _logger.Information(
                        "[character-bind] identity={IdentityId} bootstrap_character={CharacterId}",
                        identityId,
                        bootstrapCharacter.InternalId);
                }
            }

            if (selected is null)
            {
                if (_options.RequireCharacterOnConnect)
                    return Denied("No character available for this identity.");

                _logger.Information("[character-bind] identity={IdentityId} connected without active character.", identityId);
                return new CharacterSessionBindingResult(true, null, null);
            }

            foreach (var candidate in ownedCharacters)
            {
                if (candidate.IsDeleted || candidate.Status == CharacterProfileStatus.Disabled)
                    continue;

                var desiredStatus = string.Equals(candidate.InternalId, selected.InternalId, StringComparison.Ordinal)
                    ? CharacterProfileStatus.Active
                    : CharacterProfileStatus.Inactive;

                if (candidate.Status != desiredStatus)
                    await _characters.TrySetStatusAsync(candidate.InternalId, desiredStatus, ct);
            }

            _logger.Information(
                "[character-bind] identity={IdentityId} active_character={CharacterId}",
                identityId,
                selected.InternalId);
            return new CharacterSessionBindingResult(true, null, selected.InternalId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private CharacterProfileRecord? ResolveSelectedCharacterAsync(
        string? preferredCharacterId,
        IReadOnlyList<CharacterProfileRecord> ownedCharacters)
    {
        if (preferredCharacterId is not null)
            return ownedCharacters.FirstOrDefault(x => string.Equals(x.InternalId, preferredCharacterId, StringComparison.Ordinal));

        if (!_options.AutoSelectMostRecentCharacterWhenMissing)
            return null;

        return ownedCharacters
            .Where(x => !x.IsDeleted && x.Status != CharacterProfileStatus.Disabled)
            .OrderByDescending(x => x.Status == CharacterProfileStatus.Active)
            .ThenByDescending(x => x.LastActivityAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();
    }

    private async Task<CharacterProfileRecord?> TryCreateBootstrapCharacterAsync(
        PlayerIdentityRecord identity,
        CancellationToken ct)
    {
        var givenName = ExtractGivenName(identity.DisplayName);
        var suffix = identity.InternalId.Length >= 6
            ? identity.InternalId[^6..]
            : identity.InternalId;
        var familyName = $"Traveler{suffix}";
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bootstrap"] = "true",
            ["source"] = "auto_on_first_connect",
        };

        var created = await _characters.CreateAsync(
            identity.InternalId,
            new CharacterCreateRequest(
                GivenName: givenName,
                FamilyName: familyName,
                ModelKey: "knight",
                Metadata: metadata),
            ct);

        if (!created.Created || created.Character is null)
        {
            _logger.Warning(
                "[character-bind] identity={IdentityId} bootstrap create failed reason={Reason}",
                identity.InternalId,
                created.DenialReason ?? "unknown");
            return null;
        }

        return created.Character;
    }

    private static string ExtractGivenName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "Player";

        var parts = displayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[0] : "Player";
    }

    private CharacterSessionBindingResult Denied(string reason)
    {
        _logger.Warning("[character-bind] denied reason={Reason}", reason);
        return new CharacterSessionBindingResult(false, reason, null);
    }
}
