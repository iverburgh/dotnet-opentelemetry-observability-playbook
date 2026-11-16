using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Observability;

/// <summary>
/// Starts process spans: spans that stand for a business process rather than a technical operation. A
/// process span carries its name, its place in the chain of processes (the breadcrumb) and the process that
/// started the trace (the origin) as baggage, so every span, log and downstream service below it knows
/// which process it serves. When it stops, <see cref="Metrics.ProcessMetricsListener"/> counts it.
/// </summary>
public sealed class ProcessTracer
{
    internal const string IsProcessProperty = "observability.is_process";

    // A process further up than this is not "the current process" any more; it also bounds the walk.
    private const int MaxDepth = 20;

    private readonly ObservabilityOptions _options;

    public ProcessTracer(IOptions<ObservabilityOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>The nearest process span at or above <see cref="Activity.Current"/> in this process, if any.</summary>
    public static Activity? CurrentProcess
    {
        get
        {
            var activity = Activity.Current;
            for (var depth = 0; activity is not null && depth < MaxDepth; depth++, activity = activity.Parent)
            {
                if (IsProcess(activity))
                {
                    return activity;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Starts a process span as a child of the current span. Dispose it to end the process.
    /// </summary>
    /// <param name="name">A constant, functional name: it becomes a metric dimension.</param>
    /// <param name="step">An optional step within the process.</param>
    /// <param name="baggage">Extra baggage for this process and everything below it. Keys are lower-cased.</param>
    public Activity? StartProcess(string name, string? step = null, IEnumerable<KeyValuePair<string, string?>>? baggage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var activity = Telemetry.ProcessSource.StartActivity(name, ActivityKind.Internal);
        return activity is null ? null : Initialise(activity, name, step, baggage);
    }

    /// <summary>
    /// Starts a process span in a new trace, linked to the current span if there is one. Use it for a unit
    /// of work that should not be part of whatever happens to be current: one run of a timer, one batch, one
    /// message out of many. Without it, every run of a long-lived loop ends up in one endless trace.
    /// </summary>
    /// <returns>A scope; disposing it ends the process and makes the previous span current again.</returns>
    public RootProcessScope StartRootProcess(string name, string? step = null, IEnumerable<KeyValuePair<string, string?>>? baggage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var previous = Activity.Current;
        ActivityLink[]? links = previous is null ? null : [new ActivityLink(previous.Context)];

        // A default parent context means "use Activity.Current", so clear it to get a new trace id.
        Activity.Current = null;
        var activity = Telemetry.ProcessSource.StartActivity(ActivityKind.Internal, parentContext: default, tags: null, links: links, name: name);
        if (activity is null)
        {
            Activity.Current = previous;
            return new RootProcessScope(null, previous);
        }

        return new RootProcessScope(Initialise(activity, name, step, baggage), previous);
    }

    /// <summary>Sets the result of the current process, for code that knows better than the middleware.</summary>
    public static void SetResult(ActivityStatusCode status, string? description = null) =>
        CurrentProcess?.SetStatus(status, description);

    /// <summary>Fails the current process and records the exception on it.</summary>
    public static void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Fail(CurrentProcess, exception);
    }

    internal static void Fail(Activity? process, Exception exception)
    {
        if (process is null)
        {
            return;
        }

        process.SetStatus(ActivityStatusCode.Error, exception.Message);
        process.AddException(exception);
    }

    /// <summary>Whether a span was started by <see cref="ProcessTracer"/>.</summary>
    public static bool IsProcess(Activity activity) => activity.GetCustomProperty(IsProcessProperty) is true;

    /// <summary>The status an HTTP response gives a process: an error from 400 up, except the codes listed.</summary>
    public static ActivityStatusCode StatusFromHttp(int statusCode, IReadOnlyCollection<int> nonFailureStatusCodes) =>
        statusCode >= 400 && !nonFailureStatusCodes.Contains(statusCode) ? ActivityStatusCode.Error : ActivityStatusCode.Unset;

    private Activity Initialise(Activity activity, string name, string? step, IEnumerable<KeyValuePair<string, string?>>? baggage)
    {
        activity.SetCustomProperty(IsProcessProperty, true);
        activity.SetTag(ProcessBaggage.Name, name);

        // GetBaggageItem walks up to the parents, so this reads the breadcrumb and origin of the caller,
        // including a caller in another service whose baggage arrived in the request.
        var breadcrumb = AppendBreadcrumb(activity.GetBaggageItem(ProcessBaggage.Breadcrumb), name);
        activity.SetBaggage(ProcessBaggage.Name, name);
        activity.SetBaggage(ProcessBaggage.Breadcrumb, breadcrumb);
        if (step is not null || activity.GetBaggageItem(ProcessBaggage.Step) is not null)
        {
            // Overwrite a step inherited from an outer process: it does not apply to this one.
            activity.SetBaggage(ProcessBaggage.Step, step ?? string.Empty);
        }

        if (string.IsNullOrEmpty(activity.GetBaggageItem(ProcessBaggage.Origin)))
        {
            activity.SetBaggage(ProcessBaggage.Origin, $"{_options.ServiceName}/{name}");
        }

        if (!string.IsNullOrWhiteSpace(_options.Domain))
        {
            activity.SetBaggage(ProcessBaggage.Domain, _options.Domain);
        }

        if (!string.IsNullOrWhiteSpace(_options.Subdomain))
        {
            activity.SetBaggage(ProcessBaggage.Subdomain, _options.Subdomain);
        }

        foreach (var (key, value) in baggage ?? [])
        {
            var normalised = key.Trim().ToLowerInvariant();
            activity.SetBaggage(normalised, value);
            activity.SetTag(normalised, value);
        }

        return activity;
    }

    internal static string AppendBreadcrumb(string? parent, string name)
    {
        var breadcrumb = string.IsNullOrEmpty(parent) ? name : parent + ProcessBaggage.BreadcrumbSeparator + name;
        return breadcrumb.Length <= ProcessBaggage.MaxBreadcrumbLength
            ? breadcrumb
            : "…" + breadcrumb[^(ProcessBaggage.MaxBreadcrumbLength - 1)..];
    }
}
