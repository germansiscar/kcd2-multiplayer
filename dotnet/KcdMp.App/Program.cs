using KcdMp.Client;
using KcdMp.Server.AccessControl;
using KcdMp.Server.Audit;
using KcdMp.Server.Characters;
using KcdMp.Server.Currency;
using KcdMp.Server.Identity;
using KcdMp.Server;
using KcdMp.Server.Observability;
using KcdMp.Server.Respawn;
using KcdMp.Shared.Config;
using Serilog;
using Serilog.Events;

var configPath = Path.Combine(AppContext.BaseDirectory, "kcdmp.json");
var config = KcdmpConfig.LoadOrDefault(configPath);

// CLI overrides: kcdmp.exe [host|join] [friendIp]
if (args.Length > 0) config.Mode = args[0];
if (args.Length > 1) config.FriendIp = args[1];

var minimumLevel = ParseLogLevel(config.ObservabilityMinSeverity);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(minimumLevel)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("kcdmp.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

string gameApi = $"http://localhost:{config.GameApiPort}";
string name = config.SteamName == "auto"
    ? SteamNameResolver.Resolve() ?? Environment.MachineName
    : config.SteamName;

Log.Information("=== KCD2 Multiplayer ===");
Log.Information("Mode    : {Mode}", config.Mode);
Log.Information("Name    : {Name}", name);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { cts.Cancel(); } catch (ObjectDisposedException) { } };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };

var observabilityOptions = new ServerObservabilityOptions
{
    MinimumSeverity = ParseObservabilitySeverity(config.ObservabilityMinSeverity),
    IncludeSyncMicroEvents = config.ObservabilityIncludeSyncMicroEvents,
};
var auditOptions = new ServerAuditOptions
{
    Enabled = config.AuditEnabled,
    RetentionDays = config.AuditRetentionDays,
};
var accessMode = ResolveAccessMode(config.ServerAccessMode, config.IdentityRequireWhitelist);
var accessControlOptions = new ServerAccessControlOptions
{
    DefaultAccessMode = accessMode,
};
var identityOptions = new PlayerIdentityOptions
{
};
var characterBindingOptions = new CharacterSessionBindingOptions
{
    RequireCharacterOnConnect = config.CharacterRequireOnConnect,
};
var currencyOptions = new CharacterCurrencyOptions
{
    InitialBalance = config.CurrencyInitialBalance,
    SaveRetryCount = config.CurrencySaveRetryCount,
    SaveRetryDelay = TimeSpan.FromMilliseconds(Math.Max(0, config.CurrencySaveRetryDelayMs)),
};
var respawnOptions = new CharacterRespawnOptions
{
    UnconsciousDuration = TimeSpan.FromSeconds(Math.Max(1, config.RespawnUnconsciousDurationSeconds)),
    DefaultRespawnPolicyId = string.IsNullOrWhiteSpace(config.RespawnDefaultPolicyId) ? "default" : config.RespawnDefaultPolicyId.Trim(),
    DefaultRespawnPointId = string.IsNullOrWhiteSpace(config.RespawnDefaultPointId) ? "default_spawn" : config.RespawnDefaultPointId.Trim(),
    SaveRetryCount = config.RespawnSaveRetryCount,
    SaveRetryDelay = TimeSpan.FromMilliseconds(Math.Max(0, config.RespawnSaveRetryDelayMs)),
};

if (config.Mode == "host")
{
    Log.Information("Port    : {Port}", config.Port);
    Log.Information("Password: {Password}", string.IsNullOrEmpty(config.Password) ? "(none)" : "****");
    Log.Information("Access mode (bootstrap default): {Mode}", accessMode);
    Log.Information("Observability severity: {Severity}", observabilityOptions.MinimumSeverity);
    Log.Information("Audit enabled: {Enabled} (retention days: {RetentionDays})", auditOptions.Enabled, auditOptions.RetentionDays);
    Log.Information("Currency initial balance: {Balance}", currencyOptions.InitialBalance);
    Log.Information("Respawn unconscious duration (s): {Duration}", respawnOptions.UnconsciousDuration.TotalSeconds);
    Log.Information("Bootstrap admin identities: {Count}", config.AdminIdentityIds.Count);

    var server = new RelayServer(
        config.Port,
        password: config.Password,
        accessControlOptions: accessControlOptions,
        identityOptions: identityOptions,
        characterBindingOptions: characterBindingOptions,
        characterCurrencyOptions: currencyOptions,
        characterRespawnOptions: respawnOptions,
        auditOptions: auditOptions,
        observabilityOptions: observabilityOptions,
        bootstrapAdminIdentityIds: config.AdminIdentityIds);
    _ = server.RunAsync(cts.Token);

    await ClientLauncher.RunAsync(
        "localhost",
        config.Port,
        "",
        name,
        gameApi,
        config.PersistentToken,
        config.SteamId,
        config.CharacterId,
        cts.Token);
}
else
{
    if (string.IsNullOrEmpty(config.FriendIp))
    {
        Console.Write("Enter host IP: ");
        config.FriendIp = Console.ReadLine()?.Trim() ?? "";
        config.Mode = "join";
        config.Save(configPath);
    }

    Log.Information("Host    : {Host}", $"{config.FriendIp}:{config.Port}");
    await ClientLauncher.RunAsync(
        config.FriendIp,
        config.Port,
        config.Password,
        name,
        gameApi,
        config.PersistentToken,
        config.SteamId,
        config.CharacterId,
        cts.Token);
}

static LogEventLevel ParseLogLevel(string configuredLevel)
{
    if (Enum.TryParse(configuredLevel, true, out LogEventLevel parsed))
        return parsed;

    return LogEventLevel.Information;
}

static ServerObservableSeverity ParseObservabilitySeverity(string configuredLevel)
{
    if (Enum.TryParse(configuredLevel, true, out ServerObservableSeverity parsed))
        return parsed;

    return ServerObservableSeverity.Information;
}

static ServerAccessMode ResolveAccessMode(string configuredMode, bool legacyWhitelistFlag)
{
    if (Enum.TryParse(configuredMode, ignoreCase: true, out ServerAccessMode parsed))
        return parsed;

    return legacyWhitelistFlag ? ServerAccessMode.Whitelist : ServerAccessMode.Open;
}

