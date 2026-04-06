using KcdMp.Server.Characters;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;
using Serilog;

namespace KcdMp.Server.Inventory;

public sealed class CharacterInventoryService : ICharacterInventoryService
{
    private readonly IJsonPersistenceStore _store;
    private readonly IServerObservabilitySink _observability;
    private readonly CharacterInventoryOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, LoadedInventoryContext> _loadedBySession = [];
    private readonly Dictionary<string, Guid> _characterToSession = new(StringComparer.Ordinal);

    public CharacterInventoryService(
        IJsonPersistenceStore store,
        IServerObservabilitySink observability,
        CharacterInventoryOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _options = options ?? new CharacterInventoryOptions();
        _logger = logger ?? Log.Logger;
    }

    public async Task<CharacterInventoryLoadResult> LoadForSessionAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        Emit(
            ServerObservableEventType.InventoryLoadStarted,
            ServerObservableSeverity.Information,
            "Inventory load started.",
            sessionId,
            identityId,
            characterId);

        await _gate.WaitAsync(ct);
        try
        {
            if (_characterToSession.TryGetValue(characterId, out var ownerSessionId) && ownerSessionId != sessionId)
            {
                return new CharacterInventoryLoadResult(
                    false,
                    "Inventory already active in another session.",
                    null,
                    DroppedContainerCount: 0,
                    DroppedItemCount: 0);
            }

            if (_loadedBySession.TryGetValue(sessionId, out var existing))
            {
                if (!string.Equals(existing.CharacterId, characterId, StringComparison.Ordinal))
                {
                    return new CharacterInventoryLoadResult(
                        false,
                        "Session already has a different loaded inventory.",
                        null,
                        DroppedContainerCount: 0,
                        DroppedItemCount: 0);
                }

                return new CharacterInventoryLoadResult(
                    true,
                    null,
                    CloneInventory(existing.Inventory),
                    DroppedContainerCount: 0,
                    DroppedItemCount: 0);
            }
        }
        finally
        {
            _gate.Release();
        }

        CharacterInventoryRecord inventory;
        var loadedExisting = true;
        try
        {
            var loaded = await _store.LoadAsync<CharacterInventoryRecord>(
                JsonPersistenceDomains.Inventory,
                characterId,
                validate: null,
                ct);
            if (loaded is null)
            {
                loadedExisting = false;
                inventory = CreateNewInventory(characterId);
            }
            else
            {
                inventory = CloneInventory(loaded);
            }
        }
        catch (Exception ex)
        {
            Emit(
                ServerObservableEventType.InventoryLoadFailed,
                ServerObservableSeverity.Error,
                "Inventory load failed.",
                sessionId,
                identityId,
                characterId,
                payload: new Dictionary<string, object?>
                {
                    ["exception"] = ex.Message,
                });
            return new CharacterInventoryLoadResult(false, "Inventory could not be loaded.", null, 0, 0);
        }

        var normalization = NormalizeInventory(inventory, characterId);
        var normalized = normalization.DroppedContainerCount > 0
                         || normalization.DroppedItemCount > 0
                         || normalization.Modified;

        if (normalization.DroppedContainerCount > 0 || normalization.DroppedItemCount > 0)
        {
            Emit(
                ServerObservableEventType.InventoryValidationFailed,
                ServerObservableSeverity.Warning,
                "Inventory partially loaded with invalid records removed.",
                sessionId,
                identityId,
                characterId,
                payload: new Dictionary<string, object?>
                {
                    ["dropped_container_count"] = normalization.DroppedContainerCount,
                    ["dropped_item_count"] = normalization.DroppedItemCount,
                });
        }

        if (normalized)
        {
            Emit(
                ServerObservableEventType.InventoryChanged,
                ServerObservableSeverity.Information,
                "Inventory normalized during load.",
                sessionId,
                identityId,
                characterId,
                payload: new Dictionary<string, object?>
                {
                    ["loaded_existing"] = loadedExisting,
                    ["dropped_container_count"] = normalization.DroppedContainerCount,
                    ["dropped_item_count"] = normalization.DroppedItemCount,
                });
        }

        if (!loadedExisting || normalized)
        {
            var saveResult = await SaveWithRetryAsync(
                sessionId,
                identityId,
                inventory,
                CharacterLifecycleSaveReason.Activation,
                ct);
            if (!saveResult.Saved)
            {
                return new CharacterInventoryLoadResult(
                    false,
                    "Inventory activation save failed.",
                    null,
                    normalization.DroppedContainerCount,
                    normalization.DroppedItemCount);
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_characterToSession.TryGetValue(characterId, out var ownerSessionId) && ownerSessionId != sessionId)
            {
                return new CharacterInventoryLoadResult(
                    false,
                    "Inventory already active in another session.",
                    null,
                    normalization.DroppedContainerCount,
                    normalization.DroppedItemCount);
            }

            _loadedBySession[sessionId] = new LoadedInventoryContext(
                sessionId,
                identityId,
                characterId,
                CloneInventory(inventory),
                isDirty: false);
            _characterToSession[characterId] = sessionId;
        }
        finally
        {
            _gate.Release();
        }

        Emit(
            ServerObservableEventType.InventoryLoadCompleted,
            ServerObservableSeverity.Information,
            "Inventory load completed.",
            sessionId,
            identityId,
            characterId,
            payload: new Dictionary<string, object?>
            {
                ["loaded_existing"] = loadedExisting,
                ["container_count"] = inventory.Containers.Count,
                ["dropped_container_count"] = normalization.DroppedContainerCount,
                ["dropped_item_count"] = normalization.DroppedItemCount,
            });

        return new CharacterInventoryLoadResult(
            true,
            null,
            CloneInventory(inventory),
            normalization.DroppedContainerCount,
            normalization.DroppedItemCount);
    }

    public async Task<CharacterInventoryRecord?> GetLoadedForSessionAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out var loaded))
                return null;
            return CloneInventory(loaded.Inventory);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CharacterInventoryMutationResult> UpsertItemAsync(
        Guid sessionId,
        string containerId,
        InventoryItemRecord item,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(item);

        if (!ValidateItem(item))
        {
            Emit(
                ServerObservableEventType.InventoryValidationFailed,
                ServerObservableSeverity.Warning,
                "Inventory mutation rejected due to invalid item payload.",
                sessionId,
                payload: new Dictionary<string, object?>
                {
                    ["container_id"] = containerId,
                    ["item_id"] = item.InternalId,
                });
            return new CharacterInventoryMutationResult(false, "Invalid item.", null);
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out var loaded))
                return new CharacterInventoryMutationResult(false, "Inventory not loaded for this session.", null);

            var now = DateTimeOffset.UtcNow;
            var container = loaded.Inventory.Containers.FirstOrDefault(
                x => string.Equals(x.ContainerId, containerId, StringComparison.Ordinal));
            if (container is null)
            {
                container = new InventoryContainerRecord
                {
                    ContainerId = containerId,
                    ContainerType = InventoryContainerType.Storage,
                    DisplayName = containerId,
                    UpdatedAtUtc = now,
                };
                loaded.Inventory.Containers.Add(container);
            }

            var existingIndex = container.Items.FindIndex(x => string.Equals(x.InternalId, item.InternalId, StringComparison.Ordinal));
            var clonedItem = CloneItem(item);
            if (existingIndex >= 0)
            {
                container.Items[existingIndex] = clonedItem;
            }
            else
            {
                container.Items.Add(clonedItem);
            }

            container.UpdatedAtUtc = now;
            loaded.Inventory.UpdatedAtUtc = now;
            loaded.IsDirty = true;

            Emit(
                ServerObservableEventType.InventoryChanged,
                ServerObservableSeverity.Information,
                "Inventory item upserted.",
                sessionId,
                loaded.IdentityId,
                loaded.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["container_id"] = containerId,
                    ["item_id"] = item.InternalId,
                    ["is_new_container"] = existingIndex < 0 && container.Items.Count == 1,
                });

            return new CharacterInventoryMutationResult(true, null, CloneInventory(loaded.Inventory));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CharacterInventoryMutationResult> RemoveItemAsync(
        Guid sessionId,
        string containerId,
        string itemInternalId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemInternalId);

        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out var loaded))
                return new CharacterInventoryMutationResult(false, "Inventory not loaded for this session.", null);

            var container = loaded.Inventory.Containers.FirstOrDefault(
                x => string.Equals(x.ContainerId, containerId, StringComparison.Ordinal));
            if (container is null)
                return new CharacterInventoryMutationResult(false, "Container does not exist.", null);

            var removed = container.Items.RemoveAll(x => string.Equals(x.InternalId, itemInternalId, StringComparison.Ordinal));
            if (removed <= 0)
                return new CharacterInventoryMutationResult(false, "Item does not exist.", null);

            var now = DateTimeOffset.UtcNow;
            container.UpdatedAtUtc = now;
            loaded.Inventory.UpdatedAtUtc = now;
            loaded.IsDirty = true;

            Emit(
                ServerObservableEventType.InventoryChanged,
                ServerObservableSeverity.Information,
                "Inventory item removed.",
                sessionId,
                loaded.IdentityId,
                loaded.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["container_id"] = containerId,
                    ["item_id"] = itemInternalId,
                });

            return new CharacterInventoryMutationResult(true, null, CloneInventory(loaded.Inventory));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CharacterInventorySaveResult> SaveForSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        LoadedInventoryContext? loaded;
        await _gate.WaitAsync(ct);
        try
        {
            _loadedBySession.TryGetValue(sessionId, out loaded);
            if (loaded is null)
                return new CharacterInventorySaveResult(true, null, 0);
        }
        finally
        {
            _gate.Release();
        }

        var result = await SaveWithRetryAsync(sessionId, loaded.IdentityId, loaded.Inventory, reason, ct);
        if (result.Saved)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_loadedBySession.TryGetValue(sessionId, out var current))
                    current.IsDirty = false;
            }
            finally
            {
                _gate.Release();
            }
        }

        return result;
    }

    public async Task<CharacterInventorySaveResult> SaveAndUnloadSessionAsync(
        Guid sessionId,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        LoadedInventoryContext? loaded;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loadedBySession.TryGetValue(sessionId, out loaded))
                return new CharacterInventorySaveResult(true, null, 0);

            _loadedBySession.Remove(sessionId);
            _characterToSession.Remove(loaded.CharacterId);
        }
        finally
        {
            _gate.Release();
        }

        return await SaveWithRetryAsync(sessionId, loaded.IdentityId, loaded.Inventory, reason, ct);
    }

    public async Task<int> SaveAndUnloadAllAsync(
        CharacterLifecycleSaveReason reason,
        CancellationToken ct = default)
    {
        List<LoadedInventoryContext> loaded;
        await _gate.WaitAsync(ct);
        try
        {
            loaded = _loadedBySession.Values.ToList();
            _loadedBySession.Clear();
            _characterToSession.Clear();
        }
        finally
        {
            _gate.Release();
        }

        var savedCount = 0;
        foreach (var entry in loaded)
        {
            ct.ThrowIfCancellationRequested();
            var save = await SaveWithRetryAsync(entry.SessionId, entry.IdentityId, entry.Inventory, reason, ct);
            if (save.Saved)
                savedCount++;
        }

        return savedCount;
    }

    private async Task<CharacterInventorySaveResult> SaveWithRetryAsync(
        Guid sessionId,
        string identityId,
        CharacterInventoryRecord inventory,
        CharacterLifecycleSaveReason reason,
        CancellationToken ct)
    {
        var attempts = 0;
        Exception? lastError = null;
        var maxAttempts = Math.Max(1, _options.SaveRetryCount + 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            attempts = attempt;
            var now = DateTimeOffset.UtcNow;
            inventory.UpdatedAtUtc = now;
            inventory.LastSavedAtUtc = now;

            Emit(
                ServerObservableEventType.InventorySaveStarted,
                ServerObservableSeverity.Information,
                "Inventory save started.",
                sessionId,
                identityId,
                inventory.CharacterId,
                payload: new Dictionary<string, object?>
                {
                    ["reason"] = reason.ToString(),
                    ["attempt"] = attempt,
                    ["max_attempts"] = maxAttempts,
                });

            try
            {
                await _store.SaveAsync(JsonPersistenceDomains.Inventory, inventory.CharacterId, inventory, ct);
                Emit(
                    ServerObservableEventType.InventorySaveCompleted,
                    ServerObservableSeverity.Information,
                    "Inventory save completed.",
                    sessionId,
                    identityId,
                    inventory.CharacterId,
                    payload: new Dictionary<string, object?>
                    {
                        ["reason"] = reason.ToString(),
                        ["attempt"] = attempt,
                    });
                return new CharacterInventorySaveResult(true, null, attempts);
            }
            catch (Exception ex)
            {
                lastError = ex;
                Emit(
                    ServerObservableEventType.InventorySaveFailed,
                    ServerObservableSeverity.Error,
                    "Inventory save failed.",
                    sessionId,
                    identityId,
                    inventory.CharacterId,
                    payload: new Dictionary<string, object?>
                    {
                        ["reason"] = reason.ToString(),
                        ["attempt"] = attempt,
                        ["max_attempts"] = maxAttempts,
                        ["exception"] = ex.Message,
                    });

                if (attempt < maxAttempts && _options.SaveRetryDelay > TimeSpan.Zero)
                    await Task.Delay(_options.SaveRetryDelay, ct);
            }
        }

        _logger.Warning(
            "[inventory] persistent save incident character_id={CharacterId} session_id={SessionId} reason={Reason} attempts={Attempts}",
            inventory.CharacterId,
            sessionId,
            reason,
            attempts);
        return new CharacterInventorySaveResult(false, lastError?.Message, attempts);
    }

    private CharacterInventoryRecord CreateNewInventory(string characterId)
    {
        var now = DateTimeOffset.UtcNow;
        return new CharacterInventoryRecord
        {
            CharacterId = characterId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastSavedAtUtc = DateTimeOffset.MinValue,
            Containers =
            [
                new InventoryContainerRecord
                {
                    ContainerId = _options.BaseContainerId,
                    ContainerType = InventoryContainerType.Primary,
                    DisplayName = _options.BaseContainerDisplayName,
                    UpdatedAtUtc = now,
                },
            ],
        };
    }

    private InventoryNormalizationResult NormalizeInventory(CharacterInventoryRecord inventory, string characterId)
    {
        var droppedContainers = 0;
        var droppedItems = 0;
        var modified = false;
        var now = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(inventory.CharacterId))
        {
            inventory.CharacterId = characterId;
            modified = true;
        }
        else if (!string.Equals(inventory.CharacterId, characterId, StringComparison.Ordinal))
        {
            throw new PersistenceValidationException("Inventory record has a mismatched character_id.");
        }

        var normalizedContainers = new List<InventoryContainerRecord>();
        var containerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in inventory.Containers)
        {
            if (container is null || string.IsNullOrWhiteSpace(container.ContainerId))
            {
                droppedContainers++;
                modified = true;
                continue;
            }

            if (!containerIds.Add(container.ContainerId))
            {
                droppedContainers++;
                modified = true;
                continue;
            }

            var normalized = CloneContainer(container);
            if (string.IsNullOrWhiteSpace(normalized.DisplayName))
            {
                normalized.DisplayName = normalized.ContainerId;
                modified = true;
            }

            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            var validItems = new List<InventoryItemRecord>();
            foreach (var item in normalized.Items)
            {
                if (item is null || !ValidateItem(item))
                {
                    droppedItems++;
                    modified = true;
                    continue;
                }

                if (!itemIds.Add(item.InternalId))
                {
                    droppedItems++;
                    modified = true;
                    continue;
                }

                validItems.Add(CloneItem(item));
            }

            normalized.Items = validItems;
            normalized.UpdatedAtUtc = now;
            normalizedContainers.Add(normalized);
        }

        inventory.Containers = normalizedContainers;

        if (!inventory.Containers.Any(x => string.Equals(x.ContainerId, _options.BaseContainerId, StringComparison.Ordinal)))
        {
            inventory.Containers.Insert(0, new InventoryContainerRecord
            {
                ContainerId = _options.BaseContainerId,
                ContainerType = InventoryContainerType.Primary,
                DisplayName = _options.BaseContainerDisplayName,
                UpdatedAtUtc = now,
            });
            modified = true;
        }

        if (inventory.CreatedAtUtc == default)
        {
            inventory.CreatedAtUtc = now;
            modified = true;
        }

        inventory.UpdatedAtUtc = now;
        return new InventoryNormalizationResult(
            DroppedContainerCount: droppedContainers,
            DroppedItemCount: droppedItems,
            Modified: modified);
    }

    private static bool ValidateItem(InventoryItemRecord item)
    {
        if (item is null)
            return false;
        if (string.IsNullOrWhiteSpace(item.InternalId))
            return false;
        if (string.IsNullOrWhiteSpace(item.ItemType))
            return false;
        if (string.IsNullOrWhiteSpace(item.ItemRef))
            return false;
        if (item.Quantity <= 0)
            return false;
        if (!item.IsStackable && item.Quantity != 1)
            return false;
        return true;
    }

    private static CharacterInventoryRecord CloneInventory(CharacterInventoryRecord source)
    {
        return new CharacterInventoryRecord
        {
            CharacterId = source.CharacterId,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            LastSavedAtUtc = source.LastSavedAtUtc,
            Containers = source.Containers.Select(CloneContainer).ToList(),
        };
    }

    private static InventoryContainerRecord CloneContainer(InventoryContainerRecord source)
    {
        return new InventoryContainerRecord
        {
            ContainerId = source.ContainerId,
            ContainerType = source.ContainerType,
            DisplayName = source.DisplayName,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
            Items = source.Items.Select(CloneItem).ToList(),
            UpdatedAtUtc = source.UpdatedAtUtc,
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

    private sealed class LoadedInventoryContext
    {
        public LoadedInventoryContext(
            Guid sessionId,
            string identityId,
            string characterId,
            CharacterInventoryRecord inventory,
            bool isDirty)
        {
            SessionId = sessionId;
            IdentityId = identityId;
            CharacterId = characterId;
            Inventory = inventory;
            IsDirty = isDirty;
        }

        public Guid SessionId { get; }
        public string IdentityId { get; }
        public string CharacterId { get; }
        public CharacterInventoryRecord Inventory { get; }
        public bool IsDirty { get; set; }
    }

    private sealed record InventoryNormalizationResult(
        int DroppedContainerCount,
        int DroppedItemCount,
        bool Modified);
}
