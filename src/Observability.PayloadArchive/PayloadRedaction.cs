namespace Observability.PayloadArchive;

/// <summary>
/// Keeps credentials out of the archive. Values are replaced, not removed: "an Authorization header was
/// sent" is often exactly what you are looking for when a call fails with 401.
/// </summary>
internal static class PayloadRedaction
{
    public const string Redacted = "[redacted]";

    /// <summary>All headers, one entry per name; repeated values are joined with ", " as HTTP allows.</summary>
    public static Dictionary<string, string> Headers<TValues>(IEnumerable<KeyValuePair<string, TValues>> headers, IReadOnlySet<string> redacted)
        where TValues : IEnumerable<string?>
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            var value = redacted.Contains(name) ? Redacted : string.Join(", ", values);
            result[name] = result.TryGetValue(name, out var existing) && !redacted.Contains(name) ? $"{existing}, {value}" : value;
        }

        return result;
    }

    /// <summary>The URL with the values of sensitive query parameters replaced. The fragment is never sent, so it is not kept.</summary>
    public static string Url(string url, IReadOnlySet<string> redactedParameters)
    {
        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return url;
        }

        var fragmentStart = url.IndexOf('#', queryStart);
        var query = fragmentStart < 0 ? url[(queryStart + 1)..] : url[(queryStart + 1)..fragmentStart];
        var parameters = query.Split('&');
        for (var i = 0; i < parameters.Length; i++)
        {
            var separator = parameters[i].IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? parameters[i] : parameters[i][..separator];
            if (redactedParameters.Contains(Uri.UnescapeDataString(name.Replace('+', ' '))))
            {
                parameters[i] = $"{name}={Redacted}";
            }
        }

        return $"{url[..queryStart]}?{string.Join('&', parameters)}";
    }
}
