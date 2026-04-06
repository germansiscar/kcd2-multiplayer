namespace KcdMp.Server.Persistence;

/// <summary>
/// Server-side JSON persistence contract.
/// Data is partitioned by domain and stable internal id.
/// </summary>
public interface IJsonPersistenceStore
{
    Task<T?> LoadAsync<T>(
        string domain,
        string internalId,
        Func<T, bool>? validate = null,
        CancellationToken ct = default);

    Task SaveAsync<T>(
        string domain,
        string internalId,
        T value,
        CancellationToken ct = default);

    Task<T> UpdateAsync<T>(
        string domain,
        string internalId,
        Func<T?, T> update,
        Func<T, bool>? validate = null,
        CancellationToken ct = default);
}
