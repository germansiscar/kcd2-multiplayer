using System.Collections.Concurrent;
using KcdMp.Server.Characters;
using KcdMp.Server.Currency;
using KcdMp.Server.Economy;
using KcdMp.Server.Inventory;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class EconomyAppliedServiceTests
{
    [Fact]
    public async Task TransferDirectAsync_WithValidRequest_TransfersAndPersistsMoneyObject()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var inventory = new CharacterInventoryService(store, sink);
            var currency = new CharacterCurrencyService(store, sink, new CharacterCurrencyOptions { InitialBalance = 100 });
            var economy = new EconomyAppliedService(inventory, currency, sink);

            var senderSession = Guid.NewGuid();
            var receiverSession = Guid.NewGuid();

            await inventory.LoadForSessionAsync(senderSession, "pid_sender", "cid_sender");
            await inventory.LoadForSessionAsync(receiverSession, "pid_receiver", "cid_receiver");
            await currency.LoadForSessionAsync(senderSession, "pid_sender", "cid_sender");
            await currency.LoadForSessionAsync(receiverSession, "pid_receiver", "cid_receiver");
            await currency.SetBalanceAsync(receiverSession, 25);

            var senderSync = await economy.EnsureMoneyObjectForSessionAsync(senderSession, "pid_sender", "cid_sender");
            var receiverSync = await economy.EnsureMoneyObjectForSessionAsync(receiverSession, "pid_receiver", "cid_receiver");
            Assert.True(senderSync.Applied);
            Assert.True(receiverSync.Applied);

            var result = await economy.TransferDirectAsync(new EconomyDirectTransferRequest(
                SenderSessionId: senderSession,
                ReceiverSessionId: receiverSession,
                SenderIdentityId: "pid_sender",
                SenderCharacterId: "cid_sender",
                ReceiverIdentityId: "pid_receiver",
                ReceiverCharacterId: "cid_receiver",
                Amount: 30,
                DistanceMeters: 2.2));

            Assert.True(result.Applied);
            Assert.Equal(70, result.SenderBalance);
            Assert.Equal(55, result.ReceiverBalance);

            var senderPersisted = await store.LoadAsync<CharacterInventoryRecord>(JsonPersistenceDomains.Inventory, "cid_sender");
            var receiverPersisted = await store.LoadAsync<CharacterInventoryRecord>(JsonPersistenceDomains.Inventory, "cid_receiver");
            Assert.Equal(70, ReadMoneyAmount(senderPersisted!, economy.Options));
            Assert.Equal(55, ReadMoneyAmount(receiverPersisted!, economy.Options));

            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.EconomyTransferCompleted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransferDirectAsync_WithInsufficientFunds_IsDeniedWithoutMutation()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var inventory = new CharacterInventoryService(store, sink);
            var currency = new CharacterCurrencyService(store, sink, new CharacterCurrencyOptions { InitialBalance = 10 });
            var economy = new EconomyAppliedService(inventory, currency, sink);

            var senderSession = Guid.NewGuid();
            var receiverSession = Guid.NewGuid();

            await inventory.LoadForSessionAsync(senderSession, "pid_sender", "cid_sender");
            await inventory.LoadForSessionAsync(receiverSession, "pid_receiver", "cid_receiver");
            await currency.LoadForSessionAsync(senderSession, "pid_sender", "cid_sender");
            await currency.LoadForSessionAsync(receiverSession, "pid_receiver", "cid_receiver");
            await economy.EnsureMoneyObjectForSessionAsync(senderSession, "pid_sender", "cid_sender");
            await economy.EnsureMoneyObjectForSessionAsync(receiverSession, "pid_receiver", "cid_receiver");

            var denied = await economy.TransferDirectAsync(new EconomyDirectTransferRequest(
                SenderSessionId: senderSession,
                ReceiverSessionId: receiverSession,
                SenderIdentityId: "pid_sender",
                SenderCharacterId: "cid_sender",
                ReceiverIdentityId: "pid_receiver",
                ReceiverCharacterId: "cid_receiver",
                Amount: 50,
                DistanceMeters: 1.5));

            Assert.False(denied.Applied);
            Assert.Contains("Insufficient", denied.DenialReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var sender = await currency.GetLoadedForSessionAsync(senderSession);
            var receiver = await currency.GetLoadedForSessionAsync(receiverSession);
            Assert.Equal(10, sender!.Balance);
            Assert.Equal(10, receiver!.Balance);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.EconomyTransferDenied);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static long ReadMoneyAmount(CharacterInventoryRecord inventory, EconomyAppliedOptions options)
    {
        var container = inventory.Containers.First(x => string.Equals(x.ContainerId, options.MoneyContainerId, StringComparison.Ordinal));
        var item = container.Items.First(x => string.Equals(x.InternalId, options.MoneyItemInternalId, StringComparison.Ordinal));
        return long.Parse(item.Metadata[options.MoneyAmountMetadataKey]);
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_economy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class InMemoryObservabilitySink : IServerObservabilitySink
    {
        private readonly ConcurrentQueue<ServerObservableEvent> _events = new();
        public IReadOnlyCollection<ServerObservableEvent> Events => _events.ToArray();

        public void Emit(ServerObservableEvent observableEvent)
        {
            _events.Enqueue(observableEvent);
        }
    }
}
