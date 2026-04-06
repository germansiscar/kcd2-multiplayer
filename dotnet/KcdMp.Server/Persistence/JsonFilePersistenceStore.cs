using System.Text.Json;
using System.Text.Json.Serialization;
using KcdMp.Server.Observability;
using Serilog;

namespace KcdMp.Server.Persistence;

public sealed class JsonFilePersistenceStore : IJsonPersistenceStore
{
    private readonly JsonServerStorageLayout _layout;
    private readonly ILogger _logger;
    private readonly IServerObservabilitySink _observability;
    private readonly JsonSerializerOptions _json;

    public JsonFilePersistenceStore(
        JsonPersistenceOptions? options = null,
        ILogger? logger = null,
        IServerObservabilitySink? observability = null)
    {
        var persistenceOptions = options ?? new JsonPersistenceOptions();
        _layout = new JsonServerStorageLayout(persistenceOptions);
        _layout.EnsureInitialized();
        _logger = logger ?? Log.Logger;
        _observability = observability ?? new SerilogServerObservabilitySink(_logger);
        _json = new JsonSerializerOptions
        {
            WriteIndented = persistenceOptions.Environment == JsonPersistenceEnvironment.Development,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public async Task<T?> LoadAsync<T>(
        string domain,
        string internalId,
        Func<T, bool>? validate = null,
        CancellationToken ct = default)
    {
        var path = BuildPath(domain, internalId);
        if (!File.Exists(path))
            return default;

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, _json, ct);
            if (value is null)
                throw new PersistenceLoadException($"Persistence record is null for '{domain}/{internalId}'.");

            if (validate is not null && !validate(value))
                throw new PersistenceValidationException(
                    $"Validation failed for persistence record '{domain}/{internalId}'.");

            EmitPersistenceEvent(
                ServerObservableEventType.PersistenceLoadCompleted,
                ServerObservableSeverity.Information,
                "Persistence record loaded.",
                domain,
                internalId,
                path);
            return value;
        }
        catch (PersistenceLoadException)
        {
            EmitPersistenceEvent(
                ServerObservableEventType.PersistenceLoadFailed,
                ServerObservableSeverity.Error,
                "Persistence load failed.",
                domain,
                internalId,
                path);
            throw;
        }
        catch (PersistenceValidationException)
        {
            EmitPersistenceEvent(
                ServerObservableEventType.PersistenceLoadFailed,
                ServerObservableSeverity.Warning,
                "Persistence validation failed.",
                domain,
                internalId,
                path);
            throw;
        }
        catch (JsonException ex)
        {
            EmitPersistenceEvent(
                ServerObservableEventType.PersistenceLoadFailed,
                ServerObservableSeverity.Error,
                "Persistence JSON parse failed.",
                domain,
                internalId,
                path,
                ex.Message);
            throw new PersistenceLoadException(
                $"Invalid JSON for persistence record '{domain}/{internalId}'.", ex);
        }
    }

    public async Task SaveAsync<T>(
        string domain,
        string internalId,
        T value,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        var path = BuildPath(domain, internalId);
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("Persistence path does not contain a directory.");
        Directory.CreateDirectory(directory);

        var tempPath = $"{path}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, _json, ct);
                await stream.FlushAsync(ct);
            }

            // Replace existing file atomically when possible, otherwise move as first write.
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }

            EmitPersistenceEvent(
                ServerObservableEventType.PersistenceSaveCompleted,
                ServerObservableSeverity.Information,
                "Persistence record saved.",
                domain,
                internalId,
                path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            EmitPersistenceEvent(
                ServerObservableEventType.PersistenceSaveFailed,
                ServerObservableSeverity.Error,
                "Persistence save failed.",
                domain,
                internalId,
                path,
                ex.Message);
            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to clean temporary persistence file {TempPath}", tempPath);
                }
            }
        }
    }

    public async Task<T> UpdateAsync<T>(
        string domain,
        string internalId,
        Func<T?, T> update,
        Func<T, bool>? validate = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var current = await LoadAsync<T>(domain, internalId, validate: null, ct);
        var next = update(current)
                   ?? throw new InvalidOperationException("Update callback returned null persistence value.");

        if (validate is not null && !validate(next))
            throw new PersistenceValidationException(
                $"Validation failed for persistence record '{domain}/{internalId}'.");

        await SaveAsync(domain, internalId, next, ct);
        return next;
    }

    private string BuildPath(string domain, string internalId)
    {
        return _layout.GetEntityPath(domain, internalId);
    }

    private void EmitPersistenceEvent(
        ServerObservableEventType type,
        ServerObservableSeverity severity,
        string message,
        string domain,
        string internalId,
        string path,
        string? exception = null)
    {
        _observability.Emit(new ServerObservableEvent(
            Type: type,
            Component: ServerObservableComponent.Persistence,
            Severity: severity,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Message: message,
            Payload: new Dictionary<string, object?>
            {
                ["domain"] = domain,
                ["internal_id"] = internalId,
                ["path"] = path,
                ["exception"] = exception,
            }));
    }
}
