using System.Text.Json;
using System.Text.Json.Serialization;

namespace KcdMp.Shared.Config;

public class KcdmpConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Mode { get; set; } = "host";
    public int Port { get; set; } = 7778;
    public string Password { get; set; } = "";
    public string FriendIp { get; set; } = "";
    public int GameApiPort { get; set; } = 1404;
    public string SteamName { get; set; } = "auto";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static KcdmpConfig FromJson(string json)
        => JsonSerializer.Deserialize<KcdmpConfig>(json, JsonOpts) ?? new();

    public void Save(string path)
        => File.WriteAllText(path, ToJson());

    public static KcdmpConfig LoadOrDefault(string path)
    {
        if (!File.Exists(path)) return new();
        try { return FromJson(File.ReadAllText(path)); }
        catch { return new(); }
    }
}
