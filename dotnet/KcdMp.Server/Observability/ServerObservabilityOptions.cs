namespace KcdMp.Server.Observability;

public sealed class ServerObservabilityOptions
{
    public ServerObservableSeverity MinimumSeverity { get; set; } = ServerObservableSeverity.Information;
    public bool IncludeSyncMicroEvents { get; set; } = false;
}

