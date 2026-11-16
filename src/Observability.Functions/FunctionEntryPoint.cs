using System.Collections.Concurrent;
using System.Reflection;

namespace Observability.Functions;

/// <summary>
/// Finds the <see cref="ProcessAttribute"/> of a function from its entry point, <c>Namespace.Class.Method</c>.
/// </summary>
internal static class FunctionEntryPoint
{
    private static readonly ConcurrentDictionary<string, ProcessAttribute?> Cache = new(StringComparer.Ordinal);

    public static ProcessAttribute? ResolveProcess(string entryPoint) =>
        Cache.GetOrAdd(entryPoint, static e => Resolve(e, AppDomain.CurrentDomain.GetAssemblies()));

    internal static ProcessAttribute? Resolve(string entryPoint, IEnumerable<Assembly> assemblies)
    {
        var separator = entryPoint.LastIndexOf('.');
        if (separator <= 0 || separator == entryPoint.Length - 1)
        {
            return null;
        }

        var typeName = entryPoint[..separator];
        var methodName = entryPoint[(separator + 1)..];
        foreach (var assembly in assemblies)
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            var type = assembly.GetType(typeName, throwOnError: false);
            var method = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == methodName);
            if (method is not null)
            {
                return ProcessAttribute.Resolve(method);
            }
        }

        return null;
    }
}
