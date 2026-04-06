using System.Collections.Concurrent;
using KcdMp.Server.Characters;
using KcdMp.Server.Identity;
using KcdMp.Server.Observability;
using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Server;

public sealed class CharacterSessionBindingServiceTests
{
    [Fact]
    public async Task BindAsync_WithPreferredCharacter_ActivatesItAndInactivatesOthers()
    {
        var root = CreateTempDir();
        try
        {
            var (identities, characters, binder) = CreateServices(root);
            var identity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Henry", "token_henry", null));
            var first = await characters.CreateAsync(identity.Identity!.InternalId, new CharacterCreateRequest("Henry", "Skalitz", "knight"));
            var second = await characters.CreateAsync(identity.Identity!.InternalId, new CharacterCreateRequest("Sir", "Radzig", "noble"));
            await characters.TrySetStatusAsync(first.Character!.InternalId, CharacterProfileStatus.Active);

            var result = await binder.BindAsync(identity.Identity.InternalId, second.Character!.InternalId);

            Assert.True(result.IsAllowed);
            Assert.Equal(second.Character.InternalId, result.CharacterId);

            var loadedFirst = await characters.GetByInternalIdAsync(first.Character.InternalId);
            var loadedSecond = await characters.GetByInternalIdAsync(second.Character.InternalId);
            Assert.Equal(CharacterProfileStatus.Inactive, loadedFirst!.Status);
            Assert.Equal(CharacterProfileStatus.Active, loadedSecond!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BindAsync_DeniesPreferredCharacterOwnedByAnotherIdentity()
    {
        var root = CreateTempDir();
        try
        {
            var (identities, characters, binder) = CreateServices(root, new CharacterSessionBindingOptions
            {
                RequireCharacterOnConnect = true,
            });

            var a = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("A", "token_a", null));
            var b = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("B", "token_b", null));
            var characterB = await characters.CreateAsync(b.Identity!.InternalId, new CharacterCreateRequest("Hans", "Capon", "noble"));

            var result = await binder.BindAsync(a.Identity!.InternalId, characterB.Character!.InternalId);

            Assert.False(result.IsAllowed);
            Assert.Equal("Requested character is not owned by this identity.", result.DenialReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BindAsync_AutoSelectsMostRecentlyActiveCharacter()
    {
        var root = CreateTempDir();
        try
        {
            var (identities, characters, binder) = CreateServices(root);
            var identity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Robard", "token_robard", null));
            var first = await characters.CreateAsync(identity.Identity!.InternalId, new CharacterCreateRequest("Captain", "Robard", "guard"));
            var second = await characters.CreateAsync(identity.Identity!.InternalId, new CharacterCreateRequest("Divish", "Talmberg", "lord"));
            await characters.TrySetStatusAsync(second.Character!.InternalId, CharacterProfileStatus.Active);

            var result = await binder.BindAsync(identity.Identity.InternalId, preferredCharacterId: null);

            Assert.True(result.IsAllowed);
            Assert.Equal(second.Character.InternalId, result.CharacterId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BindAsync_WhenRequired_DeniesIdentityWithoutCharacters()
    {
        var root = CreateTempDir();
        try
        {
            var (identities, _, binder) = CreateServices(root, new CharacterSessionBindingOptions
            {
                RequireCharacterOnConnect = true,
            });
            var identity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Theresa", "token_theresa", null));

            var result = await binder.BindAsync(identity.Identity!.InternalId, preferredCharacterId: null);

            Assert.False(result.IsAllowed);
            Assert.Equal("No character available for this identity.", result.DenialReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (PlayerIdentityService identities, CharacterProfileService characters, CharacterSessionBindingService binder) CreateServices(
        string root,
        CharacterSessionBindingOptions? options = null)
    {
        var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
        var sink = new InMemoryObservabilitySink();
        var identities = new PlayerIdentityService(store, sink);
        var characters = new CharacterProfileService(store, identities, sink);
        var binder = new CharacterSessionBindingService(characters, identities, options);
        return (identities, characters, binder);
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_character_bind_{Guid.NewGuid():N}");
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
