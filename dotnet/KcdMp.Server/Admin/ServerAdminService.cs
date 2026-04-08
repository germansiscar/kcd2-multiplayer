using System.Text.Json;
using KcdMp.Server.Audit;
using KcdMp.Server.Bans;
using KcdMp.Server.Characters;
using KcdMp.Server.Crime;
using KcdMp.Server.Identity;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using KcdMp.Server.Sessions;
using Serilog;

namespace KcdMp.Server.Admin;

public sealed class ServerAdminService : IServerAdminService
{
    private readonly IPlayerIdentityService _identityService;
    private readonly ICharacterProfileService _characterService;
    private readonly IIdentityBanService _banService;
    private readonly ICrimeLawService _crimeLaw;
    private readonly IServerObservabilitySink _observability;
    private readonly JsonServerStorageLayout _layout;
    private readonly ILogger _logger;
    private readonly Func<IReadOnlyList<ServerSessionRecord>> _listSessions;
    private readonly Func<Guid, bool> _kickSession;
    private readonly Func<IdentityBanApplyRequest, CancellationToken, Task<IdentityBanApplyResult>> _applyBan;
    private readonly Func<IdentityBanRevocationRequest, CancellationToken, Task<IdentityBanRevocationResult>> _revokeBan;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ServerAdminService(
        IPlayerIdentityService identityService,
        ICharacterProfileService characterService,
        IIdentityBanService banService,
        ICrimeLawService crimeLaw,
        IServerObservabilitySink observability,
        Func<IReadOnlyList<ServerSessionRecord>> listSessions,
        Func<Guid, bool> kickSession,
        Func<IdentityBanApplyRequest, CancellationToken, Task<IdentityBanApplyResult>> applyBan,
        Func<IdentityBanRevocationRequest, CancellationToken, Task<IdentityBanRevocationResult>> revokeBan,
        JsonPersistenceOptions? persistenceOptions = null,
        ILogger? logger = null)
    {
        _identityService = identityService ?? throw new ArgumentNullException(nameof(identityService));
        _characterService = characterService ?? throw new ArgumentNullException(nameof(characterService));
        _banService = banService ?? throw new ArgumentNullException(nameof(banService));
        _crimeLaw = crimeLaw ?? throw new ArgumentNullException(nameof(crimeLaw));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _listSessions = listSessions ?? throw new ArgumentNullException(nameof(listSessions));
        _kickSession = kickSession ?? throw new ArgumentNullException(nameof(kickSession));
        _applyBan = applyBan ?? throw new ArgumentNullException(nameof(applyBan));
        _revokeBan = revokeBan ?? throw new ArgumentNullException(nameof(revokeBan));
        _layout = new JsonServerStorageLayout(persistenceOptions ?? new JsonPersistenceOptions());
        _logger = logger ?? Log.Logger;
    }

    public async Task<AdminActionResult<IReadOnlyList<PlayerIdentityRecord>>> ListPendingWhitelistAsync(
        string adminIdentityId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var list = await _identityService.ListAllAsync(ct);
        var pending = list
            .Where(x => x.Status == PlayerIdentityStatus.Pending && !x.IsDeactivated)
            .OrderBy(x => x.CreatedAtUtc)
            .ToArray();
        return new(true, null, pending);
    }

    public async Task<AdminActionResult> ApproveWhitelistIdentityAsync(
        string adminIdentityId,
        string targetIdentityId,
        CancellationToken ct = default)
    {
        var result = await SetIdentityStatusAsync(adminIdentityId, targetIdentityId, PlayerIdentityStatus.Active, ct);
        if (result.Success)
        {
            Emit(
                ServerObservableEventType.WhitelistApproved,
                ServerObservableSeverity.Information,
                "Whitelist identity approved.",
                identityId: targetIdentityId,
                payload: new Dictionary<string, object?> { ["actor_id"] = adminIdentityId, ["target_id"] = targetIdentityId });
        }

        return result;
    }

    public async Task<AdminActionResult> RejectWhitelistIdentityAsync(
        string adminIdentityId,
        string targetIdentityId,
        CancellationToken ct = default)
    {
        var result = await SetIdentityStatusAsync(adminIdentityId, targetIdentityId, PlayerIdentityStatus.Blocked, ct);
        if (result.Success)
        {
            Emit(
                ServerObservableEventType.WhitelistRejected,
                ServerObservableSeverity.Warning,
                "Whitelist identity rejected.",
                identityId: targetIdentityId,
                payload: new Dictionary<string, object?> { ["actor_id"] = adminIdentityId, ["target_id"] = targetIdentityId });
        }

        return result;
    }

    public async Task<AdminActionResult<PlayerIdentityRecord>> GetIdentityAsync(
        string adminIdentityId,
        string targetIdentityId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var identity = await _identityService.GetByInternalIdAsync(targetIdentityId, ct);
        if (identity is null)
            return new(false, "Identity not found.", null);
        return new(true, null, identity);
    }

    public async Task<AdminActionResult> SetIdentityStatusAsync(
        string adminIdentityId,
        string targetIdentityId,
        PlayerIdentityStatus status,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error);

        var changed = await _identityService.TrySetStatusAsync(targetIdentityId, status, ct);
        if (!changed)
            return new(false, "Identity not found.");

        if (status != PlayerIdentityStatus.Active)
            TryKickByIdentity(targetIdentityId, "Identity access changed by admin.");

        Emit(
            ServerObservableEventType.IdentityStatusChanged,
            ServerObservableSeverity.Information,
            "Identity status changed by admin.",
            identityId: targetIdentityId,
            payload: new Dictionary<string, object?>
            {
                ["actor_id"] = adminIdentityId,
                ["target_id"] = targetIdentityId,
                ["status"] = status.ToString(),
            });
        return new(true, null);
    }

    public async Task<AdminActionResult> DeactivateIdentityAsync(
        string adminIdentityId,
        string targetIdentityId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error);

        var changed = await _identityService.TryDeactivateAsync(targetIdentityId, ct);
        if (!changed)
            return new(false, "Identity not found.");

        TryKickByIdentity(targetIdentityId, "Identity deactivated by admin.");
        Emit(
            ServerObservableEventType.IdentityStatusChanged,
            ServerObservableSeverity.Warning,
            "Identity deactivated by admin.",
            identityId: targetIdentityId,
            payload: new Dictionary<string, object?>
            {
                ["actor_id"] = adminIdentityId,
                ["target_id"] = targetIdentityId,
                ["is_deactivated"] = true,
            });
        return new(true, null);
    }

    public async Task<AdminActionResult<IReadOnlyList<CharacterProfileRecord>>> ListCharactersByIdentityAsync(
        string adminIdentityId,
        string identityId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var list = await _characterService.ListByIdentityAsync(identityId, ct);
        return new(true, null, list);
    }

    public async Task<AdminActionResult> DisableCharacterAsync(
        string adminIdentityId,
        string characterId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error);

        var character = await _characterService.GetByInternalIdAsync(characterId, ct);
        if (character is null)
            return new(false, "Character not found.");

        var changed = await _characterService.TryDisableAsync(characterId, ct);
        if (!changed)
            return new(false, "Character not found.");

        TryKickByCharacter(characterId, "Character disabled by admin.");
        Emit(
            ServerObservableEventType.CharacterStatusChanged,
            ServerObservableSeverity.Warning,
            "Character disabled by admin.",
            identityId: character.IdentityId,
            characterId: characterId,
            payload: new Dictionary<string, object?>
            {
                ["actor_id"] = adminIdentityId,
                ["target_id"] = characterId,
                ["status"] = CharacterProfileStatus.Disabled.ToString(),
            });
        return new(true, null);
    }

    public async Task<AdminActionResult> ArchiveCharacterAsync(
        string adminIdentityId,
        string characterId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error);

        var character = await _characterService.GetByInternalIdAsync(characterId, ct);
        if (character is null)
            return new(false, "Character not found.");

        var changed = await _characterService.TrySetStatusAsync(characterId, CharacterProfileStatus.Inactive, ct);
        if (!changed)
            return new(false, "Character not found.");

        TryKickByCharacter(characterId, "Character archived by admin.");
        Emit(
            ServerObservableEventType.CharacterStatusChanged,
            ServerObservableSeverity.Information,
            "Character archived by admin.",
            identityId: character.IdentityId,
            characterId: characterId,
            payload: new Dictionary<string, object?>
            {
                ["actor_id"] = adminIdentityId,
                ["target_id"] = characterId,
                ["status"] = CharacterProfileStatus.Inactive.ToString(),
            });
        return new(true, null);
    }

    public async Task<AdminActionResult> DeleteCharacterAsync(
        string adminIdentityId,
        string characterId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error);

        var character = await _characterService.GetByInternalIdAsync(characterId, ct);
        if (character is null)
            return new(false, "Character not found.");

        var changed = await _characterService.TryDeleteAsync(characterId, isAdminOperation: true, ct);
        if (!changed)
            return new(false, "Character could not be deleted.");

        TryKickByCharacter(characterId, "Character deleted by admin.");
        Emit(
            ServerObservableEventType.CharacterDeleted,
            ServerObservableSeverity.Warning,
            "Character deleted by admin.",
            identityId: character.IdentityId,
            characterId: characterId,
            payload: new Dictionary<string, object?>
            {
                ["actor_id"] = adminIdentityId,
                ["target_id"] = characterId,
            });
        return new(true, null);
    }

    public async Task<AdminActionResult<IReadOnlyList<AdminSessionSnapshot>>> ListActiveSessionsAsync(
        string adminIdentityId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var sessions = _listSessions()
            .Select(ToSnapshot)
            .OrderBy(x => x.CreatedAtUtc)
            .ToArray();
        return new(true, null, sessions);
    }

    public async Task<AdminActionResult<AdminSessionSnapshot>> InspectSessionAsync(
        string adminIdentityId,
        Guid sessionId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var session = _listSessions().FirstOrDefault(x => x.SessionId == sessionId);
        if (session is null)
            return new(false, "Session not found.", null);
        return new(true, null, ToSnapshot(session));
    }

    public async Task<AdminActionResult> KickSessionAsync(
        string adminIdentityId,
        Guid sessionId,
        AdminPlayerMessage? playerMessage = null,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error);

        var kicked = _kickSession(sessionId);
        if (!kicked)
            return new(false, "Session not found.");

        Emit(
            ServerObservableEventType.AdminSessionKicked,
            ServerObservableSeverity.Warning,
            "Session kicked by admin.",
            sessionId: sessionId,
            payload: new Dictionary<string, object?>
            {
                ["actor_id"] = adminIdentityId,
                ["message"] = playerMessage?.Message,
                ["message_code"] = playerMessage?.Code,
            });
        return new(true, null);
    }

    public async Task<AdminActionResult<IReadOnlyList<IdentityBanEntry>>> ListBansAsync(
        string adminIdentityId,
        bool includeInactive = true,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var records = await LoadBanHistoriesAsync(ct);
        var entries = records
            .SelectMany(x => x.Bans)
            .Where(x => includeInactive || x.Status == IdentityBanStatus.Active)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToArray();
        return new(true, null, entries);
    }

    public async Task<AdminActionResult<AdminBanDetails>> GetBanDetailsAsync(
        string adminIdentityId,
        string identityId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var records = await LoadBanHistoriesAsync(ct);
        var history = records.FirstOrDefault(x => string.Equals(x.IdentityId, identityId, StringComparison.Ordinal));
        if (history is null)
        {
            var active = await _banService.GetActiveBanAsync(identityId, ct);
            if (active is null)
                return new(false, "Ban history not found.", null);

            return new(true, null, new AdminBanDetails(identityId, active.BanId, [active]));
        }

        return new(
            true,
            null,
            new AdminBanDetails(
                history.IdentityId,
                history.ActiveBanId,
                history.Bans.OrderByDescending(x => x.CreatedAtUtc).ToArray()));
    }

    public async Task<AdminActionResult<IdentityBanEntry>> ApplyBanAsync(
        string adminIdentityId,
        AdminBanApplyRequest request,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var apply = await _applyBan(new IdentityBanApplyRequest(
            request.IdentityId,
            request.Type,
            request.Duration,
            request.Reason,
            adminIdentityId,
            request.Summary,
            request.Notes,
            request.AuditEventIds), ct);

        if (!apply.Applied || apply.Ban is null)
            return new(false, apply.DenialReason ?? "Ban could not be applied.", null);
        return new(true, null, apply.Ban);
    }

    public async Task<AdminActionResult<IdentityBanEntry>> RevokeBanAsync(
        string adminIdentityId,
        string identityId,
        string revocationReason,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var revoke = await _revokeBan(new IdentityBanRevocationRequest(identityId, adminIdentityId, revocationReason), ct);
        if (!revoke.Revoked || revoke.Ban is null)
            return new(false, revoke.DenialReason ?? "Ban could not be revoked.", null);
        return new(true, null, revoke.Ban);
    }

    public async Task<AdminActionResult<AdminAuditQueryResult>> QueryAuditAsync(
        string adminIdentityId,
        AdminAuditQuery query,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        var from = query.FromUtc ?? DateTimeOffset.MinValue;
        var to = query.ToUtc ?? DateTimeOffset.MaxValue;
        if (to < from)
            return new(false, "Invalid time range.", null);

        var events = await LoadAuditEventsAsync(from, to, ct);
        var filtered = events.Where(evt =>
            (query.IdentityId is null || string.Equals(evt.IdentityId, query.IdentityId, StringComparison.Ordinal)) &&
            (query.CharacterId is null || string.Equals(evt.CharacterId, query.CharacterId, StringComparison.Ordinal)) &&
            (query.EventType is null || string.Equals(evt.EventType, query.EventType, StringComparison.OrdinalIgnoreCase)) &&
            evt.TimestampUtc >= from &&
            evt.TimestampUtc <= to)
            .OrderByDescending(x => x.TimestampUtc)
            .ToArray();

        Emit(
            ServerObservableEventType.AdminAuditQueried,
            ServerObservableSeverity.Information,
            "Admin audit query executed.",
            payload: new Dictionary<string, object?>
            {
                ["actor_id"] = adminIdentityId,
                ["identity_id"] = query.IdentityId,
                ["character_id"] = query.CharacterId,
                ["event_type"] = query.EventType,
                ["from_utc"] = from,
                ["to_utc"] = to,
                ["total_matched"] = filtered.Length,
            });
        return new(true, null, new AdminAuditQueryResult(filtered, filtered.Length));
    }

    public async Task<AdminActionResult<CharacterCrimeRecord>> GetCrimeStateAsync(
        string adminIdentityId,
        string characterId,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        if (string.IsNullOrWhiteSpace(characterId))
            return new(false, "Character id is required.", null);

        var state = await _crimeLaw.GetCharacterRecordAsync(characterId, ct);
        if (state is null)
            return new(false, "Crime state not found.", null);

        return new(true, null, state);
    }

    public async Task<AdminActionResult<CharacterCrimeRecord>> MarkCrimeAsync(
        string adminIdentityId,
        AdminCrimeMarkRequest request,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        if (string.IsNullOrWhiteSpace(request.IdentityId) || string.IsNullOrWhiteSpace(request.CharacterId))
            return new(false, "Identity id and character id are required.", null);

        var applied = await _crimeLaw.RegisterManualCrimeAsync(
            new ManualCrimeRegistrationRequest(
                SessionId: null,
                ActorIdentityId: adminIdentityId,
                IdentityId: request.IdentityId,
                CharacterId: request.CharacterId,
                CrimeType: request.CrimeType,
                TargetKind: request.TargetKind,
                TargetId: request.TargetId,
                TargetIdentityId: request.TargetIdentityId,
                TargetCharacterId: request.TargetCharacterId,
                ActionCode: request.ActionCode,
                Reason: request.Reason,
                Metadata: request.Metadata),
            ct);

        if (!applied.Applied || applied.Record is null)
            return new(false, applied.DenialReason ?? "Crime could not be marked.", null);

        return new(true, null, applied.Record);
    }

    public async Task<AdminActionResult<CharacterCrimeRecord>> ClearCrimeStateAsync(
        string adminIdentityId,
        string identityId,
        string characterId,
        string reason,
        CancellationToken ct = default)
    {
        var auth = await AuthorizeAsync(adminIdentityId, ct);
        if (!auth.Success)
            return new(false, auth.Error, null);

        if (string.IsNullOrWhiteSpace(identityId) || string.IsNullOrWhiteSpace(characterId))
            return new(false, "Identity id and character id are required.", null);

        var cleared = await _crimeLaw.ClearCharacterStateAsync(
            new CrimeStateClearRequest(
                SessionId: null,
                ActorIdentityId: adminIdentityId,
                IdentityId: identityId,
                CharacterId: characterId,
                Reason: reason),
            ct);

        if (!cleared.Applied || cleared.Record is null)
            return new(false, cleared.DenialReason ?? "Crime state could not be cleared.", null);

        return new(true, null, cleared.Record);
    }

    private async Task<AdminActionResult> AuthorizeAsync(string adminIdentityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(adminIdentityId))
            return new(false, "Admin identity id is required.");

        var actor = await _identityService.GetByInternalIdAsync(adminIdentityId, ct);
        if (actor is null)
            return new(false, "Admin identity not found.");

        if (actor.IsDeactivated || actor.Status != PlayerIdentityStatus.Active || actor.Role != PlayerIdentityRole.Admin)
            return new(false, "Operation requires active admin role.");

        return new(true, null);
    }

    private void TryKickByIdentity(string identityId, string summary)
    {
        var session = _listSessions().FirstOrDefault(x => string.Equals(x.IdentityId, identityId, StringComparison.Ordinal));
        if (session is null)
            return;

        if (_kickSession(session.SessionId))
            _logger.Information("[admin] kicked session {SessionId} for identity {IdentityId}. {Summary}", session.SessionId, identityId, summary);
    }

    private void TryKickByCharacter(string characterId, string summary)
    {
        var session = _listSessions().FirstOrDefault(x => string.Equals(x.CharacterId, characterId, StringComparison.Ordinal));
        if (session is null)
            return;

        if (_kickSession(session.SessionId))
            _logger.Information("[admin] kicked session {SessionId} for character {CharacterId}. {Summary}", session.SessionId, characterId, summary);
    }

    private async Task<IReadOnlyList<IdentityBanHistoryRecord>> LoadBanHistoriesAsync(CancellationToken ct)
    {
        var bansPath = _layout.GetDomainPath(JsonPersistenceDomains.Bans);
        if (!Directory.Exists(bansPath))
            return [];

        var result = new List<IdentityBanHistoryRecord>();
        foreach (var file in Directory.EnumerateFiles(bansPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var loaded = await TryReadJsonAsync<IdentityBanHistoryRecord>(file, ct);
            if (loaded is not null && !string.IsNullOrWhiteSpace(loaded.IdentityId))
                result.Add(loaded);
        }

        return result;
    }

    private async Task<IReadOnlyList<ServerAuditEventRecord>> LoadAuditEventsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct)
    {
        var auditPath = _layout.GetDomainPath(JsonPersistenceDomains.Audit);
        if (!Directory.Exists(auditPath))
            return [];

        var fromDate = DateOnly.FromDateTime(fromUtc.UtcDateTime);
        var toDate = DateOnly.FromDateTime(toUtc.UtcDateTime);
        var records = new List<ServerAuditEventRecord>();
        foreach (var file in Directory.EnumerateFiles(auditPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", out var partitionDate))
                continue;
            if (partitionDate < fromDate || partitionDate > toDate)
                continue;

            var partition = await TryReadJsonAsync<List<ServerAuditEventRecord>>(file, ct);
            if (partition is not null)
                records.AddRange(partition);
        }

        return records;
    }

    private async Task<T?> TryReadJsonAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await JsonSerializer.DeserializeAsync<T>(stream, _json, ct);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "[admin] failed to read json file {Path}", path);
            return default;
        }
    }

    private static AdminSessionSnapshot ToSnapshot(ServerSessionRecord source)
    {
        return new AdminSessionSnapshot(
            source.SessionId,
            source.IdentityId,
            source.CharacterId,
            source.RemoteEndpoint,
            source.State,
            source.AuthState,
            source.CreatedAtUtc,
            source.LastActivityAtUtc);
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        string message,
        Guid? sessionId = null,
        string? identityId = null,
        string? characterId = null,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        _observability.Emit(new ServerObservableEvent(
            Type: type,
            Component: ServerObservableComponent.Session,
            Severity: severity,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            SessionId: sessionId,
            IdentityId: identityId,
            CharacterId: characterId,
            Message: message,
            Payload: payload));
    }
}
