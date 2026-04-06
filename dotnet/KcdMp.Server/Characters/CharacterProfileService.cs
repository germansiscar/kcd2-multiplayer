using System.Text;
using KcdMp.Server.Identity;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.Characters;

public sealed class CharacterProfileService : ICharacterProfileService
{
    private const string NameLookupIndexId = "character_name_lookup_v1";

    private readonly IJsonPersistenceStore _store;
    private readonly IPlayerIdentityService _identityService;
    private readonly IServerObservabilitySink _observability;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CharacterProfileService(
        IJsonPersistenceStore store,
        IPlayerIdentityService identityService,
        IServerObservabilitySink observability,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _identityService = identityService ?? throw new ArgumentNullException(nameof(identityService));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _logger = logger ?? Log.Logger;
    }

    public async Task<CharacterCreateResult> CreateAsync(
        string identityId,
        CharacterCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentNullException.ThrowIfNull(request);
        var fullName = BuildFullName(request.GivenName, request.FamilyName);
        var normalizedFullName = NormalizeFullName(fullName);
        if (string.IsNullOrWhiteSpace(request.ModelKey))
            throw new ArgumentException("ModelKey cannot be empty.", nameof(request));

        await _gate.WaitAsync(ct);
        try
        {
            var identity = await _identityService.GetByInternalIdAsync(identityId, ct);
            if (identity is null || identity.IsDeactivated || identity.Status == PlayerIdentityStatus.Blocked)
            {
                Emit(
                    ServerObservableEventType.CharacterAccessDenied,
                    ServerObservableSeverity.Warning,
                    "Character creation denied because identity is unavailable.",
                    identityId: identityId,
                    payload: new Dictionary<string, object?> { ["full_name"] = fullName });

                return new CharacterCreateResult(
                    Created: false,
                    DenialReason: "Identity not available for character creation.",
                    Character: null);
            }

            var index = await LoadNameIndexAsync(ct);
            if (await NameAlreadyInUseAsync(index, normalizedFullName, ct))
            {
                Emit(
                    ServerObservableEventType.CharacterAccessDenied,
                    ServerObservableSeverity.Warning,
                    "Character creation denied because name is already in use.",
                    identityId: identityId,
                    payload: new Dictionary<string, object?> { ["full_name"] = fullName });

                return new CharacterCreateResult(
                    Created: false,
                    DenialReason: "Character name is already in use.",
                    Character: null);
            }

            var now = DateTimeOffset.UtcNow;
            var characterId = $"cid_{Guid.NewGuid():N}";
            var record = new CharacterProfileRecord
            {
                InternalId = characterId,
                IdentityId = identityId,
                FullName = fullName,
                NormalizedFullName = normalizedFullName,
                ModelKey = request.ModelKey.Trim(),
                Status = CharacterProfileStatus.Inactive,
                IsDeleted = false,
                Metadata = request.Metadata is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastActivityAtUtc = now,
            };

            await _store.SaveAsync(JsonPersistenceDomains.Characters, record.InternalId, record, ct);
            index.NameToCharacterId[normalizedFullName] = record.InternalId;
            index.UpdatedAtUtc = now;
            await _store.SaveAsync(JsonPersistenceDomains.Config, NameLookupIndexId, index, ct);

            identity.CharacterIds ??= [];
            if (!identity.CharacterIds.Contains(record.InternalId, StringComparer.Ordinal))
                identity.CharacterIds.Add(record.InternalId);
            identity.UpdatedAtUtc = now;
            await _store.SaveAsync(JsonPersistenceDomains.Identity, identity.InternalId, identity, ct);

            Emit(
                ServerObservableEventType.CharacterCreated,
                ServerObservableSeverity.Information,
                "Character profile created.",
                identityId: identityId,
                characterId: record.InternalId,
                payload: new Dictionary<string, object?>
                {
                    ["full_name"] = record.FullName,
                    ["model_key"] = record.ModelKey,
                    ["status"] = record.Status.ToString(),
                });

            return new CharacterCreateResult(
                Created: true,
                DenialReason: null,
                Character: record);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CharacterProfileRecord?> GetByInternalIdAsync(
        string characterId,
        CancellationToken ct = default)
    {
        return _store.LoadAsync<CharacterProfileRecord>(
            JsonPersistenceDomains.Characters,
            characterId,
            ValidateCharacterRecord,
            ct);
    }

    public async Task<IReadOnlyList<CharacterProfileRecord>> ListByIdentityAsync(
        string identityId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        var identity = await _identityService.GetByInternalIdAsync(identityId, ct);
        if (identity is null || identity.CharacterIds.Count == 0)
            return [];

        var output = new List<CharacterProfileRecord>();
        foreach (var characterId in identity.CharacterIds.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var character = await GetByInternalIdAsync(characterId, ct);
            if (character is not null && !character.IsDeleted)
                output.Add(character);
        }

        return output
            .OrderBy(x => x.CreatedAtUtc)
            .ToArray();
    }

    public async Task<bool> TrySetStatusAsync(
        string characterId,
        CharacterProfileStatus status,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        string? identityId = null;

        await _gate.WaitAsync(ct);
        try
        {
            var character = await GetByInternalIdAsync(characterId, ct);
            if (character is null || character.IsDeleted)
                return false;

            identityId = character.IdentityId;
            if (character.Status != status)
            {
                character.Status = status;
                character.UpdatedAtUtc = now;
                if (status == CharacterProfileStatus.Active)
                    character.LastActivityAtUtc = now;
                await _store.SaveAsync(JsonPersistenceDomains.Characters, character.InternalId, character, ct);
                changed = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Emit(
                ServerObservableEventType.CharacterStatusChanged,
                ServerObservableSeverity.Information,
                "Character profile status changed.",
                identityId: identityId,
                characterId: characterId,
                payload: new Dictionary<string, object?> { ["status"] = status.ToString() });
        }

        return true;
    }

    public Task<bool> TryDisableAsync(string characterId, CancellationToken ct = default)
        => TrySetStatusAsync(characterId, CharacterProfileStatus.Disabled, ct);

    public async Task<bool> TryDeleteAsync(
        string characterId,
        bool isAdminOperation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        if (!isAdminOperation)
        {
            Emit(
                ServerObservableEventType.CharacterAccessDenied,
                ServerObservableSeverity.Warning,
                "Character delete denied because caller is not admin.",
                characterId: characterId);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        string? identityId = null;

        await _gate.WaitAsync(ct);
        try
        {
            var character = await GetByInternalIdAsync(characterId, ct);
            if (character is null)
                return false;

            if (character.IsDeleted)
                return true;

            identityId = character.IdentityId;
            character.IsDeleted = true;
            character.Status = CharacterProfileStatus.Disabled;
            character.UpdatedAtUtc = now;
            await _store.SaveAsync(JsonPersistenceDomains.Characters, character.InternalId, character, ct);

            var index = await LoadNameIndexAsync(ct);
            index.NameToCharacterId.Remove(character.NormalizedFullName);
            index.UpdatedAtUtc = now;
            await _store.SaveAsync(JsonPersistenceDomains.Config, NameLookupIndexId, index, ct);

            var identity = await _identityService.GetByInternalIdAsync(character.IdentityId, ct);
            if (identity is not null)
            {
                identity.CharacterIds.RemoveAll(x => string.Equals(x, character.InternalId, StringComparison.Ordinal));
                identity.UpdatedAtUtc = now;
                await _store.SaveAsync(JsonPersistenceDomains.Identity, identity.InternalId, identity, ct);
            }
        }
        finally
        {
            _gate.Release();
        }

        Emit(
            ServerObservableEventType.CharacterDeleted,
            ServerObservableSeverity.Information,
            "Character profile deleted by admin.",
            identityId: identityId,
            characterId: characterId);

        return true;
    }

    private async Task<CharacterNameLookupIndex> LoadNameIndexAsync(CancellationToken ct)
    {
        var index = await _store.LoadAsync<CharacterNameLookupIndex>(
            JsonPersistenceDomains.Config,
            NameLookupIndexId,
            x => x.NameToCharacterId is not null,
            ct);
        return index ?? new CharacterNameLookupIndex { UpdatedAtUtc = DateTimeOffset.UtcNow };
    }

    private async Task<bool> NameAlreadyInUseAsync(
        CharacterNameLookupIndex index,
        string normalizedFullName,
        CancellationToken ct)
    {
        if (!index.NameToCharacterId.TryGetValue(normalizedFullName, out var existingId))
            return false;

        var existingCharacter = await GetByInternalIdAsync(existingId, ct);
        if (existingCharacter is null || existingCharacter.IsDeleted)
        {
            index.NameToCharacterId.Remove(normalizedFullName);
            return false;
        }

        return true;
    }

    private static bool ValidateCharacterRecord(CharacterProfileRecord record)
    {
        return !string.IsNullOrWhiteSpace(record.InternalId)
               && !string.IsNullOrWhiteSpace(record.IdentityId)
               && !string.IsNullOrWhiteSpace(record.FullName)
               && !string.IsNullOrWhiteSpace(record.NormalizedFullName)
               && !string.IsNullOrWhiteSpace(record.ModelKey);
    }

    private static string BuildFullName(string givenName, string familyName)
    {
        if (string.IsNullOrWhiteSpace(givenName))
            throw new ArgumentException("GivenName cannot be empty.", nameof(givenName));
        if (string.IsNullOrWhiteSpace(familyName))
            throw new ArgumentException("FamilyName cannot be empty.", nameof(familyName));
        return $"{CollapseWhitespace(givenName)} {CollapseWhitespace(familyName)}";
    }

    private static string NormalizeFullName(string fullName)
    {
        var collapsed = CollapseWhitespace(fullName).ToLowerInvariant();
        return collapsed;
    }

    private static string CollapseWhitespace(string value)
    {
        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var lastWasWhitespace = false;

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                    lastWasWhitespace = true;
                }
            }
            else
            {
                builder.Append(ch);
                lastWasWhitespace = false;
            }
        }

        return builder.ToString();
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        string message,
        string? identityId = null,
        string? characterId = null,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        _observability.Emit(new ServerObservableEvent(
            Type: type,
            Component: ServerObservableComponent.Persistence,
            Severity: severity,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            IdentityId: identityId,
            CharacterId: characterId,
            Message: message,
            Payload: payload));
        _logger.Debug("[character] {Message} identity_id={IdentityId} character_id={CharacterId}",
            message,
            identityId,
            characterId);
    }
}
