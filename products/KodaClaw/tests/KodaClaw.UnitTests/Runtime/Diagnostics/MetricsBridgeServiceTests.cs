using FluentAssertions;
using Kode.Agent.Sdk.Diagnostics;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Runtime.Diagnostics;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime.Diagnostics;

public sealed class MetricsBridgeServiceTests : IAsyncLifetime
{
    private readonly Mock<IDiagnosticsService> _diagnosticsMock = new();
    private readonly List<DiagnosticEvent> _recordedEvents = [];
    private readonly MetricsBridgeService _bridge;

    public MetricsBridgeServiceTests()
    {
        _diagnosticsMock
            .Setup(d => d.Record(It.IsAny<DiagnosticEvent>()))
            .Callback<DiagnosticEvent>(e => _recordedEvents.Add(e));

        _bridge = new MetricsBridgeService(_diagnosticsMock.Object);
    }

    public async Task InitializeAsync() => await _bridge.StartAsync(CancellationToken.None);

    public async Task DisposeAsync() => await _bridge.StopAsync(CancellationToken.None);

    [Fact]
    public void Should_bridge_run_duration_histogram()
    {
        KodeAgentMetrics.RunDuration.Record(2500.0,
            new KeyValuePair<string, object?>("model", "test"));

        _recordedEvents.Should().ContainSingle(e => e.EventType == "metrics.kode.agent.run.duration");
        var evt = _recordedEvents.First(e => e.EventType == "metrics.kode.agent.run.duration");
        evt.Source.Should().Be("kode.agent.metrics");
        evt.Level.Should().Be("debug");
        evt.Attributes.Should().ContainKey("model");
    }

    [Fact]
    public void Should_bridge_model_errors_as_warning_level()
    {
        KodeAgentMetrics.ModelErrors.Add(1,
            new KeyValuePair<string, object?>("model", "test-model"));

        _recordedEvents.Should().ContainSingle(e => e.EventType == "metrics.kode.agent.model.errors");
        var evt = _recordedEvents.First(e => e.EventType == "metrics.kode.agent.model.errors");
        evt.Level.Should().Be("warning");
    }

    [Fact]
    public void Should_bridge_tool_errors_as_warning_level()
    {
        KodeAgentMetrics.ToolErrors.Add(1,
            new KeyValuePair<string, object?>("tool.name", "bash_run"));

        _recordedEvents.Should().ContainSingle(e => e.EventType == "metrics.kode.agent.tool.errors");
        var evt = _recordedEvents.First(e => e.EventType == "metrics.kode.agent.tool.errors");
        evt.Level.Should().Be("warning");
        evt.Attributes.Should().ContainKey("tool.name");
    }

    [Fact]
    public void Should_bridge_tool_duration_histogram()
    {
        KodeAgentMetrics.ToolDuration.Record(150.0,
            new KeyValuePair<string, object?>("tool.name", "fs_read"));

        _recordedEvents.Should().ContainSingle(e => e.EventType == "metrics.kode.agent.tool.duration");
    }

    [Fact]
    public void Should_not_bridge_non_whitelisted_counters()
    {
        KodeAgentMetrics.RunsStarted.Add(1);
        KodeAgentMetrics.StepsCompleted.Add(1);
        KodeAgentMetrics.TokensInput.Add(100);
        KodeAgentMetrics.TokensOutput.Add(50);
        KodeAgentMetrics.ToolExecutions.Add(1);

        _recordedEvents.Should().BeEmpty(
            because: "only histograms and error counters are bridged to avoid noise");
    }

    [Fact]
    public void Should_bridge_context_compressions()
    {
        KodeAgentMetrics.ContextCompressions.Add(1);

        _recordedEvents.Should().ContainSingle(e => e.EventType == "metrics.kode.agent.context.compressions");
    }

    [Fact]
    public void Should_include_measurement_value_in_attributes()
    {
        KodeAgentMetrics.ModelRequestDuration.Record(999.5,
            new KeyValuePair<string, object?>("model", "gpt-4"));

        var evt = _recordedEvents.Should().ContainSingle(e =>
            e.EventType == "metrics.kode.agent.model.request.duration").Subject;
        evt.Attributes!["value"].Should().Be("999.5");
        evt.Attributes!["model"].Should().Be("gpt-4");
    }
}
