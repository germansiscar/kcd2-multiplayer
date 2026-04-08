using System.Text.Json;
using System.Text.Json.Serialization;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.Audit;

public sealed class PersistentAuditObservabilitySink : IServerObservabilitySink
{
    private static readonly string[] SensitiveTokens =
    [
        "password",
        "secret",
        "token",
        "credential",
    ];

    private readonly JsonServerStorageLayout _layout;
    private readonly ServerAuditOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json;
    private DateOnly? _lastRetentionRunUtc;

    public PersistentAuditObservabilitySink(
        JsonPersistenceOptions? persistenceOptions = null,
        ServerAuditOptions? auditOptions = null,
        ILogger? logger = null)
    {
        var options = persistenceOptions ?? new JsonPersistenceOptions();
        _layout = new JsonServerStorageLayout(options);
        _layout.EnsureInitialized();
        _options = auditOptions ?? new ServerAuditOptions();
        _logger = logger ?? Log.Logger;
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = _options.WriteIndented,
        };
    }

    public void Emit(ServerObservableEvent observableEvent)
    {
        if (!_options.Enabled)
            return;

        if (!TryMapToAudit(observableEvent, out var auditRecord))
        {
            _logger.Debug(
                "[audit] event discarded type={EventType} session_id={SessionId} identity_id={IdentityId} character_id={CharacterId}",
                observableEvent.Type,
                observableEvent.SessionId,
                observableEvent.IdentityId,
                observableEvent.CharacterId);
            return;
        }

        try
        {
            PersistAuditRecordAsync(auditRecord, CancellationToken.None).GetAwaiter().GetResult();
            _logger.Debug(
                "[audit] write ok audit_event_id={AuditEventId} type={EventType} category={Category}",
                auditRecord.AuditEventId,
                auditRecord.EventType,
                auditRecord.Category);
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "[audit] write failed type={EventType} session_id={SessionId} identity_id={IdentityId} character_id={CharacterId}",
                observableEvent.Type,
                observableEvent.SessionId,
                observableEvent.IdentityId,
                observableEvent.CharacterId);
        }
    }

    private async Task PersistAuditRecordAsync(ServerAuditEventRecord record, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var partitionDate = DateOnly.FromDateTime(record.TimestampUtc.UtcDateTime);
            var path = GetPartitionPath(partitionDate);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var current = await ReadPartitionUnsafeAsync(path, ct);
            current.Add(record);
            await WritePartitionUnsafeAsync(path, current, ct);

            await ApplyRetentionUnsafeAsync(partitionDate, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<ServerAuditEventRecord>> ReadPartitionUnsafeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var loaded = await JsonSerializer.DeserializeAsync<List<ServerAuditEventRecord>>(stream, _json, ct);
            return loaded ?? [];
        }
        catch (JsonException ex)
        {
            var corruptPath = $"{path}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(path, corruptPath, overwrite: true);
            }
            catch (Exception moveEx)
            {
                _logger.Warning(moveEx, "[audit] failed to move corrupt audit partition file {Path}", path);
            }

            _logger.Error(ex, "[audit] invalid audit partition JSON moved to {CorruptPath}", corruptPath);
            return [];
        }
    }

    private async Task WritePartitionUnsafeAsync(
        string path,
        IReadOnlyList<ServerAuditEventRecord> records,
        CancellationToken ct)
    {
        var tempPath = $"{path}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, records, _json, ct);
                await stream.FlushAsync(ct);
            }

            if (File.Exists(path))
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }
    }

    private async Task ApplyRetentionUnsafeAsync(DateOnly todayUtc, CancellationToken ct)
    {
        if (_options.RetentionDays <= 0)
            return;
        if (_lastRetentionRunUtc == todayUtc)
            return;

        _lastRetentionRunUtc = todayUtc;
        var cutoff = todayUtc.AddDays(-_options.RetentionDays);
        var auditDir = _layout.GetDomainPath(JsonPersistenceDomains.Audit);
        if (!Directory.Exists(auditDir))
            return;

        foreach (var file in Directory.EnumerateFiles(auditDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", out var parsed))
                continue;

            if (parsed < cutoff)
            {
                try
                {
                    File.Delete(file);
                    _logger.Information("[audit] retention deleted partition {PartitionDate}", parsed);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[audit] retention failed for {Path}", file);
                }
            }
        }

        await Task.CompletedTask;
    }

    private string GetPartitionPath(DateOnly partitionDate)
    {
        var auditDomainPath = _layout.GetDomainPath(JsonPersistenceDomains.Audit);
        return Path.Combine(auditDomainPath, $"{partitionDate:yyyy-MM-dd}.json");
    }

    private static bool TryMapToAudit(ServerObservableEvent observableEvent, out ServerAuditEventRecord auditRecord)
    {
        if (!TryMapCategory(observableEvent.Type, out var category))
        {
            auditRecord = default!;
            return false;
        }

        var payload = RedactSensitivePayload(observableEvent.Payload);
        var result = IsFailedEvent(observableEvent.Type)
            ? ServerAuditResult.Fail
            : ServerAuditResult.Ok;
        var actorId = ExtractString(payload, "actor_id")
                      ?? ExtractString(payload, "healer_identity_id")
                      ?? ExtractString(payload, "cancelled_by_identity_id");
        var targetId = ExtractString(payload, "target_id");

        auditRecord = new ServerAuditEventRecord(
            AuditEventId: $"aevt_{Guid.NewGuid():N}",
            TimestampUtc: observableEvent.OccurredAtUtc,
            EventType: observableEvent.Type.ToString(),
            Category: category,
            Severity: observableEvent.Severity,
            Result: result,
            SessionId: observableEvent.SessionId,
            IdentityId: observableEvent.IdentityId,
            CharacterId: observableEvent.CharacterId,
            ActorId: actorId,
            TargetId: targetId,
            Payload: payload,
            Message: observableEvent.Message);
        return true;
    }

    private static bool TryMapCategory(ServerObservableEventType eventType, out ServerAuditCategory category)
    {
        category = eventType switch
        {
            ServerObservableEventType.AccessDecisionAllowed or
            ServerObservableEventType.AccessDecisionDenied or
            ServerObservableEventType.IdentityAccessDenied or
            ServerObservableEventType.BanApplied or
            ServerObservableEventType.BanRevoked or
            ServerObservableEventType.BanExpired or
            ServerObservableEventType.BanAccessDenied or
            ServerObservableEventType.BanSessionKicked or
            ServerObservableEventType.WhitelistApproved or
            ServerObservableEventType.WhitelistRejected => ServerAuditCategory.Access,

            ServerObservableEventType.IdentityCreated => ServerAuditCategory.Identity,
            ServerObservableEventType.IdentityRoleChanged => ServerAuditCategory.Identity,

            ServerObservableEventType.CharacterCreated or
            ServerObservableEventType.CharacterDeleted or
            ServerObservableEventType.CharacterStatusChanged or
            ServerObservableEventType.CharacterAccessDenied or
            ServerObservableEventType.CharacterLoadCompleted or
            ServerObservableEventType.CharacterLoadFailed or
            ServerObservableEventType.CharacterSaveFailed => ServerAuditCategory.Character,

            ServerObservableEventType.InventoryChanged or
            ServerObservableEventType.InventoryValidationFailed or
            ServerObservableEventType.InventorySaveFailed or
            ServerObservableEventType.InventoryProjectionStarted or
            ServerObservableEventType.InventoryProjectionCompleted or
            ServerObservableEventType.InventoryProjectionPartiallyApplied or
            ServerObservableEventType.InventoryProjectionFailed or
            ServerObservableEventType.InventoryProjectionDesyncDetected or
            ServerObservableEventType.InventoryProjectionForcedCorrection or
            ServerObservableEventType.LootStarted or
            ServerObservableEventType.LootAllowed or
            ServerObservableEventType.LootDenied or
            ServerObservableEventType.LootItemTransferred or
            ServerObservableEventType.LootAccessConflict or
            ServerObservableEventType.LootExecutionFailed or
            ServerObservableEventType.LootDesyncIncident => ServerAuditCategory.Inventory,

            ServerObservableEventType.CurrencyChanged or
            ServerObservableEventType.CurrencyValidationFailed or
            ServerObservableEventType.CurrencySaveFailed or
            ServerObservableEventType.EconomyTransferStarted or
            ServerObservableEventType.EconomyTransferDenied or
            ServerObservableEventType.EconomyTransferCompleted or
            ServerObservableEventType.EconomyTransferFailed => ServerAuditCategory.Currency,

            ServerObservableEventType.DefeatDetected or
            ServerObservableEventType.UnconsciousEntered or
            ServerObservableEventType.HealerRecoveryApplied or
            ServerObservableEventType.UnconsciousExpired or
            ServerObservableEventType.UnconsciousCancelled or
            ServerObservableEventType.RespawnApplied or
            ServerObservableEventType.RespawnApplyFailed or
            ServerObservableEventType.RespawnSaveFailed => ServerAuditCategory.Respawn,

            ServerObservableEventType.PersistenceSaveFailed or
            ServerObservableEventType.PersistenceLoadFailed => ServerAuditCategory.Persistence,

            ServerObservableEventType.AdminSessionKicked => ServerAuditCategory.Access,
            ServerObservableEventType.AdminAuditQueried => ServerAuditCategory.Access,

            ServerObservableEventType.ChatMessageAccepted or
            ServerObservableEventType.ChatMessageDelivered or
            ServerObservableEventType.ChatMessageRejected or
            ServerObservableEventType.ChatRateLimitTriggered or
            ServerObservableEventType.ChatInvalidChannel => ServerAuditCategory.Communication,

            ServerObservableEventType.CrimeDetected or
            ServerObservableEventType.CrimeRegistered or
            ServerObservableEventType.CrimeStateUpdated or
            ServerObservableEventType.CrimeAdminAction or
            ServerObservableEventType.CrimeRegistrationFailed => ServerAuditCategory.Crime,

            ServerObservableEventType.BackendError => ServerAuditCategory.Error,
            _ => default,
        };

        return eventType is
            ServerObservableEventType.AccessDecisionAllowed or
            ServerObservableEventType.AccessDecisionDenied or
            ServerObservableEventType.IdentityAccessDenied or
            ServerObservableEventType.BanApplied or
            ServerObservableEventType.BanRevoked or
            ServerObservableEventType.BanExpired or
            ServerObservableEventType.BanAccessDenied or
            ServerObservableEventType.BanSessionKicked or
            ServerObservableEventType.WhitelistApproved or
            ServerObservableEventType.WhitelistRejected or
            ServerObservableEventType.IdentityCreated or
            ServerObservableEventType.IdentityRoleChanged or
            ServerObservableEventType.CharacterCreated or
            ServerObservableEventType.CharacterDeleted or
            ServerObservableEventType.CharacterStatusChanged or
            ServerObservableEventType.CharacterAccessDenied or
            ServerObservableEventType.CharacterLoadCompleted or
            ServerObservableEventType.CharacterLoadFailed or
            ServerObservableEventType.CharacterSaveFailed or
            ServerObservableEventType.InventoryChanged or
            ServerObservableEventType.InventoryValidationFailed or
            ServerObservableEventType.InventorySaveFailed or
            ServerObservableEventType.InventoryProjectionStarted or
            ServerObservableEventType.InventoryProjectionCompleted or
            ServerObservableEventType.InventoryProjectionPartiallyApplied or
            ServerObservableEventType.InventoryProjectionFailed or
            ServerObservableEventType.InventoryProjectionDesyncDetected or
            ServerObservableEventType.InventoryProjectionForcedCorrection or
            ServerObservableEventType.LootStarted or
            ServerObservableEventType.LootAllowed or
            ServerObservableEventType.LootDenied or
            ServerObservableEventType.LootItemTransferred or
            ServerObservableEventType.LootAccessConflict or
            ServerObservableEventType.LootExecutionFailed or
            ServerObservableEventType.LootDesyncIncident or
             ServerObservableEventType.CurrencyChanged or
             ServerObservableEventType.CurrencyValidationFailed or
             ServerObservableEventType.CurrencySaveFailed or
             ServerObservableEventType.EconomyTransferStarted or
             ServerObservableEventType.EconomyTransferDenied or
             ServerObservableEventType.EconomyTransferCompleted or
             ServerObservableEventType.EconomyTransferFailed or
             ServerObservableEventType.DefeatDetected or
            ServerObservableEventType.UnconsciousEntered or
            ServerObservableEventType.HealerRecoveryApplied or
            ServerObservableEventType.UnconsciousExpired or
            ServerObservableEventType.UnconsciousCancelled or
            ServerObservableEventType.RespawnApplied or
            ServerObservableEventType.RespawnApplyFailed or
            ServerObservableEventType.RespawnSaveFailed or
            ServerObservableEventType.PersistenceSaveFailed or
            ServerObservableEventType.PersistenceLoadFailed or
            ServerObservableEventType.AdminSessionKicked or
            ServerObservableEventType.AdminAuditQueried or
            ServerObservableEventType.ChatMessageAccepted or
            ServerObservableEventType.ChatMessageDelivered or
            ServerObservableEventType.ChatMessageRejected or
            ServerObservableEventType.ChatRateLimitTriggered or
            ServerObservableEventType.ChatInvalidChannel or
            ServerObservableEventType.CrimeDetected or
            ServerObservableEventType.CrimeRegistered or
            ServerObservableEventType.CrimeStateUpdated or
            ServerObservableEventType.CrimeAdminAction or
            ServerObservableEventType.CrimeRegistrationFailed or
            ServerObservableEventType.BackendError;
    }

    private static bool IsFailedEvent(ServerObservableEventType eventType)
    {
        return eventType is
            ServerObservableEventType.AccessDecisionDenied or
            ServerObservableEventType.IdentityAccessDenied or
            ServerObservableEventType.BanAccessDenied or
            ServerObservableEventType.WhitelistRejected or
            ServerObservableEventType.CharacterAccessDenied or
            ServerObservableEventType.CharacterLoadFailed or
            ServerObservableEventType.CharacterSaveFailed or
            ServerObservableEventType.InventoryValidationFailed or
            ServerObservableEventType.InventorySaveFailed or
            ServerObservableEventType.InventoryProjectionPartiallyApplied or
            ServerObservableEventType.InventoryProjectionFailed or
            ServerObservableEventType.InventoryProjectionDesyncDetected or
            ServerObservableEventType.LootDenied or
            ServerObservableEventType.LootAccessConflict or
            ServerObservableEventType.LootExecutionFailed or
            ServerObservableEventType.LootDesyncIncident or
            ServerObservableEventType.CurrencyValidationFailed or
            ServerObservableEventType.CurrencySaveFailed or
            ServerObservableEventType.EconomyTransferDenied or
            ServerObservableEventType.EconomyTransferFailed or
            ServerObservableEventType.RespawnApplyFailed or
            ServerObservableEventType.RespawnSaveFailed or
            ServerObservableEventType.PersistenceLoadFailed or
            ServerObservableEventType.PersistenceSaveFailed or
            ServerObservableEventType.AdminSessionKicked or
            ServerObservableEventType.ChatMessageRejected or
            ServerObservableEventType.ChatRateLimitTriggered or
            ServerObservableEventType.ChatInvalidChannel or
            ServerObservableEventType.CrimeRegistrationFailed or
            ServerObservableEventType.BackendError;
    }

    private static IReadOnlyDictionary<string, object?>? RedactSensitivePayload(
        IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is null || payload.Count == 0)
            return payload;

        var redacted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in payload)
            redacted[key] = IsSensitiveKey(key) ? "[REDACTED]" : value;

        return redacted;
    }

    private static bool IsSensitiveKey(string key)
    {
        foreach (var token in SensitiveTokens)
        {
            if (key.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? ExtractString(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string s => string.IsNullOrWhiteSpace(s) ? null : s,
            _ => raw.ToString(),
        };
    }
}

