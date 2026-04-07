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
    public string PersistentToken { get; set; } = "";
    public string SteamId { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public string ServerAccessMode { get; set; } = "Open";
    public bool IdentityRequireWhitelist { get; set; } = false;
    public bool CharacterRequireOnConnect { get; set; } = false;
    public long CurrencyInitialBalance { get; set; } = 0;
    public int CurrencySaveRetryCount { get; set; } = 1;
    public int CurrencySaveRetryDelayMs { get; set; } = 100;
    public int RespawnUnconsciousDurationSeconds { get; set; } = 300;
    public string RespawnDefaultPolicyId { get; set; } = "default";
    public string RespawnDefaultPointId { get; set; } = "default_spawn";
    public int RespawnSaveRetryCount { get; set; } = 1;
    public int RespawnSaveRetryDelayMs { get; set; } = 100;
    public string ObservabilityMinSeverity { get; set; } = "Information";
    public bool ObservabilityIncludeSyncMicroEvents { get; set; } = false;
    public bool AuditEnabled { get; set; } = true;
    public int AuditRetentionDays { get; set; } = 30;
    public List<string> AdminIdentityIds { get; set; } = [];

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
