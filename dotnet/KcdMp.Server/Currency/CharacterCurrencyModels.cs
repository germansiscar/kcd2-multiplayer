namespace KcdMp.Server.Currency;

public sealed class CharacterCurrencyRecord
{
    public string CharacterId { get; set; } = "";
    public long Balance { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastSavedAtUtc { get; set; }
}

public sealed class CharacterCurrencyOptions
{
    /// <summary>
    /// Initial currency balance when a character has no persisted wallet yet.
    /// </summary>
    public long InitialBalance { get; init; } = 0;

    /// <summary>
    /// Number of retries after the first failed save attempt.
    /// </summary>
    public int SaveRetryCount { get; init; } = 1;

    /// <summary>
    /// Delay between save retries.
    /// </summary>
    public TimeSpan SaveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}

public sealed record CharacterCurrencyLoadResult(
    bool Loaded,
    string? DenialReason,
    CharacterCurrencyRecord? Currency,
    bool CreatedDefault);

public sealed record CharacterCurrencySaveResult(
    bool Saved,
    string? FailureReason,
    int Attempts);

public sealed record CharacterCurrencyMutationResult(
    bool Applied,
    string? DenialReason,
    CharacterCurrencyRecord? Currency);
