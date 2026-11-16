using System.Diagnostics;

namespace Observability;

/// <summary>
/// A process span in a new trace. Stopping a span makes its parent current; a root span has no parent, so
/// stopping it alone would leave no current span at all. Disposing this scope ends the process and restores
/// the span that was current when it started.
/// </summary>
public sealed class RootProcessScope : IDisposable
{
    private readonly Activity? _previous;

    internal RootProcessScope(Activity? process, Activity? previous)
    {
        Process = process;
        _previous = previous;
    }

    /// <summary>The process span, or <c>null</c> when nothing listens to processes.</summary>
    public Activity? Process { get; }

    public void Dispose()
    {
        if (Process is null)
        {
            return;
        }

        Process.Dispose();
        if (Activity.Current is null)
        {
            Activity.Current = _previous;
        }
    }
}
