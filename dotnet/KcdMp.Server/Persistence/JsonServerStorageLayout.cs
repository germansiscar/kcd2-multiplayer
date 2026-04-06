namespace KcdMp.Server.Persistence;

/// <summary>
/// Physical storage layout for server-side JSON persistence.
/// </summary>
public sealed class JsonServerStorageLayout
{
    private static readonly char[] InvalidPathChars = Path.GetInvalidFileNameChars();

    private readonly JsonPersistenceOptions _options;
    private readonly IReadOnlyList<string> _bootstrapDomains;

    public JsonServerStorageLayout(JsonPersistenceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _bootstrapDomains = BuildBootstrapDomains(options.BootstrapDomains);
    }

    public string RootPath => _options.BasePath;

    public IReadOnlyList<string> BootstrapDomains => _bootstrapDomains;

    public void EnsureInitialized()
    {
        Directory.CreateDirectory(RootPath);
        foreach (var domain in _bootstrapDomains)
            Directory.CreateDirectory(GetDomainPath(domain));
    }

    public string GetDomainPath(string domain)
    {
        ValidatePathPart(domain, nameof(domain));
        return Path.Combine(RootPath, domain);
    }

    public string GetEntityPath(string domain, string internalId)
    {
        ValidatePathPart(internalId, nameof(internalId));
        return Path.Combine(GetDomainPath(domain), $"{internalId}.json");
    }

    private static IReadOnlyList<string> BuildBootstrapDomains(IReadOnlyCollection<string> configuredDomains)
    {
        var output = new List<string>(JsonPersistenceDomains.Default.Count + configuredDomains.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var required in JsonPersistenceDomains.Default)
        {
            if (seen.Add(required))
                output.Add(required);
        }

        foreach (var configured in configuredDomains)
        {
            ValidatePathPart(configured, nameof(configuredDomains));
            if (seen.Add(configured))
                output.Add(configured);
        }

        return output;
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
