using System.Collections.Concurrent;
using KcdMp.Server.Identity;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class PlayerIdentityServiceTests
{
    [Fact]
    public async Task ResolveOrCreate_CreatesIdentityAndReusesItByFallbackName()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var service = new PlayerIdentityService(store, new InMemoryObservabilitySink());

            var first = await service.ResolveOrCreateAsync(new PlayerIdentityClaim("Henry", null, null));
            var second = await service.ResolveOrCreateAsync(new PlayerIdentityClaim("henry", null, null));

            Assert.True(first.IsAllowed);
            Assert.True(first.Created);
            Assert.NotNull(first.Identity);
            Assert.True(second.IsAllowed);
            Assert.False(second.Created);
            Assert.NotNull(second.Identity);
            Assert.Equal(first.Identity!.InternalId, second.Identity!.InternalId);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, JsonPersistenceDomains.Identity), "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveOrCreate_WhenWhitelistRequired_PendingIdentityIsDenied()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var service = new PlayerIdentityService(
                store,
                new InMemoryObservabilitySink(),
                new PlayerIdentityOptions { RequireWhitelistForPendingIdentity = true });

            var result = await service.ResolveOrCreateAsync(new PlayerIdentityClaim("Theresa", "token_1", null));

            Assert.False(result.IsAllowed);
            Assert.NotNull(result.Identity);
            Assert.Equal(PlayerIdentityStatus.Pending, result.Identity!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SetStatus_Blocked_DeniesFutureAccess()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var service = new PlayerIdentityService(store, new InMemoryObservabilitySink());

            var created = await service.ResolveOrCreateAsync(new PlayerIdentityClaim("Radzig", "token-radzig", null));
            Assert.True(created.IsAllowed);
            Assert.NotNull(created.Identity);

            var updated = await service.TrySetStatusAsync(created.Identity!.InternalId, PlayerIdentityStatus.Blocked);
            var denied = await service.ResolveOrCreateAsync(new PlayerIdentityClaim("Radzig", "token-radzig", null));

            Assert.True(updated);
            Assert.False(denied.IsAllowed);
            Assert.NotNull(denied.Identity);
            Assert.Equal(PlayerIdentityStatus.Blocked, denied.Identity!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_identity_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class InMemoryObservabilitySink : IServerObservabilitySink
    {
        private readonly ConcurrentQueue<ServerObservableEvent> _events = new();

        public void Emit(ServerObservableEvent observableEvent)
        {
            _events.Enqueue(observableEvent);
        }
    }
}
