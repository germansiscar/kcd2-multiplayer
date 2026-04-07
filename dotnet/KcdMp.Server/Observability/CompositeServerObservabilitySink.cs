using Serilog;

namespace KcdMp.Server.Observability;

public sealed class CompositeServerObservabilitySink : IServerObservabilitySink
{
    private readonly IReadOnlyList<IServerObservabilitySink> _sinks;
    private readonly ILogger _logger;

    public CompositeServerObservabilitySink(IEnumerable<IServerObservabilitySink> sinks, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks.ToArray();
        _logger = logger ?? Log.Logger;
    }

    public void Emit(ServerObservableEvent observableEvent)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Emit(observableEvent);
            }
            catch (Exception ex)
            {
                _logger.Error(
                    ex,
                    "[observability] sink failure sink_type={SinkType} event_type={EventType}",
                    sink.GetType().FullName,
                    observableEvent.Type);
            }
        }
    }
}

