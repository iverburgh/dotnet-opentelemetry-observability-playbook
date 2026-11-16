using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Observability.Metrics;

namespace Observability.Heartbeat;

/// <summary>
/// The heartbeat instruments. The same counter exists twice: on the bundle meter, which is exported through
/// the <c>{service.name}.*</c> wildcard, and on a meter with a fixed name that is added explicitly. If one
/// series stops and the other does not, the wildcard subscription is broken; if both stop, the export is.
/// </summary>
public sealed class HeartbeatMetrics : MetricsBundle
{
    public const string PathTag = "telemetry.path";

    private readonly Counter<long> _wildcard;
    private readonly Counter<long> _explicit;

    public HeartbeatMetrics(IMeterFactory meterFactory, IOptions<ObservabilityOptions> options)
        : base(meterFactory, options)
    {
        const string description = "Proof that this instance is alive and that its telemetry reaches the backend.";
        _wildcard = Meter.CreateCounter<long>("telemetry.heartbeat", "{beat}", description);
        _explicit = meterFactory.Create(Telemetry.HeartbeatMeterName).CreateCounter<long>("telemetry.heartbeat", "{beat}", description);
    }

    public void Beat()
    {
        _wildcard.Add(1, new KeyValuePair<string, object?>(PathTag, "wildcard"));
        _explicit.Add(1, new KeyValuePair<string, object?>(PathTag, "explicit"));
    }
}

/// <summary>
/// Emits a heartbeat metric and log line on a fixed interval. A host can run thousands of invocations while
/// its telemetry pipeline sends nothing; an alert on a missing heartbeat is the only thing that notices.
/// </summary>
internal sealed class TelemetryHeartbeat(
    HeartbeatMetrics metrics,
    IOptions<ObservabilityOptions> options,
    TimeProvider timeProvider,
    ILogger<TelemetryHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.EnableHeartbeat)
        {
            return;
        }

        Beat();
        using var timer = new PeriodicTimer(settings.HeartbeatInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Beat();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    internal void Beat()
    {
        try
        {
            metrics.Beat();
            Log.Heartbeat(logger, options.Value.ServiceName);
        }
        catch (Exception ex)
        {
            Log.HeartbeatFailed(logger, ex);
        }
    }
}
