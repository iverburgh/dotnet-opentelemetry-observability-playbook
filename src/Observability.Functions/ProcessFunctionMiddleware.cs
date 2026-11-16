using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace Observability.Functions;

/// <summary>
/// Runs a function inside a process span when the function method, or its class, has a <see cref="ProcessAttribute"/>.
/// An exception fails the process and is rethrown, so the host's retry and dead-letter behaviour stay the same.
/// </summary>
internal sealed class ProcessFunctionMiddleware(ProcessTracer tracer) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var process = FunctionEntryPoint.ResolveProcess(context.FunctionDefinition.EntryPoint);
        if (process is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        using var activity = tracer.StartProcess(process.Name, process.Step);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProcessTracer.Fail(activity, ex);
            throw;
        }
    }
}
