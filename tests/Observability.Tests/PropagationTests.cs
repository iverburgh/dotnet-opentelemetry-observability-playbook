using System.Diagnostics;
using Observability.Messaging;

namespace Observability.Tests;

public class PropagationTests
{
    private static Dictionary<string, string> InjectInto(DistributedContextPropagator propagator, string url)
    {
        using var listener = ProcessorAndMetricsTests.ListenToAll();
        using var source = new ActivitySource("Propagation");
        using var activity = source.StartActivity("call", ActivityKind.Client)!;
        activity.SetBaggage("process.name", "Place order");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        propagator.Inject(activity, request, (_, name, value) => headers[name] = value);
        return headers;
    }

    [TestCase("https://api.partner.example/v1/rates", false)]
    [TestCase("https://orders.internal.example/orders", true)]
    [TestCase("https://billing", true)]
    public void Baggage_goes_to_internal_hosts_only_and_traceparent_goes_everywhere(string url, bool expectBaggage)
    {
        var propagator = new ScopedBaggagePropagator(DistributedContextPropagator.CreateW3CPropagator(), [".internal.example", "billing"]);

        var headers = InjectInto(propagator, url);

        Assert.Multiple(() =>
        {
            Assert.That(headers, Contains.Key("traceparent"));
            Assert.That(headers.ContainsKey("baggage"), Is.EqualTo(expectBaggage));
        });
    }

    [Test]
    public void A_wildcard_allows_every_host_and_no_list_allows_none()
    {
        var everyone = new ScopedBaggagePropagator(DistributedContextPropagator.CreateW3CPropagator(), ["*"]);
        var nobody = new ScopedBaggagePropagator(DistributedContextPropagator.CreateW3CPropagator(), []);

        Assert.Multiple(() =>
        {
            Assert.That(InjectInto(everyone, "https://api.partner.example/"), Contains.Key("baggage"));
            Assert.That(InjectInto(nobody, "https://orders.internal.example/"), Does.Not.ContainKey("baggage"));
            Assert.That(nobody.MayReceiveBaggage(new Dictionary<string, object?>()), Is.True, "only HTTP requests leave the process this way");
        });
    }

    [Test]
    public async Task The_host_installs_the_scoped_propagator_and_restores_the_previous_one()
    {
        var before = DistributedContextPropagator.Current;

        var harness = await TelemetryHarness.StartAsync();
        Assert.That(DistributedContextPropagator.Current, Is.TypeOf<ScopedBaggagePropagator>());
        await harness.DisposeAsync();

        Assert.That(DistributedContextPropagator.Current, Is.SameAs(before));
    }

    [Test]
    public void Trace_context_survives_a_round_trip_through_message_properties()
    {
        using var listener = ProcessorAndMetricsTests.ListenToAll();
        using var source = new ActivitySource("Messaging");
        using var producer = source.StartActivity("publish", ActivityKind.Producer)!;
        producer.TraceStateString = "vendor=1";
        var properties = new Dictionary<string, object?>();

        MessagingTraceContext.Inject(producer, properties);
        var found = MessagingTraceContext.TryExtract(properties, out var context);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(context.TraceId, Is.EqualTo(producer.TraceId));
            Assert.That(context.SpanId, Is.EqualTo(producer.SpanId));
            Assert.That(context.TraceState, Is.EqualTo("vendor=1"));
            Assert.That(context.IsRemote, Is.True);
        });
    }

    [Test]
    public void Diagnostic_Id_is_read_when_traceparent_is_missing()
    {
        const string diagnosticId = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var properties = new Dictionary<string, object?> { [MessagingTraceContext.DiagnosticId] = diagnosticId };

        Assert.That(MessagingTraceContext.TryExtract(properties, out var context), Is.True);
        Assert.That(context.TraceId.ToHexString(), Is.EqualTo("0af7651916cd43dd8448eb211c80319c"));
    }

    [Test]
    public void A_consumer_span_joins_the_producer_trace_and_links_to_the_trigger()
    {
        using var listener = ProcessorAndMetricsTests.ListenToAll();
        using var source = new ActivitySource("Messaging");
        const string upstream = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        using var trigger = source.StartActivity("trigger")!;
        using var consumer = MessagingTraceContext.StartConsumerActivity(source, "process OrderPlaced", new Dictionary<string, object?> { ["traceparent"] = upstream })!;

        Assert.Multiple(() =>
        {
            Assert.That(consumer.Kind, Is.EqualTo(ActivityKind.Consumer));
            Assert.That(consumer.TraceId.ToHexString(), Is.EqualTo("0af7651916cd43dd8448eb211c80319c"));
            Assert.That(consumer.ParentSpanId.ToHexString(), Is.EqualTo("b7ad6b7169203331"));
            Assert.That(consumer.Links.Single().Context.SpanId, Is.EqualTo(trigger.SpanId));
            Assert.That(consumer.GetTagItem(MessagingTraceContext.ReparentedTag), Is.EqualTo(true));
        });
    }

    [Test]
    public void A_consumer_span_in_the_same_trace_is_an_ordinary_child()
    {
        using var listener = ProcessorAndMetricsTests.ListenToAll();
        using var source = new ActivitySource("Messaging");
        using var trigger = source.StartActivity("trigger")!;
        var properties = new Dictionary<string, object?>();
        MessagingTraceContext.Inject(trigger, properties);

        using var consumer = MessagingTraceContext.StartConsumerActivity(source, "process", properties)!;

        Assert.Multiple(() =>
        {
            Assert.That(consumer.ParentSpanId, Is.EqualTo(trigger.SpanId));
            Assert.That(consumer.Links, Is.Empty);
            Assert.That(consumer.GetTagItem(MessagingTraceContext.ReparentedTag), Is.Null);
        });
    }
}
