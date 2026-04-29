using System.Diagnostics.Metrics;
using Kode.Agent.Sdk.Diagnostics;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace KodaClaw.Runtime.Diagnostics;

/// <summary>
/// Subscribes to SDK <see cref="KodeAgentMetrics"/> via <see cref="MeterListener"/>
/// and bridges selected measurements to <see cref="IDiagnosticsService"/>.
/// Only histograms and error counters are forwarded to avoid flooding the event stream.
/// Registered as <see cref="IHostedService"/> for proper lifecycle management.
/// </summary>
public sealed class MetricsBridgeService : IHostedService, IDisposable
{
    private static readonly HashSet<string> BridgedInstruments = new(StringComparer.OrdinalIgnoreCase)
    {
        "kode.agent.run.duration",
        "kode.agent.model.request.duration",
        "kode.agent.tool.duration",
        "kode.agent.model.errors",
        "kode.agent.tool.errors",
        "kode.agent.context.compressions",
    };

    private readonly IDiagnosticsService _diagnosticsService;
    private MeterListener? _listener;

    public MetricsBridgeService(IDiagnosticsService diagnosticsService)
    {
        _diagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == KodeAgentMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        _listener = null;
        return Task.CompletedTask;
    }

    private void OnMeasurement<T>(
        Instrument instrument, T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (!BridgedInstruments.Contains(instrument.Name))
            return;

        var attrs = new Dictionary<string, string?> { ["value"] = measurement?.ToString() };
        foreach (var tag in tags)
            attrs[tag.Key] = tag.Value?.ToString();

        _diagnosticsService.Record(new DiagnosticEvent(
            Id: $"diag-{Guid.NewGuid():N}",
            Source: "kode.agent.metrics",
            EventType: $"metrics.{instrument.Name}",
            Level: instrument.Name.Contains("error", StringComparison.OrdinalIgnoreCase) ? "warning" : "debug",
            Message: $"{instrument.Name}: {measurement}{(string.IsNullOrEmpty(instrument.Unit) ? "" : $" {instrument.Unit}")}",
            Timestamp: DateTimeOffset.UtcNow,
            Attributes: attrs));
    }

    public void Dispose() => _listener?.Dispose();
}
