using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Observability;

/// <summary>
/// Decides which outgoing HTTP requests may carry the <c>baggage</c> header. The runtime adds
/// <c>traceparent</c> and <c>baggage</c> to every request; baggage holds process names and anything else
/// you put in it, and a third party has no business receiving that. This propagator keeps
/// <c>traceparent</c> everywhere and drops baggage for hosts that are not internal.
/// </summary>
public sealed class ScopedBaggagePropagator : DistributedContextPropagator
{
    private static readonly string[] BaggageFields = ["baggage", "Correlation-Context"];

    private readonly DistributedContextPropagator _inner;
    private readonly string[] _internalHosts;

    public ScopedBaggagePropagator(DistributedContextPropagator inner, IEnumerable<string> internalHosts)
    {
        _inner = inner;
        _internalHosts = [.. internalHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant())];
    }

    public override IReadOnlyCollection<string> Fields => _inner.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (setter is null)
        {
            return;
        }

        var allowBaggage = MayReceiveBaggage(carrier);
        _inner.Inject(activity, carrier, (c, name, value) =>
        {
            if (!allowBaggage && BaggageFields.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            setter(c, name, value);
        });
    }

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
        _inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) =>
        _inner.ExtractBaggage(carrier, getter);

    internal bool MayReceiveBaggage(object? carrier)
    {
        if (carrier is not HttpRequestMessage { RequestUri: { IsAbsoluteUri: true } uri })
        {
            return true;
        }

        var host = uri.Host.ToLowerInvariant();
        foreach (var allowed in _internalHosts)
        {
            if (allowed == "*" || host == allowed || (allowed.StartsWith('.') && host.EndsWith(allowed, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Installs <see cref="ScopedBaggagePropagator"/> as the process-wide propagator when the host starts.
/// </summary>
/// <remarks>
/// A <see cref="SocketsHttpHandler"/> captures the propagator when it is created, so a handler built before
/// the host starts keeps the default. Handlers from <c>IHttpClientFactory</c> are built later and are fine;
/// for a handler you construct yourself, set <see cref="SocketsHttpHandler.ActivityHeadersPropagator"/>.
/// </remarks>
internal sealed class BaggagePropagationService(IOptions<ObservabilityOptions> options) : IHostedService
{
    private DistributedContextPropagator? _previous;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _previous = DistributedContextPropagator.Current;
        if (_previous is not ScopedBaggagePropagator)
        {
            DistributedContextPropagator.Current = new ScopedBaggagePropagator(_previous, options.Value.InternalHosts ?? []);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_previous is not null)
        {
            DistributedContextPropagator.Current = _previous;
        }

        return Task.CompletedTask;
    }
}
