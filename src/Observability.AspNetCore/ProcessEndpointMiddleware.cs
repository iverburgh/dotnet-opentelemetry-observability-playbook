using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Observability.AspNetCore;

/// <summary>
/// Runs an endpoint inside a process span when the endpoint, or its class, has a <see cref="ProcessAttribute"/>.
/// An exception or a failing status code fails the process; 401 and 404 do not by default.
/// </summary>
internal sealed class ProcessEndpointMiddleware(
    RequestDelegate next,
    ProcessTracer tracer,
    IOptions<ObservabilityOptions> options,
    ILogger<ProcessEndpointMiddleware> logger)
{
    private readonly IReadOnlyCollection<int> _nonFailures = options.Value.EffectiveNonFailureStatusCodes;

    public async Task InvokeAsync(HttpContext context)
    {
        // The last metadata item is the most specific: method attributes come after class attributes.
        var process = context.GetEndpoint()?.Metadata.GetMetadata<ProcessAttribute>();
        if (process is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        Activity? activity = null;
        try
        {
            activity = tracer.StartProcess(process.Name, process.Step);
        }
        catch (Exception ex)
        {
            Log.MiddlewareFailed(logger, ex);
        }

        using (activity)
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ProcessTracer.Fail(activity, ex);
                throw;
            }

            if (activity is not null && activity.Status == ActivityStatusCode.Unset)
            {
                var status = ProcessTracer.StatusFromHttp(context.Response.StatusCode, _nonFailures);
                if (status == ActivityStatusCode.Error)
                {
                    activity.SetStatus(status, $"HTTP {context.Response.StatusCode}");
                }
            }
        }
    }
}
