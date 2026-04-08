using KcdMp.Server.Characters;
using KcdMp.Server.Crime;
using KcdMp.Server.Currency;
using KcdMp.Server.Inventory;
using KcdMp.Server.InventoryRules;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using KcdMp.Server.Respawn;
using Serilog;

namespace KcdMp.Server.Loot;

public sealed class LootableInventoryService : ILootableInventoryService
{
    private readonly IJsonPersistenceStore _store;
    private readonly ICharacterInventoryService _inventory;
    private readonly ICharacterCurrencyService _currency;
    private readonly ICharacterRespawnService _respawn;
    private readonly IInventoryRulesConfigurationService _inventoryRules;
    private readonly ICrimeLawService _crimeLaw;
    private readonly IServerObservabilitySink _observability;
    private readonly LootableInventoryOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _targetLocks = new(StringComparer.Ordinal);

    public LootableInventoryService(
        IJsonPersistenceStore store,
        ICharacterInventoryService inventory,
        ICharacterCurrencyService currency,
        ICharacterRespawnService respawn,
        IInventoryRulesConfigurationService inventoryRules,
        ICrimeLawService? crimeLaw,
        IServerObservabilitySink observability,
        LootableInventoryOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _currency = currency ?? throw new ArgumentNullException(nameof(currency));
        _respawn = respawn ?? throw new ArgumentNullException(nameof(respawn));
        _inventoryRules = inventoryRules ?? throw new ArgumentNullException(nameof(inventoryRules));
        _crimeLaw = crimeLaw ?? new NoOpCrimeLawService();
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new LootableInventoryOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<ChestUpsertResult> UpsertChestAsync(LootableChestRecord chest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chest);
        ArgumentException.ThrowIfNullOrWhiteSpace(chest.ChestId);

        if (!ValidateChest(chest))
            return new ChestUpsertResult(false, "Invalid chest payload.", null);

        var now = DateTimeOffset.UtcNow;
        chest.UpdatedAtUtc = now;
        if (chest.CreatedAtUtc == default)
            chest.CreatedAtUtc = now;

        var save = await SaveChestWithRetryAsync(chest, ct);
        if (!save.saved)
            return new ChestUpsertResult(false, save.failureReason ?? "Chest save failed.", null);

        return new ChestUpsertResult(true, null, CloneChest(chest));
    }

    public async Task<LootableChestRecord?> GetChestAsync(string chestId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chestId);
        var loaded = await _store.LoadAsync<LootableChestRecord>(
            JsonPersistenceDomains.WorldContainers,
            chestId,
            validate: ValidateChest,
            ct);
        return loaded is null ? null : CloneChest(loaded);
    }

    public async Task<LootTransferResult> TransferItemFromCharacterAsync(
        LootCharacterItemTransferRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Quantity <= 0)
            return Denied("Quantity must be greater than zero.");
        if (!ValidateDistance(request.DistanceMeters))
            return Denied("Target is out of range.");
        if (string.IsNullOrWhiteSpace(request.DestinationContainerId))
            request = request with { DestinationContainerId = _options.DefaultDestinationContainerId };

        var lockKey = $"character:{request.TargetCharacterId}";
        if (!await TryAcquireTargetLockAsync(lockKey, ct))
            return DeniedWithConflict(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Character,
                request.TargetCharacterId,
                "Target is currently being looted.");

        try
        {
            EmitStarted(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Character,
                request.TargetCharacterId,
                request.ItemInternalId,
                request.Quantity,
                currencyAmount: null);

            var targetEligibility = await EvaluateCharacterLootEligibilityAsync(
                request.TargetSessionId,
                request.TargetCharacterId,
                request.StealSucceeded,
                request.ExpectedTargetState,
                ct);
            if (!targetEligibility.allowed)
                return DeniedWithReason(request, targetEligibility.denialReason!);

            await MaybeDelayForTestingAsync(ct);

            var targetInventory = await _inventory.GetLoadedForSessionAsync(request.TargetSessionId, ct);
            if (targetInventory is null || !string.Equals(targetInventory.CharacterId, request.TargetCharacterId, StringComparison.Ordinal))
                return DeniedWithReason(request, "Target inventory is not available.");

            var looterInventory = await _inventory.GetLoadedForSessionAsync(request.LooterSessionId, ct);
            if (looterInventory is null || !string.Equals(looterInventory.CharacterId, request.LooterCharacterId, StringComparison.Ordinal))
                return DeniedWithReason(request, "Looter inventory is not available.");

            var sourceContainer = targetInventory.Containers.FirstOrDefault(
                x => string.Equals(x.ContainerId, request.SourceContainerId, StringComparison.Ordinal));
            if (sourceContainer is null)
                return DeniedWithReason(request, "Source container does not exist.");

            var sourceItem = sourceContainer.Items.FirstOrDefault(
                x => string.Equals(x.InternalId, request.ItemInternalId, StringComparison.Ordinal));
            if (sourceItem is null)
                return DeniedWithReason(request, "Source item does not exist.");

            if (!sourceItem.IsStackable && request.Quantity != 1)
                return DeniedWithReason(request, "Non-stackable item quantity must be one.");
            if (request.Quantity > sourceItem.Quantity)
                return DeniedWithReason(request, "Requested quantity exceeds source quantity.");

            var transferItem = CloneItem(sourceItem);
            transferItem.Quantity = request.Quantity;
            var remainingQuantity = sourceItem.Quantity - request.Quantity;

            if (remainingQuantity <= 0)
            {
                var removed = await _inventory.RemoveItemAsync(
                    request.TargetSessionId,
                    request.SourceContainerId,
                    sourceItem.InternalId,
                    ct);
                if (!removed.Applied)
                    return DeniedWithExecutionFailure(request, removed.DenialReason ?? "Target item remove failed.");
            }
            else
            {
                var sourceRemainder = CloneItem(sourceItem);
                sourceRemainder.Quantity = remainingQuantity;
                var sourceUpdate = await _inventory.UpsertItemAsync(
                    request.TargetSessionId,
                    request.SourceContainerId,
                    sourceRemainder,
                    ct);
                if (!sourceUpdate.Applied)
                    return DeniedWithExecutionFailure(request, sourceUpdate.DenialReason ?? "Target item update failed.");
            }

            var destinationItem = BuildDestinationItem(looterInventory, request.DestinationContainerId, transferItem);
            var destinationUpsert = await _inventory.UpsertItemAsync(
                request.LooterSessionId,
                request.DestinationContainerId,
                destinationItem,
                ct);
            if (!destinationUpsert.Applied)
                return DeniedWithExecutionFailure(request, destinationUpsert.DenialReason ?? "Destination item update failed.");

            var targetSave = await _inventory.SaveForSessionAsync(request.TargetSessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
            if (!targetSave.Saved)
                return DeniedWithExecutionFailure(request, targetSave.FailureReason ?? "Target inventory save failed.");

            var looterSave = await _inventory.SaveForSessionAsync(request.LooterSessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
            if (!looterSave.Saved)
                return DeniedWithExecutionFailure(request, looterSave.FailureReason ?? "Looter inventory save failed.");

            EmitSuccess(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Character,
                request.TargetCharacterId,
                destinationItem.InternalId,
                request.Quantity,
                currencyAmount: null);

            await RegisterCharacterLootCrimeAsync(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                request.TargetIdentityId,
                request.TargetCharacterId,
                targetEligibility.targetState,
                targetKind: CrimeTargetKind.Character,
                targetId: request.TargetCharacterId,
                actionCode: "loot_character_item",
                reason: null,
                ct);

            return new LootTransferResult(
                true,
                null,
                new LootTransferSummary(
                    LootTargetKind.Character,
                    request.TargetCharacterId,
                    destinationItem.InternalId,
                    request.Quantity,
                    CurrencyTransferred: 0));
        }
        catch (Exception ex)
        {
            EmitExecutionFailure(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Character,
                request.TargetCharacterId,
                ex.Message);
            return Denied("Loot transfer failed unexpectedly.");
        }
        finally
        {
            await ReleaseTargetLockAsync(lockKey, ct);
        }
    }

    public async Task<LootTransferResult> TransferCurrencyFromCharacterAsync(
        LootCharacterCurrencyTransferRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
            return Denied("Amount must be greater than zero.");
        if (!ValidateDistance(request.DistanceMeters))
            return Denied("Target is out of range.");

        var lockKey = $"character:{request.TargetCharacterId}";
        if (!await TryAcquireTargetLockAsync(lockKey, ct))
            return DeniedWithConflict(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Character,
                request.TargetCharacterId,
                "Target is currently being looted.");

        try
        {
            EmitStarted(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Character,
                request.TargetCharacterId,
                itemInternalId: null,
                quantity: null,
                currencyAmount: request.Amount);

            var targetEligibility = await EvaluateCharacterLootEligibilityAsync(
                request.TargetSessionId,
                request.TargetCharacterId,
                request.StealSucceeded,
                request.ExpectedTargetState,
                ct);
            if (!targetEligibility.allowed)
                return DeniedWithReason(request, targetEligibility.denialReason!);

            await MaybeDelayForTestingAsync(ct);

            var remove = await _currency.AddAsync(request.TargetSessionId, -request.Amount, ct);
            if (!remove.Applied)
                return DeniedWithReason(request, remove.DenialReason ?? "Target currency mutation denied.");

            var add = await _currency.AddAsync(request.LooterSessionId, request.Amount, ct);
            if (!add.Applied)
            {
                var rollback = await _currency.AddAsync(request.TargetSessionId, request.Amount, ct);
                if (!rollback.Applied)
                {
                    EmitLootEvent(
                        ServerObservableEventType.LootDesyncIncident,
                        ServerObservableSeverity.Error,
                        request.LooterSessionId,
                        request.LooterIdentityId,
                        request.LooterCharacterId,
                        new Dictionary<string, object?>
                        {
                            ["target_kind"] = LootTargetKind.Character.ToString(),
                            ["target_id"] = request.TargetCharacterId,
                            ["currency_amount"] = request.Amount,
                        });
                }

                return DeniedWithExecutionFailure(request, add.DenialReason ?? "Looter currency mutation failed.");
            }

            var targetSave = await _currency.SaveForSessionAsync(request.TargetSessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
            if (!targetSave.Saved)
                return DeniedWithExecutionFailure(request, targetSave.FailureReason ?? "Target currency save failed.");

            var looterSave = await _currency.SaveForSessionAsync(request.LooterSessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
            if (!looterSave.Saved)
                return DeniedWithExecutionFailure(request, looterSave.FailureReason ?? "Looter currency save failed.");

            EmitSuccess(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Character,
                request.TargetCharacterId,
                itemInternalId: "currency",
                quantity: 0,
                currencyAmount: request.Amount);

            await RegisterCharacterLootCrimeAsync(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                request.TargetIdentityId,
                request.TargetCharacterId,
                targetEligibility.targetState,
                targetKind: CrimeTargetKind.Character,
                targetId: request.TargetCharacterId,
                actionCode: "loot_character_currency",
                reason: null,
                ct);

            return new LootTransferResult(
                true,
                null,
                new LootTransferSummary(
                    LootTargetKind.Character,
                    request.TargetCharacterId,
                    "currency",
                    0,
                    request.Amount));
        }
        catch (Exception ex)
        {
            EmitExecutionFailure(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Character,
                request.TargetCharacterId,
                ex.Message);
            return Denied("Currency loot transfer failed unexpectedly.");
        }
        finally
        {
            await ReleaseTargetLockAsync(lockKey, ct);
        }
    }

    public async Task<LootTransferResult> TransferItemFromChestAsync(
        LootChestItemTransferRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Quantity <= 0)
            return Denied("Quantity must be greater than zero.");
        if (!ValidateDistance(request.DistanceMeters))
            return Denied("Target is out of range.");
        if (string.IsNullOrWhiteSpace(request.DestinationContainerId))
            request = request with { DestinationContainerId = _options.DefaultDestinationContainerId };

        var lockKey = $"chest:{request.ChestId}";
        if (!await TryAcquireTargetLockAsync(lockKey, ct))
            return DeniedWithConflict(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Chest,
                request.ChestId,
                "Container is currently in use.");

        try
        {
            EmitStarted(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Chest,
                request.ChestId,
                request.ItemInternalId,
                request.Quantity,
                currencyAmount: null);

            var chest = await GetChestAsync(request.ChestId, ct);
            if (chest is null)
                return DeniedWithReason(request, "Chest does not exist.");
            if (request.ExpectedChestState.HasValue && request.ExpectedChestState.Value != chest.State)
                return DeniedWithReason(request, "Chest state changed.");

            var access = await _inventoryRules.EvaluateContainerAccessAsync(
                new InventoryContainerAccessEvaluationRequest(
                    chest.ContainerTypeId,
                    InventoryContainerKind.Chest,
                    request.HasValidKey,
                    request.IsFullyOpen || chest.State == ChestLockState.Open,
                    request.LockpickSucceeded),
                ct);
            if (!access.Allowed)
                return DeniedWithReason(request, access.DenialReason ?? "Chest access denied.");

            await MaybeDelayForTestingAsync(ct);

            var sourceItem = chest.Items.FirstOrDefault(x => string.Equals(x.InternalId, request.ItemInternalId, StringComparison.Ordinal));
            if (sourceItem is null)
                return DeniedWithReason(request, "Chest item does not exist.");
            if (!sourceItem.IsStackable && request.Quantity != 1)
                return DeniedWithReason(request, "Non-stackable item quantity must be one.");
            if (request.Quantity > sourceItem.Quantity)
                return DeniedWithReason(request, "Requested quantity exceeds source quantity.");

            var looterInventory = await _inventory.GetLoadedForSessionAsync(request.LooterSessionId, ct);
            if (looterInventory is null || !string.Equals(looterInventory.CharacterId, request.LooterCharacterId, StringComparison.Ordinal))
                return DeniedWithReason(request, "Looter inventory is not available.");

            var transferItem = CloneItem(sourceItem);
            transferItem.Quantity = request.Quantity;
            var remainingQuantity = sourceItem.Quantity - request.Quantity;
            if (remainingQuantity <= 0)
                chest.Items.RemoveAll(x => string.Equals(x.InternalId, sourceItem.InternalId, StringComparison.Ordinal));
            else
                sourceItem.Quantity = remainingQuantity;

            var destinationItem = BuildDestinationItem(looterInventory, request.DestinationContainerId, transferItem);
            var destinationUpsert = await _inventory.UpsertItemAsync(request.LooterSessionId, request.DestinationContainerId, destinationItem, ct);
            if (!destinationUpsert.Applied)
                return DeniedWithExecutionFailure(request, destinationUpsert.DenialReason ?? "Destination item update failed.");

            var chestSave = await SaveChestWithRetryAsync(chest, ct);
            if (!chestSave.saved)
                return DeniedWithExecutionFailure(request, chestSave.failureReason ?? "Chest save failed.");

            var looterSave = await _inventory.SaveForSessionAsync(request.LooterSessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
            if (!looterSave.Saved)
                return DeniedWithExecutionFailure(request, looterSave.FailureReason ?? "Looter inventory save failed.");

            EmitSuccess(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Chest,
                request.ChestId,
                destinationItem.InternalId,
                request.Quantity,
                currencyAmount: null);

            if (!request.HasValidKey)
            {
                await RegisterContainerAccessCrimeAsync(
                    request.LooterSessionId,
                    request.LooterIdentityId,
                    request.LooterCharacterId,
                    request.ChestId,
                    actionCode: "loot_chest_item",
                    reason: request.LockpickSucceeded ? "lockpick" : "unauthorized_access",
                    ct);
            }

            return new LootTransferResult(
                true,
                null,
                new LootTransferSummary(
                    LootTargetKind.Chest,
                    request.ChestId,
                    destinationItem.InternalId,
                    request.Quantity,
                    CurrencyTransferred: 0));
        }
        catch (Exception ex)
        {
            EmitExecutionFailure(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Chest,
                request.ChestId,
                ex.Message);
            return Denied("Chest loot transfer failed unexpectedly.");
        }
        finally
        {
            await ReleaseTargetLockAsync(lockKey, ct);
        }
    }

    public async Task<LootTransferResult> TransferCurrencyFromChestAsync(
        LootChestCurrencyTransferRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
            return Denied("Amount must be greater than zero.");
        if (!ValidateDistance(request.DistanceMeters))
            return Denied("Target is out of range.");

        var lockKey = $"chest:{request.ChestId}";
        if (!await TryAcquireTargetLockAsync(lockKey, ct))
            return DeniedWithConflict(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Chest,
                request.ChestId,
                "Container is currently in use.");

        try
        {
            EmitStarted(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Chest,
                request.ChestId,
                itemInternalId: null,
                quantity: null,
                currencyAmount: request.Amount);

            var chest = await GetChestAsync(request.ChestId, ct);
            if (chest is null)
                return DeniedWithReason(request, "Chest does not exist.");
            if (request.ExpectedChestState.HasValue && request.ExpectedChestState.Value != chest.State)
                return DeniedWithReason(request, "Chest state changed.");

            var access = await _inventoryRules.EvaluateContainerAccessAsync(
                new InventoryContainerAccessEvaluationRequest(
                    chest.ContainerTypeId,
                    InventoryContainerKind.Chest,
                    request.HasValidKey,
                    request.IsFullyOpen || chest.State == ChestLockState.Open,
                    request.LockpickSucceeded),
                ct);
            if (!access.Allowed)
                return DeniedWithReason(request, access.DenialReason ?? "Chest access denied.");

            await MaybeDelayForTestingAsync(ct);

            if (chest.CurrencyBalance < request.Amount)
                return DeniedWithReason(request, "Chest balance is insufficient.");

            chest.CurrencyBalance -= request.Amount;
            var looterAdd = await _currency.AddAsync(request.LooterSessionId, request.Amount, ct);
            if (!looterAdd.Applied)
            {
                chest.CurrencyBalance += request.Amount;
                return DeniedWithExecutionFailure(request, looterAdd.DenialReason ?? "Looter currency mutation failed.");
            }

            var chestSave = await SaveChestWithRetryAsync(chest, ct);
            if (!chestSave.saved)
            {
                chest.CurrencyBalance += request.Amount;
                await _currency.AddAsync(request.LooterSessionId, -request.Amount, ct);
                return DeniedWithExecutionFailure(request, chestSave.failureReason ?? "Chest save failed.");
            }

            var looterSave = await _currency.SaveForSessionAsync(request.LooterSessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
            if (!looterSave.Saved)
                return DeniedWithExecutionFailure(request, looterSave.FailureReason ?? "Looter currency save failed.");

            EmitSuccess(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Chest,
                request.ChestId,
                itemInternalId: "currency",
                quantity: 0,
                currencyAmount: request.Amount);

            if (!request.HasValidKey)
            {
                await RegisterContainerAccessCrimeAsync(
                    request.LooterSessionId,
                    request.LooterIdentityId,
                    request.LooterCharacterId,
                    request.ChestId,
                    actionCode: "loot_chest_currency",
                    reason: request.LockpickSucceeded ? "lockpick" : "unauthorized_access",
                    ct);
            }

            return new LootTransferResult(
                true,
                null,
                new LootTransferSummary(
                    LootTargetKind.Chest,
                    request.ChestId,
                    "currency",
                    0,
                    request.Amount));
        }
        catch (Exception ex)
        {
            EmitExecutionFailure(
                request.LooterSessionId,
                request.LooterIdentityId,
                request.LooterCharacterId,
                LootTargetKind.Chest,
                request.ChestId,
                ex.Message);
            return Denied("Chest currency loot transfer failed unexpectedly.");
        }
        finally
        {
            await ReleaseTargetLockAsync(lockKey, ct);
        }
    }

    private async Task<(bool allowed, string? denialReason, CharacterDefeatState? targetState)> EvaluateCharacterLootEligibilityAsync(
        Guid targetSessionId,
        string targetCharacterId,
        bool stealSucceeded,
        CharacterDefeatState? expectedTargetState,
        CancellationToken ct)
    {
        var targetRespawn = await _respawn.GetLoadedForSessionAsync(targetSessionId, ct);
        if (targetRespawn is null || !string.Equals(targetRespawn.CharacterId, targetCharacterId, StringComparison.Ordinal))
            return (false, "Target lifecycle is not available.", null);

        if (expectedTargetState.HasValue && expectedTargetState.Value != targetRespawn.State)
            return (false, "Target state changed.", targetRespawn.State);

        var ruleState = await _inventoryRules.GetCharacterStateAsync(targetCharacterId, ct);
        var directLootAllowed = targetRespawn.State == CharacterDefeatState.Unconscious && (ruleState?.IsLootable ?? false);
        if (directLootAllowed)
            return (true, null, targetRespawn.State);

        var stealLootAllowed = targetRespawn.State == CharacterDefeatState.Alive && stealSucceeded;
        if (stealLootAllowed)
            return (true, null, targetRespawn.State);

        return (false, "Target is not currently lootable.", targetRespawn.State);
    }

    private async Task RegisterCharacterLootCrimeAsync(
        Guid sessionId,
        string looterIdentityId,
        string looterCharacterId,
        string targetIdentityId,
        string targetCharacterId,
        CharacterDefeatState? targetState,
        CrimeTargetKind targetKind,
        string targetId,
        string actionCode,
        string? reason,
        CancellationToken ct)
    {
        var crimeType = targetState switch
        {
            CharacterDefeatState.Alive => CrimeType.TheftFromConsciousCharacter,
            CharacterDefeatState.Unconscious => CrimeType.LootFromUnconsciousCharacter,
            _ => (CrimeType?)null,
        };

        if (!crimeType.HasValue)
            return;

        var metadata = new Dictionary<string, object?>
        {
            ["action_code"] = actionCode,
            ["target_state"] = targetState?.ToString(),
        };

        var registered = await _crimeLaw.RegisterAutomaticCrimeAsync(
            new AutomaticCrimeRegistrationRequest(
                SessionId: sessionId,
                IdentityId: looterIdentityId,
                CharacterId: looterCharacterId,
                CrimeType: crimeType.Value,
                TargetKind: targetKind,
                TargetId: targetId,
                TargetIdentityId: targetIdentityId,
                TargetCharacterId: targetCharacterId,
                ActionCode: actionCode,
                Reason: reason,
                Metadata: metadata),
            ct);

        if (!registered.Applied)
        {
            _logger.Warning(
                "[loot] automatic crime registration did not apply identity_id={IdentityId} character_id={CharacterId} reason={Reason}",
                looterIdentityId,
                looterCharacterId,
                registered.DenialReason);
        }
    }

    private async Task RegisterContainerAccessCrimeAsync(
        Guid sessionId,
        string looterIdentityId,
        string looterCharacterId,
        string chestId,
        string actionCode,
        string? reason,
        CancellationToken ct)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["action_code"] = actionCode,
            ["container_id"] = chestId,
        };

        var registered = await _crimeLaw.RegisterAutomaticCrimeAsync(
            new AutomaticCrimeRegistrationRequest(
                SessionId: sessionId,
                IdentityId: looterIdentityId,
                CharacterId: looterCharacterId,
                CrimeType: CrimeType.UnauthorizedContainerAccess,
                TargetKind: CrimeTargetKind.Container,
                TargetId: chestId,
                ActionCode: actionCode,
                Reason: reason,
                Metadata: metadata),
            ct);

        if (!registered.Applied)
        {
            _logger.Warning(
                "[loot] container crime registration did not apply identity_id={IdentityId} character_id={CharacterId} chest_id={ChestId} reason={Reason}",
                looterIdentityId,
                looterCharacterId,
                chestId,
                registered.DenialReason);
        }
    }

    private bool ValidateDistance(double distanceMeters)
        => !double.IsNaN(distanceMeters)
           && !double.IsInfinity(distanceMeters)
           && distanceMeters >= 0
           && distanceMeters <= _options.MaxInteractionDistanceMeters;

    private async Task<bool> TryAcquireTargetLockAsync(string lockKey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _targetLocks.Add(lockKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReleaseTargetLockAsync(string lockKey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _targetLocks.Remove(lockKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MaybeDelayForTestingAsync(CancellationToken ct)
    {
        if (_options.SimulatedTransferDelay > TimeSpan.Zero)
            await Task.Delay(_options.SimulatedTransferDelay, ct);
    }

    private async Task<(bool saved, string? failureReason)> SaveChestWithRetryAsync(
        LootableChestRecord chest,
        CancellationToken ct)
    {
        Exception? lastError = null;
        var maxAttempts = Math.Max(1, _options.SaveRetryCount + 1);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                chest.UpdatedAtUtc = now;
                chest.LastSavedAtUtc = now;
                await _store.SaveAsync(JsonPersistenceDomains.WorldContainers, chest.ChestId, chest, ct);
                return (true, null);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < maxAttempts && _options.SaveRetryDelay > TimeSpan.Zero)
                    await Task.Delay(_options.SaveRetryDelay, ct);
            }
        }

        _logger.Warning("[loot] chest save failed chest_id={ChestId}", chest.ChestId);
        return (false, lastError?.Message);
    }

    private static InventoryItemRecord BuildDestinationItem(
        CharacterInventoryRecord looterInventory,
        string destinationContainerId,
        InventoryItemRecord transferItem)
    {
        var destination = looterInventory.Containers.FirstOrDefault(
            x => string.Equals(x.ContainerId, destinationContainerId, StringComparison.Ordinal));

        if (destination is not null && transferItem.IsStackable)
        {
            var existingStack = destination.Items.FirstOrDefault(x =>
                x.IsStackable &&
                string.Equals(x.ItemType, transferItem.ItemType, StringComparison.Ordinal) &&
                string.Equals(x.ItemRef, transferItem.ItemRef, StringComparison.Ordinal));
            if (existingStack is not null)
            {
                var merged = CloneItem(existingStack);
                merged.Quantity += transferItem.Quantity;
                return merged;
            }
        }

        var candidate = CloneItem(transferItem);
        if (destination is not null && destination.Items.Any(x => string.Equals(x.InternalId, candidate.InternalId, StringComparison.Ordinal)))
            candidate.InternalId = $"it_loot_{Guid.NewGuid():N}";
        return candidate;
    }

    private static bool ValidateChest(LootableChestRecord chest)
    {
        if (chest is null || string.IsNullOrWhiteSpace(chest.ChestId) || string.IsNullOrWhiteSpace(chest.ContainerTypeId))
            return false;
        if (!Enum.IsDefined(chest.State) || chest.CurrencyBalance < 0)
            return false;

        return chest.Items.All(ValidateItem);
    }

    private static bool ValidateItem(InventoryItemRecord item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.InternalId))
            return false;
        if (string.IsNullOrWhiteSpace(item.ItemType) || string.IsNullOrWhiteSpace(item.ItemRef))
            return false;
        if (item.Quantity <= 0)
            return false;
        if (!item.IsStackable && item.Quantity != 1)
            return false;
        return true;
    }

    private static LootableChestRecord CloneChest(LootableChestRecord source)
    {
        return new LootableChestRecord
        {
            ChestId = source.ChestId,
            ContainerTypeId = source.ContainerTypeId,
            KeyId = source.KeyId,
            State = source.State,
            Position = new LootableChestPosition
            {
                X = source.Position.X,
                Y = source.Position.Y,
                Z = source.Position.Z,
            },
            Items = source.Items.Select(CloneItem).ToList(),
            CurrencyBalance = source.CurrencyBalance,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            LastSavedAtUtc = source.LastSavedAtUtc,
        };
    }

    private static InventoryItemRecord CloneItem(InventoryItemRecord source)
    {
        return new InventoryItemRecord
        {
            InternalId = source.InternalId,
            ItemType = source.ItemType,
            ItemRef = source.ItemRef,
            Quantity = source.Quantity,
            IsStackable = source.IsStackable,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
            Flags = source.Flags.ToList(),
        };
    }

    private LootTransferResult DeniedWithConflict(
        Guid sessionId,
        string identityId,
        string characterId,
        LootTargetKind targetKind,
        string targetId,
        string denialReason)
    {
        EmitLootEvent(
            ServerObservableEventType.LootAccessConflict,
            ServerObservableSeverity.Warning,
            sessionId,
            identityId,
            characterId,
            new Dictionary<string, object?>
            {
                ["target_kind"] = targetKind.ToString(),
                ["target_id"] = targetId,
                ["reason"] = denialReason,
            });
        return Denied(denialReason);
    }

    private LootTransferResult DeniedWithReason(
        LootCharacterItemTransferRequest request,
        string denialReason)
    {
        EmitDenied(request.LooterSessionId, request.LooterIdentityId, request.LooterCharacterId, LootTargetKind.Character, request.TargetCharacterId, denialReason);
        return Denied(denialReason);
    }

    private LootTransferResult DeniedWithReason(
        LootCharacterCurrencyTransferRequest request,
        string denialReason)
    {
        EmitDenied(request.LooterSessionId, request.LooterIdentityId, request.LooterCharacterId, LootTargetKind.Character, request.TargetCharacterId, denialReason);
        return Denied(denialReason);
    }

    private LootTransferResult DeniedWithReason(
        LootChestItemTransferRequest request,
        string denialReason)
    {
        EmitDenied(request.LooterSessionId, request.LooterIdentityId, request.LooterCharacterId, LootTargetKind.Chest, request.ChestId, denialReason);
        return Denied(denialReason);
    }

    private LootTransferResult DeniedWithReason(
        LootChestCurrencyTransferRequest request,
        string denialReason)
    {
        EmitDenied(request.LooterSessionId, request.LooterIdentityId, request.LooterCharacterId, LootTargetKind.Chest, request.ChestId, denialReason);
        return Denied(denialReason);
    }

    private LootTransferResult DeniedWithExecutionFailure(
        LootCharacterItemTransferRequest request,
        string denialReason)
    {
        EmitExecutionFailure(request.LooterSessionId, request.LooterIdentityId, request.LooterCharacterId, LootTargetKind.Character, request.TargetCharacterId, denialReason);
        return Denied(denialReason);
    }

    private LootTransferResult DeniedWithExecutionFailure(
        LootCharacterCurrencyTransferRequest request,
        string denialReason)
    {
        EmitExecutionFailure(request.LooterSessionId, request.LooterIdentityId, request.LooterCharacterId, LootTargetKind.Character, request.TargetCharacterId, denialReason);
        return Denied(denialReason);
    }

    private LootTransferResult DeniedWithExecutionFailure(
        LootChestItemTransferRequest request,
        string denialReason)
    {
        EmitExecutionFailure(request.LooterSessionId, request.LooterIdentityId, request.LooterCharacterId, LootTargetKind.Chest, request.ChestId, denialReason);
        return Denied(denialReason);
    }

    private LootTransferResult DeniedWithExecutionFailure(
        LootChestCurrencyTransferRequest request,
        string denialReason)
    {
        EmitExecutionFailure(request.LooterSessionId, request.LooterIdentityId, request.LooterCharacterId, LootTargetKind.Chest, request.ChestId, denialReason);
        return Denied(denialReason);
    }

    private void EmitStarted(
        Guid sessionId,
        string identityId,
        string characterId,
        LootTargetKind targetKind,
        string targetId,
        string? itemInternalId,
        int? quantity,
        long? currencyAmount)
    {
        EmitLootEvent(
            ServerObservableEventType.LootStarted,
            ServerObservableSeverity.Information,
            sessionId,
            identityId,
            characterId,
            new Dictionary<string, object?>
            {
                ["target_kind"] = targetKind.ToString(),
                ["target_id"] = targetId,
                ["item_internal_id"] = itemInternalId,
                ["quantity"] = quantity,
                ["currency_amount"] = currencyAmount,
            });
    }

    private void EmitSuccess(
        Guid sessionId,
        string identityId,
        string characterId,
        LootTargetKind targetKind,
        string targetId,
        string itemInternalId,
        int quantity,
        long? currencyAmount)
    {
        EmitLootEvent(
            ServerObservableEventType.LootAllowed,
            ServerObservableSeverity.Information,
            sessionId,
            identityId,
            characterId,
            new Dictionary<string, object?>
            {
                ["target_kind"] = targetKind.ToString(),
                ["target_id"] = targetId,
            });

        EmitLootEvent(
            ServerObservableEventType.LootItemTransferred,
            ServerObservableSeverity.Information,
            sessionId,
            identityId,
            characterId,
            new Dictionary<string, object?>
            {
                ["target_kind"] = targetKind.ToString(),
                ["target_id"] = targetId,
                ["item_internal_id"] = itemInternalId,
                ["quantity"] = quantity,
                ["currency_amount"] = currencyAmount,
            });
    }

    private void EmitDenied(
        Guid sessionId,
        string identityId,
        string characterId,
        LootTargetKind targetKind,
        string targetId,
        string denialReason)
    {
        EmitLootEvent(
            ServerObservableEventType.LootDenied,
            ServerObservableSeverity.Warning,
            sessionId,
            identityId,
            characterId,
            new Dictionary<string, object?>
            {
                ["target_kind"] = targetKind.ToString(),
                ["target_id"] = targetId,
                ["reason"] = denialReason,
            });
    }

    private void EmitExecutionFailure(
        Guid sessionId,
        string identityId,
        string characterId,
        LootTargetKind targetKind,
        string targetId,
        string failureReason)
    {
        EmitLootEvent(
            ServerObservableEventType.LootExecutionFailed,
            ServerObservableSeverity.Error,
            sessionId,
            identityId,
            characterId,
            new Dictionary<string, object?>
            {
                ["target_kind"] = targetKind.ToString(),
                ["target_id"] = targetId,
                ["reason"] = failureReason,
            });
    }

    private void EmitLootEvent(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        Guid? sessionId,
        string? identityId,
        string? characterId,
        IReadOnlyDictionary<string, object?>? payload)
    {
        _observability.Emit(new ServerObservableEvent(
            type,
            ServerObservableComponent.Persistence,
            severity,
            DateTimeOffset.UtcNow,
            sessionId,
            identityId,
            characterId,
            "Loot lifecycle event.",
            payload));
    }

    private static LootTransferResult Denied(string denialReason)
        => new(false, denialReason, null);
}
