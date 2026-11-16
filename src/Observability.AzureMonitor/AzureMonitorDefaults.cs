using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;

namespace Observability.AzureMonitor;

/// <summary>
/// Sets every exporter default that changes what you pay for or what you see, explicitly. Defaults move
/// between package versions; a pinned value turns a silent behaviour change into a visible diff.
/// </summary>
public static class AzureMonitorDefaults
{
    // One transport for the whole process. With the default handler a pooled connection lives for ever; when
    // the network drops it half-open, every export fails and nothing reports it.
    private static readonly Lazy<HttpClientTransport> SharedTransport = new(() => new HttpClientTransport(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    }));

    public static void Apply(AzureMonitorExporterOptions options, double samplingRatio)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The exporter replaces the tracer's sampler with its own. Rate-limited sampling (TracesPerSecond,
        // 5 by default) wins over the ratio, so clear it or the configured ratio means nothing.
        options.SamplingRatio = (float)samplingRatio;
        options.TracesPerSecond = null;

        // On by default: drops every log line of a trace the sampler dropped. Logs are the record of what a
        // process did; keep them all and sample traces only.
        options.EnableTraceBasedLogsSampler = false;

        // Standard metrics are pre-aggregated request and dependency counts that stay exact under sampling.
        options.EnableStandardMetrics = true;

        // Performance counters duplicate what the platform already measures for an App Service or function app.
        options.EnablePerformanceCounters = false;

        // Live metrics cost nothing when nobody looks at them.
        options.EnableLiveMetrics = true;

        options.Transport = SharedTransport.Value;
    }
}
