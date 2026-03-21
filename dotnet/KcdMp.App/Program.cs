using Serilog;
using KcdMp.Shared.Config;
using KcdMp.Client;
using KcdMp.Server;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("kcdmp.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var configPath = Path.Combine(AppContext.BaseDirectory, "kcdmp.json");
var config = KcdmpConfig.LoadOrDefault(configPath);

// CLI overrides: kcdmp.exe [host|join] [friendIp]
if (args.Length > 0) config.Mode = args[0];
if (args.Length > 1) config.FriendIp = args[1];

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

if (config.Mode == "host")
{
    Log.Information("Port    : {Port}", config.Port);
    Log.Information("Password: {Password}", string.IsNullOrEmpty(config.Password) ? "(none)" : "****");

    var server = new RelayServer(config.Port, password: config.Password);
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
