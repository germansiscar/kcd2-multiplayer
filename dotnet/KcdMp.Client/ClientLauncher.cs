namespace KcdMp.Client;

public static class ClientLauncher
{
    public static async Task RunAsync(string serverHost, int serverPort,
        string password, string name, string gameApiBase,
        string? persistentToken = null, string? steamId = null, string? characterId = null,
        CancellationToken ct = default)
    {
        var bridge = new GameBridge(
            serverHost,
            serverPort,
            password,
            name,
            gameApiBase,
            persistentToken,
            steamId,
            characterId,
            null);
        await bridge.RunAsync(ct);
    }
}
