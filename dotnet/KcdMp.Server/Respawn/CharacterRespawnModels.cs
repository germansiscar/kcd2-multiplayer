using KcdMp.Server.Characters;

namespace KcdMp.Server.Respawn;

public enum CharacterDefeatState
{
    Alive = 0,
    Unconscious = 1,
    PendingRespawn = 2,
}

public enum CharacterDefeatEventType
{
    None = 0,
    DefeatDetected = 1,
    UnconsciousEntered = 2,
    HealerRecovered = 3,
    UnconsciousExpired = 4,
    UnconsciousCancelled = 5,
    RespawnApplied = 6,
    RespawnApplyFailed = 7,
}

public enum CharacterRespawnTrigger
{
    None = 0,
    UnconsciousTimeout = 1,
    WaitCancelled = 2,
    Disconnect = 3,
    Shutdown = 4,
    RecoveryOnLoad = 5,
}

public sealed class CharacterDefeatLifecycleRecord
{
    public string CharacterId { get; set; } = "";
    public CharacterDefeatState State { get; set; } = CharacterDefeatState.Alive;
    public CharacterDefeatEventType LastEvent { get; set; } = CharacterDefeatEventType.None;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastSavedAtUtc { get; set; }
    public DateTimeOffset? LastDefeatAtUtc { get; set; }
    public DateTimeOffset? UnconsciousUntilUtc { get; set; }
    public DateTimeOffset? LastRespawnAtUtc { get; set; }
    public string LastRespawnPolicyId { get; set; } = "";
    public string LastRespawnPointId { get; set; } = "";
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CharacterRespawnOptions
{
    public TimeSpan UnconsciousDuration { get; init; } = TimeSpan.FromMinutes(5);
    public string DefaultRespawnPolicyId { get; init; } = "default";
    public string DefaultRespawnPointId { get; init; } = "default_spawn";
    public int SaveRetryCount { get; init; } = 1;
    public TimeSpan SaveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}

public sealed record CharacterRespawnLoadResult(
    bool Loaded,
    string? DenialReason,
    CharacterDefeatLifecycleRecord? Lifecycle,
    bool CreatedDefault,
    bool RecoveredFromTransientState);

public sealed record CharacterRespawnActionResult(
    bool Applied,
    string? DenialReason,
    CharacterDefeatLifecycleRecord? Lifecycle);

public sealed record CharacterRespawnSaveResult(
    bool Saved,
    string? FailureReason,
    int Attempts);

public sealed record CharacterRespawnApplyRequest(
    Guid SessionId,
    string IdentityId,
    string CharacterId,
    CharacterRespawnTrigger Trigger,
    string RespawnPolicyId,
    string RespawnPointId,
    CharacterLifecycleSaveReason SaveReason);

public sealed record CharacterRespawnApplyResult(
    bool Applied,
    string? FailureReason);

public interface ICharacterRespawnApplier
{
    Task<CharacterRespawnApplyResult> ApplyAsync(CharacterRespawnApplyRequest request, CancellationToken ct = default);
}

public sealed class NoOpCharacterRespawnApplier : ICharacterRespawnApplier
{
    public Task<CharacterRespawnApplyResult> ApplyAsync(CharacterRespawnApplyRequest request, CancellationToken ct = default)
        => Task.FromResult(new CharacterRespawnApplyResult(true, null));
}
