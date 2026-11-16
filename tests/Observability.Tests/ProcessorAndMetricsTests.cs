using System.Diagnostics;
using Microsoft.Extensions.Options;
using Observability.Metrics;

namespace Observability.Tests;

public class ProcessorAndMetricsTests
{
    [Test]
    public async Task Only_allow_listed_baggage_reaches_the_spans()
    {
        await using var harness = await TelemetryHarness.StartAsync(new Dictionary<string, string?> { ["Observability:BaggageTagKeys:0"] = "tenant.id" });

        using (var request = TelemetryHarness.TestSource.StartActivity("request"))
        {
            request!.SetBaggage("tenant.id", "t-1").SetBaggage("user.email", "someone@example.com");
            using var child = TelemetryHarness.TestSource.StartActivity("child");
            child!.SetBaggage("tenant.id", "t-2");
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Span("child").GetTagItem("tenant.id"), Is.EqualTo("t-2"), "the nearest value wins");
            Assert.That(harness.Span("request").GetTagItem("tenant.id"), Is.EqualTo("t-1"));
            Assert.That(harness.Spans.Any(s => s.GetTagItem("user.email") is not null), Is.False);
        });
    }

    [Test]
    public async Task Process_metrics_stay_exact_when_every_trace_is_sampled_out()
    {
        await using var harness = await TelemetryHarness.StartAsync(new Dictionary<string, string?> { ["Observability:SamplingRatio"] = "0" });
        var tracer = harness.Get<ProcessTracer>();

        for (var i = 0; i < 3; i++)
        {
            using var ok = tracer.StartProcess("Place order");
        }

        using (var failed = tracer.StartProcess("Place order"))
        {
            Assert.That(failed, Is.Not.Null, "the span exists for context and baggage even though it is not recorded");
            Assert.That(failed!.Recorded, Is.False);
            failed.SetStatus(ActivityStatusCode.Error);
        }

        harness.FlushMetrics();
        Assert.Multiple(() =>
        {
            Assert.That(harness.Spans, Is.Empty);
            Assert.That(harness.Sum("process.completed", (ProcessBaggage.Name, "Place order"), (ProcessMetrics.ResultTag, "ok")), Is.EqualTo(3));
            Assert.That(harness.Sum("process.completed", (ProcessBaggage.Name, "Place order"), (ProcessMetrics.ResultTag, "error")), Is.EqualTo(1));
            Assert.That(harness.Metrics.Any(m => m.Name == "process.duration"), Is.True);
        });
    }

    private sealed class CheckoutMetrics : MetricsBundle
    {
        private readonly System.Diagnostics.Metrics.Counter<long> _checkouts;

        public CheckoutMetrics(System.Diagnostics.Metrics.IMeterFactory meterFactory, IOptions<ObservabilityOptions> options)
            : base(meterFactory, options) => _checkouts = Meter.CreateCounter<long>("checkout.completed");

        public void Completed() => _checkouts.Add(1);
    }

    [Test]
    public async Task A_new_metrics_bundle_is_exported_without_adding_its_meter()
    {
        await using var harness = await TelemetryHarness.StartAsync(services: (s, _) => s.AddMetricsBundle<CheckoutMetrics>());
        var bundle = harness.Get<CheckoutMetrics>();

        bundle.Completed();
        harness.FlushMetrics();

        Assert.Multiple(() =>
        {
            Assert.That(bundle.MeterName, Is.EqualTo($"{TelemetryHarness.ServiceName}.checkoutmetrics"));
            Assert.That(harness.Sum("checkout.completed"), Is.EqualTo(1));
        });
    }

    [Test]
    public void An_expected_lock_conflict_is_not_a_failed_dependency()
    {
        var processor = ExpectedProcessor();
        using var listener = ListenToAll();
        using var source = new ActivitySource("Expected");

        using var conflict = Client(source, "https://locks.internal.example/locks/order-1", 409);
        using var otherPath = Client(source, "https://orders.internal.example/orders/1", 409);
        using var server = source.StartActivity("server", ActivityKind.Server)!;
        server.SetTag("http.response.status_code", 409).SetTag("url.full", "https://x/locks/1").SetStatus(ActivityStatusCode.Error);

        processor.OnEnd(conflict);
        processor.OnEnd(otherPath);
        processor.OnEnd(server);

        Assert.Multiple(() =>
        {
            Assert.That(conflict.Status, Is.EqualTo(ActivityStatusCode.Unset));
            Assert.That(conflict.GetTagItem(ExpectedDependencyStatusProcessor.ExpectedTag), Is.EqualTo(true));
            Assert.That(conflict.GetTagItem("error.type"), Is.Null);
            Assert.That(otherPath.Status, Is.EqualTo(ActivityStatusCode.Error));
            Assert.That(server.Status, Is.EqualTo(ActivityStatusCode.Error), "only outgoing calls are dependencies");
        });
    }

    [Test]
    public void The_Azure_SDK_HTTP_span_is_recognised_as_the_owner_of_an_HttpClient_call()
    {
        using var listener = ListenToAll();
        using var azure = new ActivitySource(ObservabilityServiceCollectionExtensions.AzureCoreHttpSourceName);
        using var http = new ActivitySource("System.Net.Http");

        using (var sdkSpan = azure.StartActivity("PUT"))
        {
            using var httpSpan = http.StartActivity("PUT", ActivityKind.Client);
            Assert.That(ObservabilityServiceCollectionExtensions.IsAzureSdkCall(httpSpan), Is.True);
        }

        using var plain = http.StartActivity("GET", ActivityKind.Client);
        Assert.That(ObservabilityServiceCollectionExtensions.IsAzureSdkCall(plain), Is.False);
    }

    private static ExpectedDependencyStatusProcessor ExpectedProcessor() =>
        new(Options.Create(new ObservabilityOptions
        {
            ServiceName = "svc",
            ExpectedDependencyResponses = [new ExpectedDependencyResponse { PathContains = "/locks/", StatusCodes = [404, 409] }],
        }));

    private static Activity Client(ActivitySource source, string url, int statusCode)
    {
        var activity = source.StartActivity("call", ActivityKind.Client)!;
        activity.SetTag("url.full", url).SetTag("http.response.status_code", statusCode).SetTag("error.type", statusCode.ToString());
        activity.SetStatus(ActivityStatusCode.Error);
        return activity;
    }

    internal static ActivityListener ListenToAll()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
