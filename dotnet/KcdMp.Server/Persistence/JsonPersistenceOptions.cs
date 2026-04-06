namespace KcdMp.Server.Persistence;

public sealed class JsonPersistenceOptions
{
    /// <summary>
    /// Default storage location beside the server runtime.
    /// </summary>
    public string BasePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "server-data");

    /// <summary>
    /// Domain folders created during storage bootstrap.
    /// Defaults always include canonical server domains.
    /// </summary>
    public IReadOnlyCollection<string> BootstrapDomains { get; init; } = JsonPersistenceDomains.Default;

    /// <summary>
    /// Controls human-readable vs compact JSON output.
    /// </summary>
    public JsonPersistenceEnvironment Environment { get; init; } = JsonPersistenceEnvironment.Development;
}
