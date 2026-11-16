using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace Observability.Metrics;

/// <summary>Gives a <see cref="MetricsBundle"/> a fixed meter name instead of the derived one.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MeterNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// A class that owns one meter and the instruments on it. Derive from it, create the instruments in the
/// constructor and register it with <c>AddMetricsBundle&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// The meter is named <c>{service.name}.{type name}</c> in lower case unless <see cref="MeterNameAttribute"/>
/// says otherwise. <c>AddObservability</c> subscribes to <c>{service.name}.*</c>, so a new bundle is
/// exported without anyone remembering to call <c>AddMeter</c> for it — the most common reason a new
/// metric never shows up.
/// </remarks>
public abstract class MetricsBundle
{
    protected MetricsBundle(IMeterFactory meterFactory, IOptions<ObservabilityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(options);
        MeterName = GetType().GetCustomAttribute<MeterNameAttribute>()?.Name
            ?? $"{options.Value.ServiceName}.{GetType().Name}".ToLowerInvariant();
        Meter = meterFactory.Create(MeterName);
    }

    public string MeterName { get; }

    protected Meter Meter { get; }

    internal static string WildcardFor(string serviceName) => $"{serviceName.ToLowerInvariant()}.*";
}
