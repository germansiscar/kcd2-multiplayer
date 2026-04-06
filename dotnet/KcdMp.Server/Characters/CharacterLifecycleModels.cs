namespace KcdMp.Server.Characters;

public enum CharacterLifecycleSaveReason
{
    Activation = 0,
    SessionClosed = 1,
    NetworkDisconnect = 2,
    SessionTimeout = 3,
    Shutdown = 4,
    DomainEvent = 5,
}

public sealed class CharacterLifecycleOptions
{
    /// <summary>
    /// Number of retries after the first failed save attempt.
    /// </summary>
    public int SaveRetryCount { get; init; } = 1;

    /// <summary>
    /// Delay between save retries.
    /// </summary>
    public TimeSpan SaveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}

public sealed record CharacterLifecycleValidationResult(
    bool IsAllowed,
    string? DenialReason);

public sealed record CharacterLifecycleLoadResult(
    bool Loaded,
    string? DenialReason,
    CharacterProfileRecord? Character);

public sealed record CharacterLifecycleSaveResult(
    bool Saved,
    string? FailureReason,
    int Attempts);
