using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Observability.AspNetCore;

public static class AspNetCoreObservabilityExtensions
{
    /// <summary>
    /// Adds ASP.NET Core request spans and metrics. Paths in <see cref="ObservabilityOptions.ExcludedRequestPaths"/>
    /// are not traced: a health probe every few seconds per instance is most of your volume and none of
    /// your insight.
    /// </summary>
    public static IOpenTelemetryBuilder WithAspNetCoreObservability(this IOpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services
            .AddOptions<AspNetCoreTraceInstrumentationOptions>()
            .Configure<IOptions<ObservabilityOptions>>((aspNet, observability) =>
            {
                var excluded = observability.Value.EffectiveExcludedRequestPaths;
                aspNet.Filter = context => !IsExcluded(context.Request.Path, excluded);
                aspNet.RecordException = true;
            });

        return builder
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());
    }

    /// <summary>
    /// Adds the correlation id response header, the gateway transaction id and process spans for endpoints
    /// with a <see cref="ProcessAttribute"/>. Call it after <c>UseRouting</c> if you call that yourself: the
    /// process middleware reads endpoint metadata.
    /// </summary>
    public static IApplicationBuilder UseObservability(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app
            .UseMiddleware<CorrelationIdMiddleware>()
            .UseMiddleware<GatewayTransactionIdMiddleware>()
            .UseMiddleware<ProcessEndpointMiddleware>();
    }

    internal static bool IsExcluded(PathString path, IReadOnlyList<string> excluded)
    {
        foreach (var prefix in excluded)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static Activity? RequestActivity(HttpContext context) =>
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpActivityFeature>()?.Activity ?? Activity.Current;
}
