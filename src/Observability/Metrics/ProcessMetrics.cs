using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace Observability.Metrics;

/// <summary>
/// <c>process.completed</c> and <c>process.duration</c>, by process name and result. Counted from every
/// process span, sampled or not: these are the numbers to alert on, not the sampled traces.
/// </summary>
[MeterName(Telemetry.ProcessMeterName)]
public sealed class ProcessMetrics : MetricsBundle
{
    public const string ResultTag = "process.result";

    private readonly Counter<long> _completed;
    private readonly Histogram<double> _duration;

    public ProcessMetrics(IMeterFactory meterFactory, IOptions<ObservabilityOptions> options)
        : base(meterFactory, options)
    {
        _completed = Meter.CreateCounter<long>("process.completed", "{process}", "Business processes that ended, by name and result.");
        _duration = Meter.CreateHistogram<double>("process.duration", "s", "How long business processes took, by name and result.");
    }

    /// <summary>Records one process. An unset status counts as <c>ok</c>.</summary>
    public void Record(string name, ActivityStatusCode status, TimeSpan duration)
    {
        var tags = new TagList
        {
            { ProcessBaggage.Name, name },
            { ResultTag, status == ActivityStatusCode.Error ? "error" : "ok" },
        };
        _completed.Add(1, tags);
        _duration.Record(duration.TotalSeconds, tags);
    }
}
