using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Observability;

/// <summary>
/// Copies allow-listed baggage onto every span when it ends, so the process a span belongs to becomes a
/// column you can filter on — <c>customDimensions</c> in Application Insights. Baggage is not exported by
/// itself: without this processor it travels with the trace but never reaches your backend.
/// </summary>
/// <remarks>
/// Only keys on the allow-list are copied. A caller controls its <c>baggage</c> header; copying all of it
/// would let anyone write arbitrary columns, and personal data, into your telemetry.
/// </remarks>
public sealed class BaggageToTagsProcessor : BaseProcessor<Activity>
{
    private readonly HashSet<string> _keys;

    public BaggageToTagsProcessor(IOptions<ObservabilityOptions> options)
    {
        _keys = new HashSet<string>([.. ProcessBaggage.All, .. options.Value.BaggageTagKeys ?? []], StringComparer.Ordinal);
    }

    public override void OnEnd(Activity data)
    {
        // Baggage enumerates the span's own items first, then its parents': the first value seen is the nearest.
        // An empty nearest value clears an inherited one, such as the step of an outer process.
        HashSet<string>? seen = null;
        foreach (var (key, value) in data.Baggage)
        {
            if (!_keys.Contains(key) || !(seen ??= new(StringComparer.Ordinal)).Add(key))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(value) && data.GetTagItem(key) is null)
            {
                data.SetTag(key, value);
            }
        }
    }
}
