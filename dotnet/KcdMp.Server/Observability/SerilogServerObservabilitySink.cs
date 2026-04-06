using Serilog;

namespace KcdMp.Server.Observability;

public sealed class SerilogServerObservabilitySink : IServerObservabilitySink
{
    private static readonly string[] SensitiveTokens =
    [
        "password",
        "secret",
        "token",
        "credential",
    ];

    private readonly ILogger _logger;
    private readonly ServerObservabilityOptions _options;

    public SerilogServerObservabilitySink(ILogger logger, ServerObservabilityOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new ServerObservabilityOptions();
    }

    public void Emit(ServerObservableEvent observableEvent)
    {
        if (observableEvent.Severity < _options.MinimumSeverity)
            return;

        var safePayload = RedactSensitivePayload(observableEvent.Payload);
        var scoped = _logger
            .ForContext("event_type", observableEvent.Type.ToString())
            .ForContext("component", observableEvent.Component.ToString())
            .ForContext("event_timestamp_utc", observableEvent.OccurredAtUtc.UtcDateTime)
            .ForContext("session_id", observableEvent.SessionId)
            .ForContext("identity_id", observableEvent.IdentityId)
            .ForContext("character_id", observableEvent.CharacterId)
            .ForContext("payload", safePayload, destructureObjects: true);

        var message = observableEvent.Message ?? observableEvent.Type.ToString();
        switch (observableEvent.Severity)
        {
            case ServerObservableSeverity.Debug:
                scoped.Debug("{Message}", message);
                break;
            case ServerObservableSeverity.Information:
                scoped.Information("{Message}", message);
                break;
            case ServerObservableSeverity.Warning:
                scoped.Warning("{Message}", message);
                break;
            case ServerObservableSeverity.Error:
                scoped.Error("{Message}", message);
                break;
            default:
                scoped.Information("{Message}", message);
                break;
        }
    }

    private static IReadOnlyDictionary<string, object?>? RedactSensitivePayload(IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is null || payload.Count == 0)
            return payload;

        Dictionary<string, object?> redacted = new(StringComparer.Ordinal);
        foreach (var (key, value) in payload)
            redacted[key] = IsSensitiveKey(key) ? "[REDACTED]" : value;

        return redacted;
    }

    private static bool IsSensitiveKey(string key)
    {
        foreach (var token in SensitiveTokens)
        {
            if (key.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

