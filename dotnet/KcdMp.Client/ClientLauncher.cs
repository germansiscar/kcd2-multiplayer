namespace KcdMp.Client;

public static class ClientLauncher
{
    public static async Task RunAsync(string serverHost, int serverPort,
        string password, string name, string gameApiBase, CancellationToken ct = default)
    {
        var bridge = new GameBridge(serverHost, serverPort, password, name, gameApiBase, null);
        await bridge.RunAsync(ct);
    }
}
