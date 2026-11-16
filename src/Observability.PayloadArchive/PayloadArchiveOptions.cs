using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Observability.PayloadArchive;

/// <summary>
/// The <c>PayloadArchive</c> section: where raw HTTP payloads go and which requests are archived. Everything
/// except <see cref="ServiceUri"/> is read per request, so a configuration refresh takes effect without a restart.
/// </summary>
public sealed partial class PayloadArchiveOptions
{
    public const string SectionName = "PayloadArchive";

    /// <summary>
    /// The blob endpoint of the storage account, for example <c>https://my-account.blob.core.windows.net</c>.
    /// Required, and HTTPS: a token credential is never sent over plain HTTP. Read once, at start-up.
    /// </summary>
    public Uri? ServiceUri { get; set; }

    /// <summary>
    /// The inbound paths to archive, each with its own container. The first endpoint whose prefix matches
    /// wins, so list specific prefixes before general ones; validation rejects an endpoint that can never match.
    /// </summary>
    public PayloadArchiveEndpoint[]? Endpoints { get; set; }

    /// <summary>Headers whose values are replaced by <c>[redacted]</c>, in addition to the built-in list.</summary>
    public string[]? RedactedHeaders { get; set; }

    /// <summary>
    /// Query parameters whose values are replaced by <c>[redacted]</c>, in addition to <c>code</c> (function
    /// keys), <c>sig</c> (SAS signatures) and <c>access_token</c>.
    /// </summary>
    public string[]? RedactedQueryParameters { get; set; }

    /// <summary>
    /// The longest one record write may take, retries included. The request waits for the request record
    /// unless the endpoint writes after the response, so this bounds the latency a storage outage can add.
    /// </summary>
    public TimeSpan WriteTimeout { get; set; } = DefaultWriteTimeout;

    internal static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(5);

    internal static readonly string[] DefaultRedactedHeaders =
    [
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
        "X-Api-Key", "Api-Key", "Ocp-Apim-Subscription-Key", "X-Functions-Key",
    ];

    internal static readonly string[] DefaultRedactedQueryParameters = ["code", "sig", "access_token"];

    private HashSet<string>? _redactedHeaders;
    private HashSet<string>? _redactedQueryParameters;

    // Arrays have no defaults in their initialisers: the configuration binder appends to an existing array
    // instead of replacing it. The options monitor hands out one instance per configuration version, so the
    // sets are built once per version, not once per request.
    internal IReadOnlyList<PayloadArchiveEndpoint> EffectiveEndpoints => Endpoints ?? [];

    internal IReadOnlySet<string> EffectiveRedactedHeaders =>
        _redactedHeaders ??= new HashSet<string>(DefaultRedactedHeaders.Concat(RedactedHeaders ?? []), StringComparer.OrdinalIgnoreCase);

    internal IReadOnlySet<string> EffectiveRedactedQueryParameters =>
        _redactedQueryParameters ??= new HashSet<string>(DefaultRedactedQueryParameters.Concat(RedactedQueryParameters ?? []), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every problem with these options; empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ServiceUri is not { IsAbsoluteUri: true } || ServiceUri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{SectionName}:{nameof(ServiceUri)} is required and must be an absolute https URI, such as https://my-account.blob.core.windows.net.");
        }

        if (WriteTimeout < TimeSpan.FromMilliseconds(100) || WriteTimeout > TimeSpan.FromMinutes(1))
        {
            errors.Add($"{SectionName}:{nameof(WriteTimeout)} must be between 100 milliseconds and one minute: it delays requests while storage is down.");
        }

        var endpoints = EffectiveEndpoints;
        for (var i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];
            if (!IsValidPathPrefix(endpoint.PathPrefix))
            {
                errors.Add($"{SectionName}:{nameof(Endpoints)}:{i} has PathPrefix '{endpoint.PathPrefix}': it must start with '/' and must not end with one, such as /api/orders.");
                continue;
            }

            if (!IsValidContainerName(endpoint.ContainerName))
            {
                errors.Add($"{SectionName}:{nameof(Endpoints)}:{i} has ContainerName '{endpoint.ContainerName}': use 3 to 63 lower-case letters, digits and single hyphens, starting and ending with a letter or digit.");
            }

            foreach (var segment in endpoint.ExcludedPathSegments ?? [])
            {
                if (string.IsNullOrWhiteSpace(segment) || segment.Contains('/', StringComparison.Ordinal))
                {
                    errors.Add($"{SectionName}:{nameof(Endpoints)}:{i} has excluded segment '{segment}': a segment is one non-empty path part without '/'.");
                }
            }

            for (var earlier = 0; earlier < i; earlier++)
            {
                if (IsValidPathPrefix(endpoints[earlier].PathPrefix) && PayloadPathFilter.Covers(endpoints[earlier].PathPrefix, endpoint.PathPrefix))
                {
                    errors.Add($"{SectionName}:{nameof(Endpoints)}:{i} ({endpoint.PathPrefix}) never matches: {endpoints[earlier].PathPrefix} comes first and covers it. List specific prefixes first.");
                    break;
                }
            }
        }

        return errors;
    }

    /// <summary>The Azure rules for a container name, checked here so a typo fails at start-up rather than on every write.</summary>
    internal static bool IsValidContainerName(string? name) => name is not null && ContainerNamePattern().IsMatch(name);

    private static bool IsValidPathPrefix(string? prefix) =>
        prefix is { Length: > 0 } && prefix[0] == '/' && (prefix.Length == 1 || prefix[^1] != '/');

    [GeneratedRegex("^(?=.{3,63}$)[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNamePattern();
}

/// <summary>One group of inbound paths that is archived to one container.</summary>
public sealed class PayloadArchiveEndpoint
{
    /// <summary>
    /// The path prefix, matched per segment and case-insensitively: <c>/api/orders</c> matches
    /// <c>/API/Orders/42</c> but not <c>/api/orders-report</c>. <c>/</c> matches every path.
    /// </summary>
    public string PathPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Path segments below the prefix that are never archived, such as <c>health</c> or <c>files</c>. A path is
    /// skipped when any of its segments after the prefix equals one of these, case-insensitively.
    /// </summary>
    public string[]? ExcludedPathSegments { get; set; }

    /// <summary>The container for this endpoint. Separate containers let you give each its own access and retention.</summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>
    /// Write both records after the response has been sent, so the archive adds no latency at all. The price:
    /// if the process dies mid-request, nothing is archived for that request.
    /// </summary>
    public bool WriteAfterResponse { get; set; }
}

internal sealed class PayloadArchiveOptionsValidator : IValidateOptions<PayloadArchiveOptions>
{
    public ValidateOptionsResult Validate(string? name, PayloadArchiveOptions options)
    {
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
