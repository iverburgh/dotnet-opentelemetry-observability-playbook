using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Observability;

/// <summary>
/// Clears the error status of an outgoing HTTP call whose status code is an expected answer — a 409 from a
/// lock that is already taken, a 404 from a lookup that may miss. Without it those calls count as failed
/// dependencies, the failure rate becomes noise and real failures hide in it.
/// </summary>
/// <remarks>Register it before the exporter: processors run in the order they were added.</remarks>
public sealed class ExpectedDependencyStatusProcessor : BaseProcessor<Activity>
{
    public const string ExpectedTag = "dependency.expected_status";

    private readonly ExpectedDependencyResponse[] _rules;

    public ExpectedDependencyStatusProcessor(IOptions<ObservabilityOptions> options)
    {
        _rules = options.Value.ExpectedDependencyResponses ?? [];
    }

    public override void OnEnd(Activity data)
    {
        if (_rules.Length == 0 || data.Kind != ActivityKind.Client || data.Status != ActivityStatusCode.Error)
        {
            return;
        }

        if (!int.TryParse(data.GetTagItem("http.response.status_code")?.ToString(), out var statusCode))
        {
            return;
        }

        var url = data.GetTagItem("url.full")?.ToString();
        if (url is null)
        {
            return;
        }

        foreach (var rule in _rules)
        {
            if (url.Contains(rule.PathContains, StringComparison.OrdinalIgnoreCase) && (rule.StatusCodes ?? []).Contains(statusCode))
            {
                data.SetStatus(ActivityStatusCode.Unset);
                data.SetTag("error.type", null);
                data.SetTag(ExpectedTag, true);
                return;
            }
        }
    }
}
