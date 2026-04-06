using System.Collections.Concurrent;
using KcdMp.Server.AccessControl;
using KcdMp.Server.Identity;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class ServerAccessControlServiceTests
{
    [Fact]
    public async Task GetActiveConfigurationAsync_WhenMissing_PersistsDefaultOpenMode()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new ServerAccessControlService(store, sink);

            var config = await service.GetActiveConfigurationAsync();

            Assert.Equal(ServerAccessMode.Open, config.AccessMode);
            var persisted = await store.LoadAsync<ServerAccessControlConfigurationRecord>(
                JsonPersistenceDomains.Config,
                "access_control_v1");
            Assert.NotNull(persisted);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.AccessControlConfigLoaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateIdentityAccessAsync_OpenMode_AllowsPendingIdentity()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new ServerAccessControlService(
                store,
                sink,
                new ServerAccessControlOptions { DefaultAccessMode = ServerAccessMode.Open });
            var sessionId = Guid.NewGuid();

            var result = await service.EvaluateIdentityAccessAsync(
                new ServerAccessIdentityEvaluationRequest(
                    sessionId,
                    "pid_henry",
                    PlayerIdentityStatus.Pending,
                    IsDeactivated: false,
                    IdentityCreatedInThisAttempt: false));

            Assert.True(result.IsAllowed);
            Assert.Equal(ServerAccessDecisionReason.AllowedPendingIdentityInOpenMode, result.Reason);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.AccessDecisionAllowed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateIdentityAccessAsync_WhitelistMode_DeniesPendingIdentity()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            await store.SaveAsync(
                JsonPersistenceDomains.Config,
                "access_control_v1",
                new ServerAccessControlConfigurationRecord
                {
                    ConfigVersion = "access_control_v1",
                    AccessMode = ServerAccessMode.Whitelist,
                    DenyPendingIdentityInWhitelistMode = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
            var service = new ServerAccessControlService(store, sink);

            var result = await service.EvaluateIdentityAccessAsync(
                new ServerAccessIdentityEvaluationRequest(
                    Guid.NewGuid(),
                    "pid_henry",
                    PlayerIdentityStatus.Pending,
                    IsDeactivated: false,
                    IdentityCreatedInThisAttempt: false));

            Assert.False(result.IsAllowed);
            Assert.Equal(ServerAccessDecisionReason.DeniedPendingIdentityInWhitelistMode, result.Reason);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.AccessDecisionDenied);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateIdentityAccessAsync_DeniesBlockedIdentity()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new ServerAccessControlService(store, sink);

            var result = await service.EvaluateIdentityAccessAsync(
                new ServerAccessIdentityEvaluationRequest(
                    Guid.NewGuid(),
                    "pid_henry",
                    PlayerIdentityStatus.Blocked,
                    IsDeactivated: false,
                    IdentityCreatedInThisAttempt: false));

            Assert.False(result.IsAllowed);
            Assert.Equal(ServerAccessDecisionReason.DeniedBlockedIdentity, result.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EmitDuplicateActiveSessionDenied_EmitsDeniedEvent()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new ServerAccessControlService(store, sink);

            service.EmitDuplicateActiveSessionDenied(Guid.NewGuid(), "pid_henry");

            Assert.Contains(
                sink.Events,
                evt => evt.Type == ServerObservableEventType.AccessDecisionDenied
                       && evt.Payload is not null
                       && evt.Payload.TryGetValue("reason", out var reason)
                       && string.Equals(reason?.ToString(), ServerAccessDecisionReason.DeniedDuplicateActiveSession.ToString(), StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_access_{Guid.NewGuid():N}");
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
