namespace Observability;

/// <summary>
/// Baggage keys that describe the business process a span belongs to. Baggage travels with the trace to
/// every child span and, over HTTP, to every internal service; <see cref="BaggageToTagsProcessor"/> copies
/// it onto each span so it becomes a searchable column in your backend.
/// </summary>
public static class ProcessBaggage
{
    /// <summary>The innermost process, for example <c>Place order</c>.</summary>
    public const string Name = "process.name";

    /// <summary>An optional step within the process, for example <c>Validate</c>.</summary>
    public const string Step = "process.step";

    /// <summary>Every process on the way here, outermost first: <c>Place order &gt; Reserve stock</c>.</summary>
    public const string Breadcrumb = "process.breadcrumb";

    /// <summary>The service and process that started the trace. Set once, never overwritten.</summary>
    public const string Origin = "process.origin";

    /// <summary>The domain (bounded context) of the service that runs the innermost process.</summary>
    public const string Domain = "process.domain";

    /// <summary>The subdomain of the service that runs the innermost process.</summary>
    public const string Subdomain = "process.subdomain";

    /// <summary>A breadcrumb is sent in a header on every internal call; beyond this it is trimmed from the left.</summary>
    public const int MaxBreadcrumbLength = 256;

    internal const string BreadcrumbSeparator = " > ";

    /// <summary>All keys above. These are always copied onto spans.</summary>
    public static IReadOnlyList<string> All { get; } = [Name, Step, Breadcrumb, Origin, Domain, Subdomain];
}
