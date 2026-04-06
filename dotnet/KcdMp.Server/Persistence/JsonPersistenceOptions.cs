namespace KcdMp.Server.Persistence;

public sealed class JsonPersistenceOptions
{
    /// <summary>
    /// Default storage location beside the server runtime.
    /// </summary>
    public string BasePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "server-data");

    /// <summary>
    /// Controls human-readable vs compact JSON output.
    /// </summary>
    public JsonPersistenceEnvironment Environment { get; init; } = JsonPersistenceEnvironment.Development;
}
