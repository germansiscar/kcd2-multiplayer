using KcdMp.Client;
using KcdMp.Server;
using KcdMp.Server.Observability;
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

if (config.Mode == "host")
{
    Log.Information("Port    : {Port}", config.Port);
    Log.Information("Password: {Password}", string.IsNullOrEmpty(config.Password) ? "(none)" : "****");
    Log.Information("Observability severity: {Severity}", observabilityOptions.MinimumSeverity);

    var server = new RelayServer(config.Port, password: config.Password, observabilityOptions: observabilityOptions);
    _ = server.RunAsync(cts.Token);

    await ClientLauncher.RunAsync("localhost", config.Port, "", name, gameApi, cts.Token);
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
    await ClientLauncher.RunAsync(config.FriendIp, config.Port, config.Password, name, gameApi, cts.Token);
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

