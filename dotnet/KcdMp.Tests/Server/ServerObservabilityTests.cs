using System.Collections.Concurrent;
using System.Net.Sockets;
using KcdMp.Server;
using KcdMp.Server.Observability;
using KcdMp.Shared.Protocol;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace KcdMp.Tests.Server;

[Collection("Integration")]
public sealed class ServerObservabilityTests
{
    [Fact]
    public void SerilogSink_RedactsSensitivePayloadValues()
    {
        var captureSink = new CapturingLogEventSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(captureSink)
            .CreateLogger();
        var sink = new SerilogServerObservabilitySink(logger);

        sink.Emit(new ServerObservableEvent(
            Type: ServerObservableEventType.AuthenticationRejected,
            Component: ServerObservableComponent.Session,
            Severity: ServerObservableSeverity.Warning,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Message: "Auth rejected.",
            Payload: new Dictionary<string, object?>
            {
                ["password"] = "hunter2",
                ["token_value"] = "abc123",
                ["reason"] = "wrong-password",
            }));

        var captured = Assert.Single(captureSink.Events);
        Assert.Contains("[REDACTED]", captured.Properties["payload"].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", captured.Properties["payload"].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", captured.Properties["payload"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SerilogSink_RespectsMinimumSeverity()
    {
        var captureSink = new CapturingLogEventSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(captureSink)
            .CreateLogger();
        var sink = new SerilogServerObservabilitySink(logger, new ServerObservabilityOptions
        {
            MinimumSeverity = ServerObservableSeverity.Warning,
        });

        sink.Emit(new ServerObservableEvent(
            Type: ServerObservableEventType.ServerStarted,
            Component: ServerObservableComponent.ServerLifecycle,
            Severity: ServerObservableSeverity.Information,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Message: "Should not be logged."));
        sink.Emit(new ServerObservableEvent(
            Type: ServerObservableEventType.BackendError,
            Component: ServerObservableComponent.Backend,
            Severity: ServerObservableSeverity.Error,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Message: "Should be logged."));

        var singleEvent = Assert.Single(captureSink.Events);
        Assert.Equal(LogEventLevel.Error, singleEvent.Level);
    }

    [Fact]
    public async Task RelayServer_EmitsLifecycleObservabilityEvents()
    {
        const int port = 17789;
        var sink = new InMemoryObservabilitySink();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, password: "pw", observability: sink);
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(PacketWriter.Auth("wrong"));
            var result = await stream.ReadPacketAsync();
            Assert.Equal(PacketType.AuthResult, result.Type);
        }
        finally
        {
            cts.Cancel();
            try { await serverTask; } catch { }
        }

        Assert.Contains(sink.Events, x => x.Type == ServerObservableEventType.ServerStarted);
        Assert.Contains(sink.Events, x => x.Type == ServerObservableEventType.SessionCreated);
        Assert.Contains(sink.Events, x => x.Type == ServerObservableEventType.AuthenticationRejected);
        Assert.Contains(sink.Events, x => x.Type == ServerObservableEventType.SessionClosed);
        Assert.Contains(sink.Events, x => x.Type == ServerObservableEventType.ServerStopped);
    }

    private sealed class CapturingLogEventSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }

    private sealed class InMemoryObservabilitySink : IServerObservabilitySink
    {
        private readonly ConcurrentQueue<ServerObservableEvent> _events = new();

        public IReadOnlyCollection<ServerObservableEvent> Events => _events.ToArray();

        public void Emit(ServerObservableEvent observableEvent)
        {
            _events.Enqueue(observableEvent);
        }
    }
}
