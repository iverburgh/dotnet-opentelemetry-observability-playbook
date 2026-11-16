using Microsoft.AspNetCore.Http;

namespace Observability.AspNetCore;

/// <summary>
/// Returns the trace id as <c>x-correlation-id</c> on every response. One id for the user, the support desk,
/// the logs and the traces: it is the operation id in Application Insights.
/// </summary>
/// <remarks>
/// An inbound <c>x-correlation-id</c> is ignored on purpose. The trace id comes from <c>traceparent</c> or
/// is created here; a second, caller-chosen id would be one more thing that can disagree.
/// </remarks>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "x-correlation-id";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var httpContext = (HttpContext)state;
            var activity = AspNetCoreObservabilityExtensions.RequestActivity(httpContext);
            if (activity is not null)
            {
                httpContext.Response.Headers[HeaderName] = activity.TraceId.ToHexString();
            }

            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
