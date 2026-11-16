using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Observability.Metrics;

/// <summary>
/// Turns every process span that stops into <see cref="ProcessMetrics"/>. It listens to the process
/// source itself and asks for every span to be created — not recorded — so the counts stay exact when the
/// sampler drops the trace, and they work even in a host that exports no traces at all.
/// </summary>
internal sealed class ProcessMetricsListener : IHostedService, IDisposable
{
    private readonly ProcessMetrics _metrics;
    private readonly ILogger<ProcessMetricsListener> _logger;
    private ActivityListener? _listener;

    public ProcessMetricsListener(ProcessMetrics metrics, ILogger<ProcessMetricsListener> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    internal void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.ProcessSourceName,

            // PropagationData creates the span (with context and baggage) without recording it. When a
            // tracer samples the trace in, the highest decision of all listeners wins, so this costs nothing.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
            ActivityStopped = OnStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }

    private void OnStopped(Activity activity)
    {
        if (!ProcessTracer.IsProcess(activity))
        {
            return;
        }

        try
        {
            _metrics.Record(activity.OperationName, activity.Status, activity.Duration);
        }
        catch (Exception ex)
        {
            // Telemetry must never break the process it observes.
            Log.ProcessMetricFailed(_logger, ex, activity.OperationName);
        }
    }
}
