using System.Diagnostics;

namespace Observability.Messaging;

/// <summary>
/// Carries W3C trace context through message properties. HTTP has a propagator built in; a message does
/// not, and a message that loses its context starts a new trace — the consumer's logs can then no longer
/// be found from the producer's request.
/// </summary>
public static class MessagingTraceContext
{
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";

    /// <summary>The property older Azure SDKs and the Functions host write; read as a fallback.</summary>
    public const string DiagnosticId = "Diagnostic-Id";

    public const string ReparentedTag = "messaging.trace.reparented";

    /// <summary>Writes the context of <paramref name="activity"/> (W3C ids only) through <paramref name="write"/>.</summary>
    public static void Inject(Activity? activity, Action<string, string> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C || activity.Id is null)
        {
            return;
        }

        write(TraceParent, activity.Id);
        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            write(TraceState, activity.TraceStateString);
        }
    }

    /// <summary>Writes the context of <paramref name="activity"/> into message properties.</summary>
    public static void Inject(Activity? activity, IDictionary<string, object?> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        Inject(activity, (key, value) => properties[key] = value);
    }

    /// <summary>
    /// Reads a remote context: <c>traceparent</c> first, <c>Diagnostic-Id</c> when that is missing.
    /// </summary>
    public static bool TryExtract(Func<string, string?> read, out ActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(read);
        var traceState = read(TraceState);
        foreach (var key in (string[])[TraceParent, DiagnosticId])
        {
            var value = read(key);
            if (!string.IsNullOrWhiteSpace(value) && ActivityContext.TryParse(value, traceState, isRemote: true, out context))
            {
                return true;
            }
        }

        context = default;
        return false;
    }

    /// <summary>Reads a remote context from message properties.</summary>
    public static bool TryExtract(IReadOnlyDictionary<string, object?> properties, out ActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return TryExtract(key => properties.TryGetValue(key, out var value) ? value?.ToString() : null, out context);
    }

    /// <summary>
    /// Starts the consumer span of a message. When the message carries a trace that differs from the current
    /// one, the span joins the producer's trace and links back to the current span — the trigger or poll
    /// loop the runtime started. Otherwise it is an ordinary child.
    /// </summary>
    public static Activity? StartConsumerActivity(
        ActivitySource source,
        string name,
        Func<string, string?> read,
        IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var current = Activity.Current;
        if (!TryExtract(read, out var upstream) || (current is not null && current.TraceId == upstream.TraceId))
        {
            return source.StartActivity(name, ActivityKind.Consumer, parentContext: default, tags: tags);
        }

        ActivityLink[]? links = current is null ? null : [new ActivityLink(current.Context)];
        var activity = source.StartActivity(ActivityKind.Consumer, upstream, tags, links, name: name);
        if (current is not null)
        {
            activity?.SetTag(ReparentedTag, true);
        }

        return activity;
    }

    /// <summary>Starts the consumer span of a message from its properties.</summary>
    public static Activity? StartConsumerActivity(
        ActivitySource source,
        string name,
        IReadOnlyDictionary<string, object?> properties,
        IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return StartConsumerActivity(source, name, key => properties.TryGetValue(key, out var value) ? value?.ToString() : null, tags);
    }
}
