using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Observability.PayloadArchive;

/// <summary>A container that an outgoing <see cref="HttpClient"/> archives to; registered so the health check covers it.</summary>
internal sealed record OutboundArchiveContainer(string Name);

/// <summary>
/// Checks, per archive container, that it is reachable and that blobs in it can be found by tag. Reports
/// <see cref="HealthStatus.Degraded"/>, never unhealthy: the archive is best effort, and a storage outage must
/// not take an instance out of the load balancer.
/// </summary>
/// <remarks>
/// Nothing is written: a probe blob in an immutable container could never be deleted. Containers are read
/// from the options monitor on every check, so a configuration change is checked from the next run.
/// </remarks>
internal sealed class PayloadArchiveHealthCheck(
    IPayloadArchiveStore store,
    IOptionsMonitor<PayloadArchiveOptions> optionsMonitor,
    IEnumerable<OutboundArchiveContainer> outboundContainers) : IHealthCheck
{
    public const string Name = "payload-archive";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        PayloadArchiveOptions options;
        try
        {
            options = optionsMonitor.CurrentValue;
        }
        catch (OptionsValidationException exception)
        {
            return HealthCheckResult.Degraded("The payload archive configuration is invalid; nothing is archived.", exception);
        }

        var containers = options.EffectiveEndpoints
            .Select(endpoint => endpoint.ContainerName)
            .Concat(outboundContainers.Select(container => container.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        Exception? firstFailure = null;

        // A probe every few seconds would otherwise be a root span, or several, in every trace backend.
        using var suppress = SuppressInstrumentationScope.Begin();
        foreach (var container in containers)
        {
            // Not only RequestFailedException: a credential that cannot get a token throws its own exception type.
            try
            {
                await store.CheckAccessAsync(container, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                data[container] = $"unreachable: {FirstLine(exception.Message)}";
                firstFailure ??= exception;
                continue;
            }

            try
            {
                await store.CheckTagQueryAsync(container, cancellationToken).ConfigureAwait(false);
                data[container] = "ok";
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                data[container] = $"finding blobs by tag is not allowed: {FirstLine(exception.Message)}";
                firstFailure ??= exception;
            }
        }

        return firstFailure is null
            ? HealthCheckResult.Healthy($"{containers.Count} archive container(s) reachable and searchable.", data)
            : HealthCheckResult.Degraded("Payloads may not be archived; requests are not affected.", firstFailure, data);
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}
