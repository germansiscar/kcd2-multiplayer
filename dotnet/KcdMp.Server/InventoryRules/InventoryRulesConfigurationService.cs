using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using KcdMp.Server.Respawn;
using Serilog;

namespace KcdMp.Server.InventoryRules;

public sealed class InventoryRulesConfigurationService : IInventoryRulesConfigurationService
{
    private readonly IJsonPersistenceStore _store;
    private readonly IServerObservabilitySink _observability;
    private readonly InventoryRulesOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InventoryRulesConfigurationRecord? _cachedConfiguration;

    public InventoryRulesConfigurationService(
        IJsonPersistenceStore store,
        IServerObservabilitySink observability,
        InventoryRulesOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new InventoryRulesOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<InventoryRulesConfigurationRecord> GetActiveConfigurationAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cachedConfiguration is not null)
                return CloneConfig(_cachedConfiguration);
        }
        finally
        {
            _gate.Release();
        }

        var createdDefault = false;
        InventoryRulesConfigurationRecord loaded;
        try
        {
            loaded = await _store.LoadAsync<InventoryRulesConfigurationRecord>(
                JsonPersistenceDomains.Config,
                _options.ConfigDocumentId,
                validate: ValidateConfiguration,
                ct) ?? CreateDefaultConfiguration();

            if (loaded.CreatedAtUtc == default)
                loaded.CreatedAtUtc = DateTimeOffset.UtcNow;
            loaded.UpdatedAtUtc = DateTimeOffset.UtcNow;
            EnsureDefaultContainerRules(loaded);
            createdDefault = !await ConfigurationExistsAsync(ct);
            if (createdDefault)
            {
                await _store.SaveAsync(
                    JsonPersistenceDomains.Config,
                    _options.ConfigDocumentId,
                    loaded,
                    ct);
            }
        }
        catch (Exception ex) when (ex is PersistenceValidationException or PersistenceLoadException)
        {
            Emit(
                ServerObservableEventType.InventoryRulesConfigValidationFailed,
                ServerObservableSeverity.Error,
                "Inventory rules configuration is invalid.",
                payload: new Dictionary<string, object?>
                {
                    ["exception"] = ex.Message,
                    ["config_id"] = _options.ConfigDocumentId,
                });
            throw;
        }

        await _gate.WaitAsync(ct);
        try
        {
            _cachedConfiguration = CloneConfig(loaded);
            return CloneConfig(_cachedConfiguration);
        }
        finally
        {
            _gate.Release();
            Emit(
                ServerObservableEventType.InventoryRulesConfigLoaded,
                ServerObservableSeverity.Information,
                "Inventory rules configuration loaded.",
                payload: new Dictionary<string, object?>
                {
                    ["config_id"] = _options.ConfigDocumentId,
                    ["created_default"] = createdDefault,
                    ["character_mode"] = loaded.CharacterMode.ToString(),
                });
        }
    }

    public async Task<CharacterInventoryRuleStateRecord?> GetCharacterStateAsync(
        string characterId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        var loaded = await _store.LoadAsync<CharacterInventoryRuleStateRecord>(
            JsonPersistenceDomains.InventoryRules,
            characterId,
            validate: ValidateCharacterState,
            ct);
        return loaded is null ? null : CloneCharacterState(loaded);
    }

    public async Task<InventoryRuleApplyResult> ApplyCharacterDefeatStateAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CharacterDefeatState defeatState,
        InventoryRuleTrigger trigger,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        var config = await GetActiveConfigurationAsync(ct);
        var previous = await GetCharacterStateAsync(characterId, ct) ?? new CharacterInventoryRuleStateRecord
        {
            CharacterId = characterId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var nextState = DetermineCharacterState(config, defeatState);
        var changed = previous.State != nextState
                      || previous.LastDefeatState != defeatState
                      || previous.LastTrigger != trigger;

        previous.State = nextState;
        previous.IsLootable = nextState == InventoryCharacterRuleState.Lootable;
        previous.LastDefeatState = defeatState;
        previous.LastTrigger = trigger;
        previous.UpdatedAtUtc = DateTimeOffset.UtcNow;

        if (!changed)
            return new InventoryRuleApplyResult(true, null, CloneCharacterState(previous), Changed: false);

        var save = await SaveWithRetryAsync(sessionId, identityId, previous, ct);
        if (!save.Saved)
        {
            Emit(
                ServerObservableEventType.InventoryRuleApplyFailed,
                ServerObservableSeverity.Error,
                "Inventory rule application could not be persisted.",
                sessionId,
                identityId,
                characterId,
                payload: new Dictionary<string, object?>
                {
                    ["failure_reason"] = save.FailureReason,
                    ["attempts"] = save.Attempts,
                    ["trigger"] = trigger.ToString(),
                    ["defeat_state"] = defeatState.ToString(),
                });
            return new InventoryRuleApplyResult(false, save.FailureReason ?? "Inventory rule save failed.", null, Changed: true);
        }

        Emit(
            ServerObservableEventType.InventoryRuleApplied,
            ServerObservableSeverity.Information,
            "Inventory rule applied.",
            sessionId,
            identityId,
            characterId,
            payload: new Dictionary<string, object?>
            {
                ["trigger"] = trigger.ToString(),
                ["defeat_state"] = defeatState.ToString(),
                ["state"] = nextState.ToString(),
            });

        Emit(
            ServerObservableEventType.InventoryLootabilityChanged,
            ServerObservableSeverity.Information,
            "Inventory lootability changed.",
            sessionId,
            identityId,
            characterId,
            payload: new Dictionary<string, object?>
            {
                ["is_lootable"] = previous.IsLootable,
                ["character_mode"] = config.CharacterMode.ToString(),
            });

        return new InventoryRuleApplyResult(true, null, CloneCharacterState(previous), Changed: true);
    }

    public async Task<InventoryContainerAccessEvaluationResult> EvaluateContainerAccessAsync(
        InventoryContainerAccessEvaluationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContainerTypeId);

        var config = await GetActiveConfigurationAsync(ct);
        var rule = ResolveContainerRule(config, request);
        if (request.HasValidKey)
        {
            return new InventoryContainerAccessEvaluationResult(
                true,
                InventoryContainerAccessState.KeyAuthorized,
                rule.ContainerTypeId,
                null);
        }

        if (rule.AllowAccessWhenFullyOpen && request.IsFullyOpen)
        {
            return new InventoryContainerAccessEvaluationResult(
                true,
                InventoryContainerAccessState.OpenAuthorized,
                rule.ContainerTypeId,
                null);
        }

        if (rule.AllowAccessWhenLockpickSucceeded && request.LockpickSucceeded)
        {
            return new InventoryContainerAccessEvaluationResult(
                true,
                InventoryContainerAccessState.LockpickAuthorized,
                rule.ContainerTypeId,
                null);
        }

        if (!rule.RequiresValidKey)
        {
            return new InventoryContainerAccessEvaluationResult(
                true,
                InventoryContainerAccessState.PolicyAuthorized,
                rule.ContainerTypeId,
                null);
        }

        return new InventoryContainerAccessEvaluationResult(
            false,
            InventoryContainerAccessState.Denied,
            rule.ContainerTypeId,
            "Container access denied by inventory policy.");
    }

    private async Task<(bool Saved, string? FailureReason, int Attempts)> SaveWithRetryAsync(
        Guid sessionId,
        string identityId,
        CharacterInventoryRuleStateRecord state,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _options.SaveRetryCount + 1);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                state.LastSavedAtUtc = DateTimeOffset.UtcNow;
                await _store.SaveAsync(JsonPersistenceDomains.InventoryRules, state.CharacterId, state, ct);
                return (true, null, attempt);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < maxAttempts && _options.SaveRetryDelay > TimeSpan.Zero)
                    await Task.Delay(_options.SaveRetryDelay, ct);
            }
        }

        _logger.Warning(
            "[inventory-rules] persistent save incident character_id={CharacterId} session_id={SessionId}",
            state.CharacterId,
            sessionId);
        return (false, lastError?.Message, maxAttempts);
    }

    private static InventoryCharacterRuleState DetermineCharacterState(
        InventoryRulesConfigurationRecord config,
        CharacterDefeatState defeatState)
    {
        return defeatState switch
        {
            CharacterDefeatState.Unconscious when config.CharacterMode == InventoryCharacterMode.PersistentLootableWhenUnconscious
                => InventoryCharacterRuleState.Lootable,
            CharacterDefeatState.PendingRespawn => InventoryCharacterRuleState.Protected,
            _ => InventoryCharacterRuleState.Normal,
        };
    }

    private static InventoryContainerRuleRecord ResolveContainerRule(
        InventoryRulesConfigurationRecord config,
        InventoryContainerAccessEvaluationRequest request)
    {
        if (config.ContainerRules.TryGetValue(request.ContainerTypeId, out var direct))
            return direct;

        var kindRuleKey = request.ContainerKind switch
        {
            InventoryContainerKind.CharacterInventory => "character_main",
            InventoryContainerKind.Chest => "chest",
            _ => "generic",
        };
        if (config.ContainerRules.TryGetValue(kindRuleKey, out var byKind))
            return byKind;

        return new InventoryContainerRuleRecord
        {
            ContainerTypeId = request.ContainerTypeId,
            ContainerKind = request.ContainerKind,
            AffectedByDefeat = request.ContainerKind != InventoryContainerKind.Chest,
            RequiresValidKey = false,
            AllowAccessWhenFullyOpen = true,
            AllowAccessWhenLockpickSucceeded = request.ContainerKind == InventoryContainerKind.Chest,
        };
    }

    private async Task<bool> ConfigurationExistsAsync(CancellationToken ct)
    {
        var loaded = await _store.LoadAsync<InventoryRulesConfigurationRecord>(
            JsonPersistenceDomains.Config,
            _options.ConfigDocumentId,
            validate: null,
            ct);
        return loaded is not null;
    }

    private static InventoryRulesConfigurationRecord CreateDefaultConfiguration()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new InventoryRulesConfigurationRecord
        {
            ConfigVersion = "inventory_rules_v1",
            CharacterMode = InventoryCharacterMode.PersistentNonLootable,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        EnsureDefaultContainerRules(config);
        return config;
    }

    private static void EnsureDefaultContainerRules(InventoryRulesConfigurationRecord config)
    {
        config.ContainerRules ??= new Dictionary<string, InventoryContainerRuleRecord>(StringComparer.Ordinal);
        if (!config.ContainerRules.ContainsKey("character_main"))
        {
            config.ContainerRules["character_main"] = new InventoryContainerRuleRecord
            {
                ContainerTypeId = "character_main",
                ContainerKind = InventoryContainerKind.CharacterInventory,
                AffectedByDefeat = true,
                RequiresValidKey = false,
                AllowAccessWhenFullyOpen = false,
                AllowAccessWhenLockpickSucceeded = false,
                AllowMultipleKeyHolders = false,
            };
        }

        if (!config.ContainerRules.ContainsKey("chest"))
        {
            config.ContainerRules["chest"] = new InventoryContainerRuleRecord
            {
                ContainerTypeId = "chest",
                ContainerKind = InventoryContainerKind.Chest,
                AffectedByDefeat = false,
                RequiresValidKey = true,
                AllowAccessWhenFullyOpen = true,
                AllowAccessWhenLockpickSucceeded = true,
                AllowMultipleKeyHolders = true,
            };
        }
    }

    private static bool ValidateConfiguration(InventoryRulesConfigurationRecord config)
    {
        if (config is null)
            return false;
        if (string.IsNullOrWhiteSpace(config.ConfigVersion))
            return false;
        if (!Enum.IsDefined(config.CharacterMode))
            return false;
        if (config.ContainerRules is null)
            return false;

        foreach (var kvp in config.ContainerRules)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
                return false;
            if (kvp.Value is null)
                return false;
            if (string.IsNullOrWhiteSpace(kvp.Value.ContainerTypeId))
                return false;
            if (!Enum.IsDefined(kvp.Value.ContainerKind))
                return false;
        }

        return true;
    }

    private static bool ValidateCharacterState(CharacterInventoryRuleStateRecord state)
    {
        if (state is null)
            return false;
        if (string.IsNullOrWhiteSpace(state.CharacterId))
            return false;
        if (!Enum.IsDefined(state.State))
            return false;
        if (!Enum.IsDefined(state.LastDefeatState))
            return false;
        if (!Enum.IsDefined(state.LastTrigger))
            return false;
        return true;
    }

    private static InventoryRulesConfigurationRecord CloneConfig(InventoryRulesConfigurationRecord source)
    {
        return new InventoryRulesConfigurationRecord
        {
            ConfigVersion = source.ConfigVersion,
            CharacterMode = source.CharacterMode,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
            ContainerRules = source.ContainerRules.ToDictionary(
                kvp => kvp.Key,
                kvp => new InventoryContainerRuleRecord
                {
                    ContainerTypeId = kvp.Value.ContainerTypeId,
                    ContainerKind = kvp.Value.ContainerKind,
                    AffectedByDefeat = kvp.Value.AffectedByDefeat,
                    RequiresValidKey = kvp.Value.RequiresValidKey,
                    AllowAccessWhenFullyOpen = kvp.Value.AllowAccessWhenFullyOpen,
                    AllowAccessWhenLockpickSucceeded = kvp.Value.AllowAccessWhenLockpickSucceeded,
                    AllowMultipleKeyHolders = kvp.Value.AllowMultipleKeyHolders,
                    Metadata = new Dictionary<string, string>(kvp.Value.Metadata, StringComparer.Ordinal),
                },
                StringComparer.Ordinal),
        };
    }

    private static CharacterInventoryRuleStateRecord CloneCharacterState(CharacterInventoryRuleStateRecord source)
    {
        return new CharacterInventoryRuleStateRecord
        {
            CharacterId = source.CharacterId,
            State = source.State,
            IsLootable = source.IsLootable,
            LastDefeatState = source.LastDefeatState,
            LastTrigger = source.LastTrigger,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            LastSavedAtUtc = source.LastSavedAtUtc,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
        };
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
            Component: ServerObservableComponent.Persistence,
            Severity: severity,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            SessionId: sessionId,
            IdentityId: identityId,
            CharacterId: characterId,
            Message: message,
            Payload: payload));
    }
}
