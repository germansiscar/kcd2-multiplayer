namespace KcdMp.Server.Persistence;

/// <summary>
/// Selects JSON serialization policy by environment.
/// </summary>
public enum JsonPersistenceEnvironment
{
    Development = 0,
    Production = 1,
}
