using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Observability.Functions;

public static class FunctionsObservabilityExtensions
{
    /// <summary>
    /// Adds the worker middleware: Service Bus trace context first, so the process span lands in the
    /// producer's trace, then process spans for functions with a <see cref="ProcessAttribute"/>.
    /// </summary>
    public static IFunctionsWorkerApplicationBuilder UseObservability(this IFunctionsWorkerApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<ServiceBusTraceContextMiddleware>();
        builder.Services.TryAddSingleton<ProcessFunctionMiddleware>();
        Use<ServiceBusTraceContextMiddleware>(builder);
        Use<ProcessFunctionMiddleware>(builder);
        return builder;
    }

    // UseMiddleware<T> lives in the full worker package; Use is all this library needs from Worker.Core.
    private static void Use<TMiddleware>(IFunctionsWorkerApplicationBuilder builder)
        where TMiddleware : IFunctionsWorkerMiddleware =>
        builder.Use(next => context => context.InstanceServices.GetRequiredService<TMiddleware>().Invoke(context, next));
}
