using System.Collections.Concurrent;
using KcdMp.Server.Characters;
using KcdMp.Server.Identity;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class CharacterProfileServiceTests
{
    [Fact]
    public async Task CreateAsync_CreatesCharacter_AndLinksItToIdentity()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var identities = new PlayerIdentityService(store, sink);
            var characters = new CharacterProfileService(store, identities, sink);

            var identity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Henry", "token_henry", null));
            var created = await characters.CreateAsync(
                identity.Identity!.InternalId,
                new CharacterCreateRequest("Henry", "Skalitz", "male_knight"));

            Assert.True(created.Created);
            Assert.NotNull(created.Character);
            Assert.Equal("Henry Skalitz", created.Character!.FullName);
            Assert.Equal(CharacterProfileStatus.Inactive, created.Character.Status);
            Assert.False(created.Character.IsDeleted);
            Assert.Equal(identity.Identity.InternalId, created.Character.IdentityId);

            var linkedIdentity = await identities.GetByInternalIdAsync(identity.Identity.InternalId);
            Assert.NotNull(linkedIdentity);
            Assert.Contains(created.Character.InternalId, linkedIdentity!.CharacterIds, StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_DeniesDuplicateGlobalName()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var identities = new PlayerIdentityService(store, sink);
            var characters = new CharacterProfileService(store, identities, sink);

            var a = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("A", "token_a", null));
            var b = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("B", "token_b", null));

            var first = await characters.CreateAsync(
                a.Identity!.InternalId,
                new CharacterCreateRequest("Hans", "Capon", "male_noble"));
            var second = await characters.CreateAsync(
                b.Identity!.InternalId,
                new CharacterCreateRequest("hans", "  CAPON ", "male_peasant"));

            Assert.True(first.Created);
            Assert.False(second.Created);
            Assert.Equal("Character name is already in use.", second.DenialReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_DeniesWhenIdentityDoesNotExist()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var identities = new PlayerIdentityService(store, sink);
            var characters = new CharacterProfileService(store, identities, sink);

            var result = await characters.CreateAsync(
                "pid_missing",
                new CharacterCreateRequest("Theresa", "Rattay", "female_commoner"));

            Assert.False(result.Created);
            Assert.Null(result.Character);
            Assert.Equal("Identity not available for character creation.", result.DenialReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TryDisableAndDelete_RequireExpectedPolicy()
    {
        var root = CreateTempDir();
        try
        {
            var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
            var sink = new InMemoryObservabilitySink();
            var identities = new PlayerIdentityService(store, sink);
            var characters = new CharacterProfileService(store, identities, sink);

            var identity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Robard", "token_robard", null));
            var created = await characters.CreateAsync(
                identity.Identity!.InternalId,
                new CharacterCreateRequest("Robard", "Captain", "male_guard"));
            Assert.True(created.Created);
            var characterId = created.Character!.InternalId;

            var disabled = await characters.TryDisableAsync(characterId);
            Assert.True(disabled);
            var disabledRecord = await characters.GetByInternalIdAsync(characterId);
            Assert.NotNull(disabledRecord);
            Assert.Equal(CharacterProfileStatus.Disabled, disabledRecord!.Status);

            var nonAdminDelete = await characters.TryDeleteAsync(characterId, isAdminOperation: false);
            Assert.False(nonAdminDelete);

            var adminDelete = await characters.TryDeleteAsync(characterId, isAdminOperation: true);
            Assert.True(adminDelete);
            var deletedRecord = await characters.GetByInternalIdAsync(characterId);
            Assert.NotNull(deletedRecord);
            Assert.True(deletedRecord!.IsDeleted);
            Assert.Equal(CharacterProfileStatus.Disabled, deletedRecord.Status);

            var linkedIdentity = await identities.GetByInternalIdAsync(identity.Identity.InternalId);
            Assert.NotNull(linkedIdentity);
            Assert.DoesNotContain(characterId, linkedIdentity!.CharacterIds, StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_character_{Guid.NewGuid():N}");
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
