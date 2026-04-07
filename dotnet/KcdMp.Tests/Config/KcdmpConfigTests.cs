using Xunit;
using KcdMp.Shared.Config;

namespace KcdMp.Tests.Config;

public class KcdmpConfigTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var config = new KcdmpConfig();
        Assert.Equal("host", config.Mode);
        Assert.Equal(7778, config.Port);
        Assert.Equal("", config.Password);
        Assert.Equal("", config.FriendIp);
        Assert.Equal(1404, config.GameApiPort);
        Assert.Equal("auto", config.SteamName);
        Assert.Equal("", config.PersistentToken);
        Assert.Equal("", config.SteamId);
        Assert.Equal("", config.CharacterId);
        Assert.Equal("Open", config.ServerAccessMode);
        Assert.False(config.CharacterRequireOnConnect);
        Assert.Equal(0, config.CurrencyInitialBalance);
        Assert.Equal(1, config.CurrencySaveRetryCount);
        Assert.Equal(100, config.CurrencySaveRetryDelayMs);
        Assert.Equal(300, config.RespawnUnconsciousDurationSeconds);
        Assert.Equal("default", config.RespawnDefaultPolicyId);
        Assert.Equal("default_spawn", config.RespawnDefaultPointId);
        Assert.Equal(1, config.RespawnSaveRetryCount);
        Assert.Equal(100, config.RespawnSaveRetryDelayMs);
        Assert.True(config.AuditEnabled);
        Assert.Equal(30, config.AuditRetentionDays);
    }

    [Fact]
    public void RoundTrips_ThroughJson()
    {
        var config = new KcdmpConfig
        {
            Mode = "join",
            Port = 9999,
            Password = "secret",
            FriendIp = "192.168.1.50",
            PersistentToken = "token_a",
            SteamId = "steam_1",
            CharacterId = "cid_123",
            ServerAccessMode = "Whitelist",
            CharacterRequireOnConnect = true,
            CurrencyInitialBalance = 250,
            CurrencySaveRetryCount = 3,
            CurrencySaveRetryDelayMs = 400,
            RespawnUnconsciousDurationSeconds = 420,
            RespawnDefaultPolicyId = "town_bed",
            RespawnDefaultPointId = "rattay_square",
            RespawnSaveRetryCount = 2,
            RespawnSaveRetryDelayMs = 250,
            AuditEnabled = true,
            AuditRetentionDays = 45,
        };
        var json = config.ToJson();
        var loaded = KcdmpConfig.FromJson(json);
        Assert.Equal("join", loaded.Mode);
        Assert.Equal(9999, loaded.Port);
        Assert.Equal("secret", loaded.Password);
        Assert.Equal("192.168.1.50", loaded.FriendIp);
        Assert.Equal("token_a", loaded.PersistentToken);
        Assert.Equal("steam_1", loaded.SteamId);
        Assert.Equal("cid_123", loaded.CharacterId);
        Assert.Equal("Whitelist", loaded.ServerAccessMode);
        Assert.True(loaded.CharacterRequireOnConnect);
        Assert.Equal(250, loaded.CurrencyInitialBalance);
        Assert.Equal(3, loaded.CurrencySaveRetryCount);
        Assert.Equal(400, loaded.CurrencySaveRetryDelayMs);
        Assert.Equal(420, loaded.RespawnUnconsciousDurationSeconds);
        Assert.Equal("town_bed", loaded.RespawnDefaultPolicyId);
        Assert.Equal("rattay_square", loaded.RespawnDefaultPointId);
        Assert.Equal(2, loaded.RespawnSaveRetryCount);
        Assert.Equal(250, loaded.RespawnSaveRetryDelayMs);
        Assert.True(loaded.AuditEnabled);
        Assert.Equal(45, loaded.AuditRetentionDays);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var config = KcdmpConfig.LoadOrDefault("/tmp/nonexistent_kcdmp_test.json");
        Assert.Equal("host", config.Mode);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kcdmp_test_{Guid.NewGuid()}.json");
        try
        {
            var config = new KcdmpConfig { Password = "test123", Mode = "join" };
            config.Save(path);
            var loaded = KcdmpConfig.LoadOrDefault(path);
            Assert.Equal("test123", loaded.Password);
            Assert.Equal("join", loaded.Mode);
        }
        finally { File.Delete(path); }
    }
}
