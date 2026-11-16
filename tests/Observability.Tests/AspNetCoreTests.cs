using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Observability.AspNetCore;
using Observability.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Observability.Tests;

public class AspNetCoreTests
{
    private const string GatewayHeader = "x-appgw-trace-id";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly List<Activity> _spans = [];
    private readonly List<Metric> _metrics = [];

    [OneTimeSetUp]
    public async Task StartApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(TelemetryHarness.DefaultSettings());
        builder.Services.AddObservability(builder.Configuration)
            .WithAspNetCoreObservability()
            .WithTracing(t => t.AddInMemoryExporter(_spans))
            .WithMetrics(m => m.AddInMemoryExporter(_metrics));
        builder.Services.AddProblemDetails();

        _app = builder.Build();
        _app.UseExceptionHandler();
        _app.UseObservability();
        _app.MapGet("/health", () => "healthy");
        _app.MapPost("/orders", () => Results.Accepted()).WithMetadata(new ProcessAttribute("Place order"));
        _app.MapGet("/orders/{id}", (string id) => Results.NotFound()).WithMetadata(new ProcessAttribute("Look up order"));
        _app.MapPost("/orders/{id}/cancel", (string id) => Results.Conflict()).WithMetadata(new ProcessAttribute("Cancel order"));
        _app.MapPost("/payments", IResult () => throw new InvalidOperationException("declined")).WithMetadata(new ProcessAttribute("Take payment"));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    [OneTimeTearDown]
    public async Task StopApp()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [SetUp]
    public void Clear() => _spans.Clear();

    private Activity ServerSpan(string route) => _spans.Single(s => s.Kind == ActivityKind.Server && (string?)s.GetTagItem("http.route") == route);

    [Test]
    public async Task Every_response_carries_the_trace_id_as_correlation_id_and_ignores_an_inbound_one()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "chosen-by-the-caller");

        using var response = await _client.SendAsync(request);

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.Multiple(() =>
        {
            Assert.That(correlationId, Is.EqualTo(ServerSpan("/orders").TraceId.ToHexString()));
            Assert.That(correlationId, Is.Not.EqualTo("chosen-by-the-caller"));
        });
    }

    [Test]
    public async Task An_inbound_traceparent_becomes_the_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        using var response = await _client.SendAsync(request);

        Assert.That(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single(), Is.EqualTo("0af7651916cd43dd8448eb211c80319c"));
    }

    [Test]
    public async Task An_unhandled_exception_fails_the_process_and_the_500_still_has_a_correlation_id()
    {
        using var response = await _client.PostAsync("/payments", null);

        var process = _spans.Single(s => s.DisplayName == "Take payment");
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single(), Is.EqualTo(process.TraceId.ToHexString()));
            Assert.That(process.Status, Is.EqualTo(ActivityStatusCode.Error));
            Assert.That(process.StatusDescription, Is.EqualTo("declined"));
        });
    }

    [Test]
    public async Task A_404_leaves_the_process_ok_and_a_409_fails_it()
    {
        using var notFound = await _client.GetAsync("/orders/1");
        using var conflict = await _client.PostAsync("/orders/1/cancel", null);

        var lookUp = _spans.Single(s => s.DisplayName == "Look up order");
        var cancel = _spans.Single(s => s.DisplayName == "Cancel order");
        Assert.Multiple(() =>
        {
            Assert.That(lookUp.Status, Is.EqualTo(ActivityStatusCode.Unset));
            Assert.That(lookUp.ParentSpanId, Is.EqualTo(ServerSpan("/orders/{id}").SpanId), "the process is a child of the request span");
            Assert.That(cancel.Status, Is.EqualTo(ActivityStatusCode.Error));
            Assert.That(cancel.StatusDescription, Is.EqualTo("HTTP 409"));
        });
    }

    [Test]
    public async Task Process_metrics_count_requests_by_process_and_result()
    {
        using var ok = await _client.PostAsync("/orders", null);
        using var failed = await _client.PostAsync("/orders/2/cancel", null);

        _metrics.Clear();
        _app.Services.GetRequiredService<MeterProvider>().ForceFlush();

        Assert.Multiple(() =>
        {
            Assert.That(Count("Place order", "ok"), Is.GreaterThanOrEqualTo(1));
            Assert.That(Count("Cancel order", "error"), Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public async Task A_health_probe_is_not_traced()
    {
        using var response = await _client.GetAsync("/health");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(_spans, Is.Empty);
    }

    [Test]
    public async Task The_gateway_transaction_id_is_tagged_on_the_request_span()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders");
        request.Headers.Add(GatewayHeader,         "00000000-0000-0000-0000-00000000ABCD");

        using var response = await _client.SendAsync(request);

                Assert.That(ServerSpan("/orders").GetTagItem(GatewayTransactionIdMiddleware.Tag), Is.EqualTo("0000000000000000000000000000abcd"));
    }

    [TestCase("ABCDEF-0123", "abcdef-0123")]
    [TestCase("<script>", null)]
    [TestCase("", null)]
    public void A_gateway_transaction_id_is_normalised_or_rejected(string value, string? expected) =>
        Assert.That(GatewayTransactionIdMiddleware.Normalise(value), Is.EqualTo(expected));

    private long Count(string process, string result)
    {
        long total = 0;
        foreach (var metric in _metrics.Where(m => m.Name == "process.completed"))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, object?>();
                foreach (var tag in point.Tags)
                {
                    tags[tag.Key] = tag.Value;
                }

                if (Equals(tags[ProcessBaggage.Name], process) && Equals(tags[ProcessMetrics.ResultTag], result))
                {
                    total += point.GetSumLong();
                }
            }
        }

        return total;
    }
}
