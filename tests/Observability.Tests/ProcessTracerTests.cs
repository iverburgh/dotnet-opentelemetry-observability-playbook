using System.Diagnostics;

namespace Observability.Tests;

public class ProcessTracerTests
{
    [Test]
    public async Task A_nested_process_carries_name_breadcrumb_origin_and_domain()
    {
        await using var harness = await TelemetryHarness.StartAsync();
        var tracer = harness.Get<ProcessTracer>();

        using (tracer.StartProcess("Place order", step: "Validate"))
        {
            using var inner = tracer.StartProcess("Reserve stock");
            Assert.Multiple(() =>
            {
                Assert.That(inner!.GetBaggageItem(ProcessBaggage.Name), Is.EqualTo("Reserve stock"));
                Assert.That(inner.GetBaggageItem(ProcessBaggage.Breadcrumb), Is.EqualTo("Place order > Reserve stock"));
                Assert.That(inner.GetBaggageItem(ProcessBaggage.Origin), Is.EqualTo($"{TelemetryHarness.ServiceName}/Place order"));
                Assert.That(inner.GetBaggageItem(ProcessBaggage.Domain), Is.EqualTo("ordering"));
                Assert.That(inner.GetBaggageItem(ProcessBaggage.Step), Is.Empty, "a step of the outer process does not apply to the inner one");
            });
        }

        var exported = harness.Span("Reserve stock");
        Assert.Multiple(() =>
        {
            Assert.That(exported.GetTagItem(ProcessBaggage.Name), Is.EqualTo("Reserve stock"));
            Assert.That(exported.GetTagItem(ProcessBaggage.Breadcrumb), Is.EqualTo("Place order > Reserve stock"), "baggage becomes a filterable column");
            Assert.That(exported.GetTagItem(ProcessBaggage.Step), Is.Null, "empty baggage is not copied");
            Assert.That(ProcessTracer.IsProcess(exported), Is.True);
        });
    }

    [Test]
    public async Task Extra_baggage_keys_are_lower_cased_and_tagged()
    {
        await using var harness = await TelemetryHarness.StartAsync();

        using (var process = harness.Get<ProcessTracer>().StartProcess("Import file", baggage: [new("Tenant.Id", "t-42")]))
        {
            Assert.That(process!.GetBaggageItem("tenant.id"), Is.EqualTo("t-42"));
        }

        Assert.That(harness.Span("Import file").GetTagItem("tenant.id"), Is.EqualTo("t-42"));
    }

    [Test]
    public void A_long_breadcrumb_keeps_its_most_recent_part()
    {
        var parent = string.Join(ProcessBaggage.BreadcrumbSeparator, Enumerable.Repeat("Some process", 40));

        var breadcrumb = ProcessTracer.AppendBreadcrumb(parent, "Last");

        Assert.Multiple(() =>
        {
            Assert.That(breadcrumb, Has.Length.EqualTo(ProcessBaggage.MaxBreadcrumbLength));
            Assert.That(breadcrumb, Does.StartWith("…").And.EndWith(" > Last"));
        });
    }

    [Test]
    public async Task A_root_process_starts_a_new_trace_linked_to_the_current_span_and_restores_it()
    {
        await using var harness = await TelemetryHarness.StartAsync();
        var tracer = harness.Get<ProcessTracer>();

        using var loop = TelemetryHarness.TestSource.StartActivity("poll loop");
        using (var scope = tracer.StartRootProcess("Publish outbox messages"))
        {
            var run = scope.Process;
            Assert.Multiple(() =>
            {
                Assert.That(run!.TraceId, Is.Not.EqualTo(loop!.TraceId));
                Assert.That(run.ParentSpanId, Is.EqualTo(default(ActivitySpanId)));
                Assert.That(run.Links.Single().Context.SpanId, Is.EqualTo(loop.SpanId));
                Assert.That(run.GetBaggageItem(ProcessBaggage.Breadcrumb), Is.EqualTo("Publish outbox messages"));
            });
        }

        Assert.That(Activity.Current, Is.SameAs(loop), "stopping the root process restores the previous span");
    }

    [Test]
    public async Task SetResult_and_Fail_apply_to_the_nearest_process_not_to_the_current_span()
    {
        await using var harness = await TelemetryHarness.StartAsync();

        using (harness.Get<ProcessTracer>().StartProcess("Place order"))
        {
            using var child = TelemetryHarness.TestSource.StartActivity("save");
            ProcessTracer.Fail(new InvalidOperationException("out of stock"));
        }

        var process = harness.Span("Place order");
        Assert.Multiple(() =>
        {
            Assert.That(process.Status, Is.EqualTo(ActivityStatusCode.Error));
            Assert.That(process.StatusDescription, Is.EqualTo("out of stock"));
            Assert.That(process.Events.Single().Name, Is.EqualTo("exception"));
            Assert.That(harness.Span("save").Status, Is.EqualTo(ActivityStatusCode.Unset));
        });
    }

    [Test]
    public void Without_a_process_SetResult_does_nothing()
    {
        Assert.That(() => ProcessTracer.SetResult(ActivityStatusCode.Error), Throws.Nothing);
        Assert.That(ProcessTracer.CurrentProcess, Is.Null);
    }

    [TestCase(200, ActivityStatusCode.Unset)]
    [TestCase(401, ActivityStatusCode.Unset)]
    [TestCase(404, ActivityStatusCode.Unset)]
    [TestCase(409, ActivityStatusCode.Error)]
    [TestCase(500, ActivityStatusCode.Error)]
    public void An_HTTP_status_fails_a_process_unless_it_is_listed(int statusCode, ActivityStatusCode expected) =>
        Assert.That(ProcessTracer.StatusFromHttp(statusCode, [401, 404]), Is.EqualTo(expected));

    [Process("Class level")]
    private sealed class Handlers
    {
        public void Inherits() { }

        [Process("Method level", Step = "Check")]
        public void Overrides() { }
    }

    [Test]
    public void The_method_attribute_wins_over_the_class_attribute()
    {
        var inherits = ProcessAttribute.Resolve(typeof(Handlers).GetMethod(nameof(Handlers.Inherits))!);
        var overrides = ProcessAttribute.Resolve(typeof(Handlers).GetMethod(nameof(Handlers.Overrides))!);

        Assert.Multiple(() =>
        {
            Assert.That(inherits?.Name, Is.EqualTo("Class level"));
            Assert.That(overrides?.Name, Is.EqualTo("Method level"));
            Assert.That(overrides?.Step, Is.EqualTo("Check"));
        });
    }
}
