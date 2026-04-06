using KcdMp.Server.Persistence;

namespace KcdMp.Tests.Persistence;

public sealed class JsonFilePersistenceStoreTests
{
    private sealed class PlayerRecord
    {
        public string InternalId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Money { get; set; }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips_ByDomainAndStableId()
    {
        var root = CreateTempDir();
        try
        {
            var store = CreateStore(root, JsonPersistenceEnvironment.Development);
            var record = new PlayerRecord { InternalId = "player_001", Name = "Henry", Money = 12 };

            await store.SaveAsync("identity", record.InternalId, record);
            var loaded = await store.LoadAsync<PlayerRecord>("identity", record.InternalId);

            Assert.NotNull(loaded);
            Assert.Equal("player_001", loaded!.InternalId);
            Assert.Equal("Henry", loaded.Name);
            Assert.Equal(12, loaded.Money);
            Assert.True(File.Exists(Path.Combine(root, "identity", "player_001.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DevelopmentMode_WritesIndentedJson()
    {
        var root = CreateTempDir();
        try
        {
            var store = CreateStore(root, JsonPersistenceEnvironment.Development);
            await store.SaveAsync("characters", "char_001", new PlayerRecord { InternalId = "char_001", Name = "Hans" });

            var raw = await File.ReadAllTextAsync(Path.Combine(root, "characters", "char_001.json"));
            Assert.Contains(Environment.NewLine, raw);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ProductionMode_WritesCompactJson()
    {
        var root = CreateTempDir();
        try
        {
            var store = CreateStore(root, JsonPersistenceEnvironment.Production);
            await store.SaveAsync("characters", "char_002", new PlayerRecord { InternalId = "char_002", Name = "Robard" });

            var raw = await File.ReadAllTextAsync(Path.Combine(root, "characters", "char_002.json"));
            Assert.DoesNotContain(Environment.NewLine, raw);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Load_InvalidJson_ThrowsDetectableError()
    {
        var root = CreateTempDir();
        try
        {
            var store = CreateStore(root, JsonPersistenceEnvironment.Development);
            var path = Path.Combine(root, "economy");
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "wallet_001.json"), "{not-valid-json");

            await Assert.ThrowsAsync<PersistenceLoadException>(
                () => store.LoadAsync<PlayerRecord>("economy", "wallet_001"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Load_ValidationFailure_ThrowsDetectableError()
    {
        var root = CreateTempDir();
        try
        {
            var store = CreateStore(root, JsonPersistenceEnvironment.Development);
            await store.SaveAsync("economy", "wallet_002", new PlayerRecord { InternalId = "wallet_002", Name = "Henry", Money = -1 });

            await Assert.ThrowsAsync<PersistenceValidationException>(
                () => store.LoadAsync<PlayerRecord>("economy", "wallet_002", rec => rec.Money >= 0));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static JsonFilePersistenceStore CreateStore(string root, JsonPersistenceEnvironment environment)
        => new(new JsonPersistenceOptions
        {
            BasePath = root,
            Environment = environment,
        });

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_persistence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
