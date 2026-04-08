using KcdMp.Server.Characters;
using KcdMp.Server.Currency;
using KcdMp.Server.Inventory;
using KcdMp.Server.Observability;
using Serilog;

namespace KcdMp.Server.Economy;

public sealed class EconomyAppliedService : IEconomyAppliedService
{
    private readonly ICharacterInventoryService _inventory;
    private readonly ICharacterCurrencyService _currency;
    private readonly IServerObservabilitySink _observability;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EconomyAppliedService(
        ICharacterInventoryService inventory,
        ICharacterCurrencyService currency,
        IServerObservabilitySink observability,
        EconomyAppliedOptions? options = null,
        ILogger? logger = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _currency = currency ?? throw new ArgumentNullException(nameof(currency));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        Options = options ?? new EconomyAppliedOptions();
        _logger = logger ?? Log.Logger;
    }

    public EconomyAppliedOptions Options { get; }

    public async Task<EconomyMoneySyncResult> EnsureMoneyObjectForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        await _gate.WaitAsync(ct);
        try
        {
            var inventory = await _inventory.GetLoadedForSessionAsync(sessionId, ct);
            if (inventory is null || !string.Equals(inventory.CharacterId, characterId, StringComparison.Ordinal))
                return new EconomyMoneySyncResult(false, "Inventory is not available for this session.", 0);

            var currency = await _currency.GetLoadedForSessionAsync(sessionId, ct);
            if (currency is null || !string.Equals(currency.CharacterId, characterId, StringComparison.Ordinal))
                return new EconomyMoneySyncResult(false, "Currency is not available for this session.", 0);

            var moneyFromInventory = TryReadMoneyAmount(inventory);
            var canonical = moneyFromInventory ?? currency.Balance;
            if (canonical < 0)
                return new EconomyMoneySyncResult(false, "Money amount cannot be negative.", 0);

            var moneyItem = BuildMoneyItem(canonical);
            var upsert = await _inventory.UpsertItemAsync(sessionId, Options.MoneyContainerId, moneyItem, ct);
            if (!upsert.Applied)
                return new EconomyMoneySyncResult(false, upsert.DenialReason ?? "Money object upsert failed.", 0);

            if (currency.Balance != canonical)
            {
                var set = await _currency.SetBalanceAsync(sessionId, canonical, ct);
                if (!set.Applied)
                    return new EconomyMoneySyncResult(false, set.DenialReason ?? "Currency sync failed.", 0);
            }

            var inventorySave = await _inventory.SaveForSessionAsync(sessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
            if (!inventorySave.Saved)
                return new EconomyMoneySyncResult(false, inventorySave.FailureReason ?? "Money object save failed.", canonical);

            Emit(
                ServerObservableEventType.EconomyTransferCompleted,
                ServerObservableSeverity.Information,
                sessionId,
                identityId,
                characterId,
                "Economy money object synchronized for session.",
                new Dictionary<string, object?>
                {
                    ["balance"] = canonical,
                    ["source"] = moneyFromInventory.HasValue ? "inventory_object" : "currency_fallback",
                });

            return new EconomyMoneySyncResult(true, null, canonical);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[economy] failed to ensure money object session_id={SessionId}", sessionId);
            Emit(
                ServerObservableEventType.EconomyTransferFailed,
                ServerObservableSeverity.Error,
                sessionId,
                identityId,
                characterId,
                "Economy money object synchronization failed.",
                new Dictionary<string, object?>
                {
                    ["exception"] = ex.Message,
                });
            return new EconomyMoneySyncResult(false, "Money object synchronization failed.", 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EconomyDirectTransferResult> TransferDirectAsync(
        EconomyDirectTransferRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
            return Denied("Amount must be greater than zero.");
        if (request.SenderSessionId == request.ReceiverSessionId)
            return Denied("Sender and receiver cannot be the same session.");
        if (!ValidateDistance(request.DistanceMeters))
            return Denied("Target is out of range.");

        await _gate.WaitAsync(ct);
        try
        {
            Emit(
                ServerObservableEventType.EconomyTransferStarted,
                ServerObservableSeverity.Information,
                request.SenderSessionId,
                request.SenderIdentityId,
                request.SenderCharacterId,
                "Economy transfer started.",
                new Dictionary<string, object?>
                {
                    ["receiver_identity_id"] = request.ReceiverIdentityId,
                    ["receiver_character_id"] = request.ReceiverCharacterId,
                    ["amount"] = request.Amount,
                    ["distance_meters"] = request.DistanceMeters,
                });

            var senderInventory = await _inventory.GetLoadedForSessionAsync(request.SenderSessionId, ct);
            if (senderInventory is null || !string.Equals(senderInventory.CharacterId, request.SenderCharacterId, StringComparison.Ordinal))
                return DeniedWithEvent(request, "Sender inventory is not available.");

            var receiverInventory = await _inventory.GetLoadedForSessionAsync(request.ReceiverSessionId, ct);
            if (receiverInventory is null || !string.Equals(receiverInventory.CharacterId, request.ReceiverCharacterId, StringComparison.Ordinal))
                return DeniedWithEvent(request, "Receiver inventory is not available.");

            var senderCurrency = await _currency.GetLoadedForSessionAsync(request.SenderSessionId, ct);
            var receiverCurrency = await _currency.GetLoadedForSessionAsync(request.ReceiverSessionId, ct);
            if (senderCurrency is null || !string.Equals(senderCurrency.CharacterId, request.SenderCharacterId, StringComparison.Ordinal))
                return DeniedWithEvent(request, "Sender currency context is not available.");
            if (receiverCurrency is null || !string.Equals(receiverCurrency.CharacterId, request.ReceiverCharacterId, StringComparison.Ordinal))
                return DeniedWithEvent(request, "Receiver currency context is not available.");

            var senderCurrent = TryReadMoneyAmount(senderInventory) ?? senderCurrency.Balance;
            var receiverCurrent = TryReadMoneyAmount(receiverInventory) ?? receiverCurrency.Balance;

            if (senderCurrent < request.Amount)
                return DeniedWithEvent(request, "Insufficient funds.");

            long senderNext;
            long receiverNext;
            try
            {
                senderNext = checked(senderCurrent - request.Amount);
                receiverNext = checked(receiverCurrent + request.Amount);
            }
            catch (OverflowException)
            {
                return DeniedWithEvent(request, "Transfer amount overflow.");
            }

            var senderUpsert = await _inventory.UpsertItemAsync(
                request.SenderSessionId,
                Options.MoneyContainerId,
                BuildMoneyItem(senderNext),
                ct);
            if (!senderUpsert.Applied)
                return FailedWithEvent(request, senderUpsert.DenialReason ?? "Sender money object update failed.");

            var receiverUpsert = await _inventory.UpsertItemAsync(
                request.ReceiverSessionId,
                Options.MoneyContainerId,
                BuildMoneyItem(receiverNext),
                ct);
            if (!receiverUpsert.Applied)
            {
                await _inventory.UpsertItemAsync(
                    request.SenderSessionId,
                    Options.MoneyContainerId,
                    BuildMoneyItem(senderCurrent),
                    ct);
                return FailedWithEvent(request, receiverUpsert.DenialReason ?? "Receiver money object update failed.");
            }

            var senderCurrencySet = await _currency.SetBalanceAsync(request.SenderSessionId, senderNext, ct);
            if (!senderCurrencySet.Applied)
                return FailedWithEvent(request, senderCurrencySet.DenialReason ?? "Sender currency sync failed.");

            var receiverCurrencySet = await _currency.SetBalanceAsync(request.ReceiverSessionId, receiverNext, ct);
            if (!receiverCurrencySet.Applied)
                return FailedWithEvent(request, receiverCurrencySet.DenialReason ?? "Receiver currency sync failed.");

            var senderSave = await _inventory.SaveForSessionAsync(request.SenderSessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
            if (!senderSave.Saved)
                return FailedWithEvent(request, senderSave.FailureReason ?? "Sender inventory save failed.");

            var receiverSave = await _inventory.SaveForSessionAsync(request.ReceiverSessionId, CharacterLifecycleSaveReason.DomainEvent, ct);
            if (!receiverSave.Saved)
                return FailedWithEvent(request, receiverSave.FailureReason ?? "Receiver inventory save failed.");

            Emit(
                ServerObservableEventType.EconomyTransferCompleted,
                ServerObservableSeverity.Information,
                request.SenderSessionId,
                request.SenderIdentityId,
                request.SenderCharacterId,
                "Economy transfer completed.",
                new Dictionary<string, object?>
                {
                    ["receiver_identity_id"] = request.ReceiverIdentityId,
                    ["receiver_character_id"] = request.ReceiverCharacterId,
                    ["amount"] = request.Amount,
                    ["sender_balance"] = senderNext,
                    ["receiver_balance"] = receiverNext,
                    ["result"] = "applied",
                });

            return new EconomyDirectTransferResult(true, null, senderNext, receiverNext);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[economy] transfer failed unexpectedly sender={Sender} receiver={Receiver}", request.SenderCharacterId, request.ReceiverCharacterId);
            return FailedWithEvent(request, "Transfer failed unexpectedly.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private InventoryItemRecord BuildMoneyItem(long amount)
    {
        return new InventoryItemRecord
        {
            InternalId = Options.MoneyItemInternalId,
            ItemType = Options.MoneyItemType,
            ItemRef = Options.MoneyItemRef,
            Quantity = 1,
            IsStackable = false,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Options.MoneyAmountMetadataKey] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    private long? TryReadMoneyAmount(CharacterInventoryRecord inventory)
    {
        var container = inventory.Containers.FirstOrDefault(
            x => string.Equals(x.ContainerId, Options.MoneyContainerId, StringComparison.Ordinal));
        if (container is null)
            return null;

        var item = container.Items.FirstOrDefault(
            x => string.Equals(x.InternalId, Options.MoneyItemInternalId, StringComparison.Ordinal)
                 || (string.Equals(x.ItemType, Options.MoneyItemType, StringComparison.Ordinal)
                     && string.Equals(x.ItemRef, Options.MoneyItemRef, StringComparison.Ordinal)));
        if (item is null)
            return null;

        if (!item.Metadata.TryGetValue(Options.MoneyAmountMetadataKey, out var raw))
            return 0;

        return long.TryParse(raw, out var amount) && amount >= 0 ? amount : 0;
    }

    private bool ValidateDistance(double distanceMeters)
        => !double.IsNaN(distanceMeters)
           && !double.IsInfinity(distanceMeters)
           && distanceMeters >= 0
           && distanceMeters <= Options.MaxTransferDistanceMeters;

    private EconomyDirectTransferResult Denied(string reason)
        => new(false, reason, 0, 0);

    private EconomyDirectTransferResult DeniedWithEvent(EconomyDirectTransferRequest request, string reason)
    {
        Emit(
            ServerObservableEventType.EconomyTransferDenied,
            ServerObservableSeverity.Warning,
            request.SenderSessionId,
            request.SenderIdentityId,
            request.SenderCharacterId,
            "Economy transfer denied.",
            new Dictionary<string, object?>
            {
                ["receiver_identity_id"] = request.ReceiverIdentityId,
                ["receiver_character_id"] = request.ReceiverCharacterId,
                ["amount"] = request.Amount,
                ["reason"] = reason,
            });
        return new EconomyDirectTransferResult(false, reason, 0, 0);
    }

    private EconomyDirectTransferResult FailedWithEvent(EconomyDirectTransferRequest request, string reason)
    {
        Emit(
            ServerObservableEventType.EconomyTransferFailed,
            ServerObservableSeverity.Error,
            request.SenderSessionId,
            request.SenderIdentityId,
            request.SenderCharacterId,
            "Economy transfer execution failed.",
            new Dictionary<string, object?>
            {
                ["receiver_identity_id"] = request.ReceiverIdentityId,
                ["receiver_character_id"] = request.ReceiverCharacterId,
                ["amount"] = request.Amount,
                ["reason"] = reason,
            });
        return new EconomyDirectTransferResult(false, reason, 0, 0);
    }

    private void Emit(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        Guid? sessionId,
        string? identityId,
        string? characterId,
        string message,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        _observability.Emit(new ServerObservableEvent(
            type,
            ServerObservableComponent.Persistence,
            severity,
            DateTimeOffset.UtcNow,
            sessionId,
            identityId,
            characterId,
            message,
            payload));
    }
}
