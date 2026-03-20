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
        };
        var json = config.ToJson();
        var loaded = KcdmpConfig.FromJson(json);
        Assert.Equal("join", loaded.Mode);
        Assert.Equal(9999, loaded.Port);
        Assert.Equal("secret", loaded.Password);
        Assert.Equal("192.168.1.50", loaded.FriendIp);
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
