using System.Collections.Concurrent;
using KcdMp.Server.Bans;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class IdentityBanServiceTests
{
    [Fact]
    public async Task ApplyBanAsync_Permanent_DeniesAccess()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new IdentityBanService(store, sink);
            var identityId = "pid_henry";

            var apply = await service.ApplyBanAsync(new IdentityBanApplyRequest(
                IdentityId: identityId,
                Type: IdentityBanType.Permanent,
                Duration: null,
                Reason: "Repeated griefing",
                ActorId: "admin_1",
                Summary: "Banned by moderation team."));

            Assert.True(apply.Applied);
            Assert.NotNull(apply.Ban);
            Assert.Equal(IdentityBanStatus.Active, apply.Ban!.Status);

            var eval = await service.EvaluateAccessAsync(Guid.NewGuid(), identityId);
            Assert.True(eval.IsDenied);
            Assert.Equal("Banned by moderation team.", eval.DenialReason);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.BanApplied);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.BanAccessDenied);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyBanAsync_WhenAlreadyActive_RejectsSecondBan()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var service = new IdentityBanService(store, new InMemoryObservabilitySink());
            var identityId = "pid_hans";

            var first = await service.ApplyBanAsync(new IdentityBanApplyRequest(
                identityId,
                IdentityBanType.Permanent,
                null,
                "Cheating",
                "admin_1"));
            var second = await service.ApplyBanAsync(new IdentityBanApplyRequest(
                identityId,
                IdentityBanType.Temporary,
                TimeSpan.FromMinutes(10),
                "Spam",
                "admin_2"));

            Assert.True(first.Applied);
            Assert.False(second.Applied);
            Assert.Equal("Identity already has an active ban.", second.DenialReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateAccessAsync_TemporaryBanExpiresAutomatically()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new IdentityBanService(store, sink);
            var identityId = "pid_theresa";

            var apply = await service.ApplyBanAsync(new IdentityBanApplyRequest(
                identityId,
                IdentityBanType.Temporary,
                TimeSpan.FromMilliseconds(50),
                "Cooldown",
                "admin_1"));
            Assert.True(apply.Applied);

            await Task.Delay(120);
            var eval = await service.EvaluateAccessAsync(Guid.NewGuid(), identityId);
            Assert.False(eval.IsDenied);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.BanExpired);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RevokeActiveBanAsync_RevokesAndAllowsAccess()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var service = new IdentityBanService(store, sink);
            var identityId = "pid_radzig";

            var apply = await service.ApplyBanAsync(new IdentityBanApplyRequest(
                identityId,
                IdentityBanType.Permanent,
                null,
                "Abuse",
                "admin_1"));
            Assert.True(apply.Applied);

            var revoke = await service.RevokeActiveBanAsync(new IdentityBanRevocationRequest(
                identityId,
                "admin_2",
                "Appeal accepted"));
            Assert.True(revoke.Revoked);
            Assert.NotNull(revoke.Ban);
            Assert.Equal(IdentityBanStatus.Revoked, revoke.Ban!.Status);

            var eval = await service.EvaluateAccessAsync(Guid.NewGuid(), identityId);
            Assert.False(eval.IsDenied);
            Assert.Contains(sink.Events, evt => evt.Type == ServerObservableEventType.BanRevoked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_bans_{Guid.NewGuid():N}");
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
