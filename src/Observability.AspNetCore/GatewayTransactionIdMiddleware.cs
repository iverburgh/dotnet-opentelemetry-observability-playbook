using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Observability.AspNetCore;

/// <summary>
/// Tags the request span with the transaction id a gateway in front of the service adds. Gateways often
/// strip or replace <c>traceparent</c>, so their own id is the only key that joins a gateway log line to
/// your trace.
/// </summary>
internal sealed class GatewayTransactionIdMiddleware(RequestDelegate next, IOptions<ObservabilityOptions> options)
{
    public const string Tag = "gateway.transaction_id";

    private const int MaxLength = 64;

    private readonly string _header = options.Value.GatewayTransactionIdHeader;

    public Task InvokeAsync(HttpContext context)
    {
        if (!string.IsNullOrEmpty(_header)
            && context.Request.Headers.TryGetValue(_header, out var values)
            && Normalise(values.ToString()) is { } transactionId)
        {
            AspNetCoreObservabilityExtensions.RequestActivity(context)?.SetTag(Tag, transactionId);
        }

        return next(context);
    }

    /// <summary>
    /// A GUID in its compact form, so it matches however the gateway formats it; otherwise hex digits and
    /// dashes only. Anything else is a header someone typed, and it does not go into telemetry.
    /// </summary>
    internal static string? Normalise(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (Guid.TryParse(value, out var guid))
        {
            return guid.ToString("N");
        }

        return value.Length <= MaxLength && value.All(c => char.IsAsciiHexDigit(c) || c == '-')
            ? value.ToLowerInvariant()
            : null;
    }
}
