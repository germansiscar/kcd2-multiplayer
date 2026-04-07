namespace KcdMp.Server.Audit;

public sealed class ServerAuditOptions
{
    public bool Enabled { get; init; } = true;

    public int RetentionDays { get; init; } = 30;

    public bool WriteIndented { get; init; } = false;
}

