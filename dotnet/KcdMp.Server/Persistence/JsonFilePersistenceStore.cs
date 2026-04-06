using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace KcdMp.Server.Persistence;

public sealed class JsonFilePersistenceStore : IJsonPersistenceStore
{
    private static readonly char[] InvalidPathChars = Path.GetInvalidFileNameChars();

    private readonly JsonPersistenceOptions _options;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _json;

    public JsonFilePersistenceStore(JsonPersistenceOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new JsonPersistenceOptions();
        _logger = logger ?? Log.Logger;
        _json = new JsonSerializerOptions
        {
            WriteIndented = _options.Environment == JsonPersistenceEnvironment.Development,
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

            return value;
        }
        catch (JsonException ex)
        {
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
        ValidatePathPart(domain, nameof(domain));
        ValidatePathPart(internalId, nameof(internalId));

        return Path.Combine(_options.BasePath, domain, $"{internalId}.json");
    }

    private static void ValidatePathPart(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", paramName);

        if (value.Contains("..", StringComparison.Ordinal) ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar) ||
            value.IndexOfAny(InvalidPathChars) >= 0)
        {
            throw new ArgumentException("Value contains invalid path characters.", paramName);
        }
    }
}
