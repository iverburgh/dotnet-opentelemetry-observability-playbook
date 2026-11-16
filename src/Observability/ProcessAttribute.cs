using System.Reflection;

namespace Observability;

/// <summary>
/// Declares the business process an endpoint, function or handler implements. The host middleware reads it
/// and starts a process span around the call, so the process shows up in traces, logs and metrics without
/// any code in the method itself.
/// </summary>
/// <remarks>
/// Use a functional name that a product owner would recognise, and keep it constant: it becomes a metric
/// dimension, and a name with an ID in it creates a new time series per call.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class ProcessAttribute(string name) : Attribute
{
    /// <summary>The process name, for example <c>Place order</c>.</summary>
    public string Name { get; } = name;

    /// <summary>An optional step within the process, for example <c>Validate</c>.</summary>
    public string? Step { get; set; }

    /// <summary>The attribute on the method, or else the one on its class. The method wins.</summary>
    public static ProcessAttribute? Resolve(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.GetCustomAttribute<ProcessAttribute>() ?? method.DeclaringType?.GetCustomAttribute<ProcessAttribute>();
    }
}
