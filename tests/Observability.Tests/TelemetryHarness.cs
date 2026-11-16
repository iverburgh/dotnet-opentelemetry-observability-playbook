using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Observability.Tests;

/// <summary>
/// A started host with <c>AddObservability</c> and in-memory exporters. Spans are exported when they end;
/// metrics when <see cref="FlushMetrics"/> is called.
/// </summary>
internal sealed class TelemetryHarness : IAsyncDisposable
{
    public const string ServiceName = "order-service";
    public const string TestSourceName = "Tests";

    public static readonly ActivitySource TestSource = new(TestSourceName);

    private TelemetryHarness(IHost host, List<Activity> spans, List<Metric> metrics)
    {
        Host = host;
        Spans = spans;
        Metrics = metrics;
    }

    public IHost Host { get; }

    public List<Activity> Spans { get; }

    public List<Metric> Metrics { get; }

    public static Dictionary<string, string?> DefaultSettings() => new()
    {
        ["Observability:ServiceName"] = ServiceName,
        ["Observability:Domain"] = "ordering",
        ["Observability:EnableHeartbeat"] = "false",
    };

    public static async Task<TelemetryHarness> StartAsync(
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection, IConfiguration>? services = null,
        Action<IOpenTelemetryBuilder>? telemetry = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Test" });
        var merged = DefaultSettings();
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            merged[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(merged);
        var spans = new List<Activity>();
        var metrics = new List<Metric>();
        var otel = builder.Services.AddObservability(builder.Configuration)
            .WithTracing(tracing => tracing.AddSource(TestSourceName).AddInMemoryExporter(spans))
            .WithMetrics(meters => meters.AddInMemoryExporter(metrics));
        telemetry?.Invoke(otel);
        services?.Invoke(builder.Services, builder.Configuration);

        var host = builder.Build();
        await host.StartAsync();
        return new TelemetryHarness(host, spans, metrics);
    }

    public T Get<T>()
        where T : notnull => Host.Services.GetRequiredService<T>();

    public Activity Span(string name) => Spans.Single(s => s.DisplayName == name);

    public void FlushMetrics()
    {
        Metrics.Clear();
        Get<MeterProvider>().ForceFlush();
    }

    /// <summary>The sum of a counter's points that match the tags, after <see cref="FlushMetrics"/>.</summary>
    public long Sum(string instrument, params (string Key, object Value)[] tags)
    {
        long total = 0;
        foreach (var metric in Metrics.Where(m => m.Name == instrument))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var pointTags = new Dictionary<string, object?>();
                foreach (var tag in point.Tags)
                {
                    pointTags[tag.Key] = tag.Value;
                }

                if (tags.All(t => pointTags.TryGetValue(t.Key, out var v) && Equals(v, t.Value)))
                {
                    total += point.GetSumLong();
                }
            }
        }

        return total;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}
