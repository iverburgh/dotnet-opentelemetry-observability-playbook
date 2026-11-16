using Microsoft.Extensions.Options;

namespace Observability;

/// <summary>
/// One section, <c>Observability</c>, for every host: the same service identity, the same sampling ratio
/// and the same privacy rules whether the code runs in a web API, a worker or a function app.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// <c>service.name</c>. Azure Monitor shows it as the cloud role name; without it every service in a
    /// shared workspace looks alike. Required.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary><c>service.namespace</c>. Azure Monitor prefixes the role name with it: <c>[namespace]/name</c>.</summary>
    public string? ServiceNamespace { get; set; }

    /// <summary>The domain (bounded context) this service belongs to; set as <see cref="ProcessBaggage.Domain"/>.</summary>
    public string? Domain { get; set; }

    /// <summary>The subdomain this service belongs to; set as <see cref="ProcessBaggage.Subdomain"/>.</summary>
    public string? Subdomain { get; set; }

    /// <summary>The fraction of traces to keep, from 0 to 1. One setting for every host and exporter.</summary>
    public double SamplingRatio { get; set; } = 1.0;

    /// <summary>
    /// Baggage keys to copy onto spans in addition to the <see cref="ProcessBaggage"/> keys. Anything not
    /// listed stays out of your telemetry, whatever a caller puts in its <c>baggage</c> header.
    /// </summary>
    public string[]? BaggageTagKeys { get; set; }

    /// <summary>
    /// Hosts that may receive the <c>baggage</c> header: an exact host (<c>orders.internal.example</c>), a
    /// suffix (<c>.internal.example</c>) or <c>*</c>. Empty means no host: baggage never leaves the process
    /// over HTTP. <c>traceparent</c> is always sent.
    /// </summary>
    public string[]? InternalHosts { get; set; }

    /// <summary>Request paths not traced at all, such as health probes. Defaults to <c>/health</c> and <c>/alive</c>.</summary>
    public string[]? ExcludedRequestPaths { get; set; }

    /// <summary>HTTP status codes of 400 and up that do not fail a process. Defaults to 401 and 404.</summary>
    public int[]? NonFailureStatusCodes { get; set; }

    /// <summary>Outgoing calls whose error status is an expected answer, not a failed dependency.</summary>
    public ExpectedDependencyResponse[]? ExpectedDependencyResponses { get; set; }

    /// <summary>The request header that carries the gateway's transaction id, if a gateway sits in front.</summary>
    public string GatewayTransactionIdHeader { get; set; } = "x-appgw-trace-id";

    /// <summary>Emit <c>telemetry.heartbeat</c> and a log line on a fixed interval.</summary>
    public bool EnableHeartbeat { get; set; } = true;

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    // Arrays have no defaults in their initialisers: the configuration binder appends to an existing array
    // instead of replacing it, so a default would always be in the result.
    internal IReadOnlyList<string> EffectiveExcludedRequestPaths => ExcludedRequestPaths ?? ["/health", "/alive"];

    internal IReadOnlyCollection<int> EffectiveNonFailureStatusCodes => NonFailureStatusCodes ?? [401, 404];

    /// <summary>Every problem with these options; empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            errors.Add($"{SectionName}:{nameof(ServiceName)} is required: it identifies this service in every backend.");
        }

        if (SamplingRatio is < 0 or > 1 || double.IsNaN(SamplingRatio))
        {
            errors.Add($"{SectionName}:{nameof(SamplingRatio)} must be between 0 and 1, not {SamplingRatio}.");
        }

        if (HeartbeatInterval < TimeSpan.FromSeconds(1))
        {
            errors.Add($"{SectionName}:{nameof(HeartbeatInterval)} must be at least one second.");
        }

        foreach (var key in BaggageTagKeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(key) || key != key.ToLowerInvariant())
            {
                errors.Add($"{SectionName}:{nameof(BaggageTagKeys)} contains '{key}': baggage keys are lower case.");
            }
        }

        foreach (var expected in ExpectedDependencyResponses ?? [])
        {
            if (string.IsNullOrWhiteSpace(expected.PathContains) || expected.StatusCodes is not { Length: > 0 })
            {
                errors.Add($"{SectionName}:{nameof(ExpectedDependencyResponses)} needs a PathContains and at least one status code per entry.");
            }
        }

        return errors;
    }
}

/// <summary>
/// An outgoing call whose error status is part of the protocol. A lock that is already taken answers 409;
/// counting that as a failed dependency makes every failure rate and alert meaningless.
/// </summary>
public sealed class ExpectedDependencyResponse
{
    /// <summary>A case-insensitive fragment of the URL path, for example <c>/locks/</c>.</summary>
    public string PathContains { get; set; } = string.Empty;

    /// <summary>The status codes that are expected on that path, for example 404 and 409.</summary>
    public int[]? StatusCodes { get; set; }
}

internal sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
