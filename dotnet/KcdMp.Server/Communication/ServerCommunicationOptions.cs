namespace KcdMp.Server.Communication;

public sealed class ServerCommunicationOptions
{
    public int MaxMessageLength { get; init; } = 280;
    public int RateLimitMaxMessages { get; init; } = 6;
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromSeconds(4);
    public float ProximityNormalRadius { get; init; } = 25f;
    public float ProximityWhisperRadius { get; init; } = 10f;
    public float ProximityShoutRadius { get; init; } = 60f;
    public float ProximityZoneCellSize { get; init; } = 120f;
    public bool AuditIncludeMessageText { get; init; } = false;
}
