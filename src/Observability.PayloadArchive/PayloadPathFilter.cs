using Microsoft.AspNetCore.Http;

namespace Observability.PayloadArchive;

/// <summary>Chooses the endpoint, if any, whose payloads are archived for a request path.</summary>
internal static class PayloadPathFilter
{
    /// <summary>
    /// The first endpoint whose prefix matches and none of whose excluded segments occur below it. Matching is
    /// per segment: a plain string prefix would let <c>/api/orders</c> match <c>/api/orders-report</c> and
    /// archive the wrong traffic into the wrong container. An excluded segment is final: a later endpoint, such
    /// as a catch-all <c>/</c>, does not pick the path up.
    /// </summary>
    public static PayloadArchiveEndpoint? Match(IReadOnlyList<PayloadArchiveEndpoint> endpoints, PathString path)
    {
        foreach (var endpoint in endpoints)
        {
            if (TryMatchPrefix(path, endpoint.PathPrefix, out var remaining))
            {
                return IsExcluded(remaining, endpoint.ExcludedPathSegments) ? null : endpoint;
            }
        }

        return null;
    }

    /// <summary>Whether every path that <paramref name="other"/> matches is already matched by <paramref name="prefix"/>.</summary>
    public static bool Covers(string prefix, string other) => TryMatchPrefix(new PathString(other), prefix, out _);

    private static bool TryMatchPrefix(PathString path, string prefix, out PathString remaining)
    {
        if (prefix == "/")
        {
            remaining = path;
            return true;
        }

        return path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase, out remaining);
    }

    private static bool IsExcluded(PathString remaining, string[]? excludedSegments)
    {
        if (excludedSegments is not { Length: > 0 } || !remaining.HasValue)
        {
            return false;
        }

        foreach (var segment in remaining.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (excludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
