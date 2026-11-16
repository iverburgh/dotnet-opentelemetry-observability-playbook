using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Observability.Messaging;
using Observability.Outbox;

namespace Observability.Tests;

public class OutboxTests
{
    private sealed class RecordingSender : IMessageSender
    {
        public List<OutgoingMessage> Sent { get; } = [];

        public HashSet<string> FailFor { get; } = [];

        public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
        {
            if (FailFor.Contains(message.MessageType))
            {
                throw new InvalidOperationException("The broker is unavailable.");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static async Task<(TelemetryHarness Harness, RecordingSender Sender, FakeTimeProvider Time)> StartAsync(string mode = "Parent", string samplingRatio = "1")
    {
        var sender = new RecordingSender();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var harness = await TelemetryHarness.StartAsync(
            new Dictionary<string, string?> { ["Outbox:TraceMode"] = mode, ["Observability:SamplingRatio"] = samplingRatio },
            (services, configuration) =>
            {
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IMessageSender>(sender);
                services.AddOutbox(configuration, runPublisher: false);
            },
            telemetry => telemetry.WithOutboxInstrumentation());
        return (harness, sender, time);
    }

    private static async Task<ActivityTraceId> EnqueueInRequestAsync(TelemetryHarness harness, string messageType = "OrderPlaced")
    {
        using var request = TelemetryHarness.TestSource.StartActivity("POST /orders", ActivityKind.Server)!;
        await harness.Get<OutboxWriter>().EnqueueAsync(messageType, "{}", CancellationToken.None);
        return request.TraceId;
    }

    [Test]
    public async Task In_parent_mode_the_request_publish_and_consumer_share_one_trace()
    {
        var (harness, sender, _) = await StartAsync();
        await using var _ = harness;
        var requestTrace = await EnqueueInRequestAsync(harness);

        await harness.Get<OutboxPublisher>().PublishPendingAsync(CancellationToken.None);

        var run = harness.Span(OutboxTelemetry.PublishRunProcess);
        var publish = harness.Span("publish OrderPlaced");
        MessagingTraceContext.TryExtract(sender.Sent.Single().ApplicationProperties, out var carried);
        using var listener = ProcessorAndMetricsTests.ListenToAll();
        using var consumer = MessagingTraceContext.StartConsumerActivity(TelemetryHarness.TestSource, "process OrderPlaced", sender.Sent.Single().ApplicationProperties)!;
        Assert.Multiple(() =>
        {
            Assert.That(publish.Kind, Is.EqualTo(ActivityKind.Producer));
            Assert.That(publish.TraceId, Is.EqualTo(requestTrace));
            Assert.That(publish.Links.Single().Context.SpanId, Is.EqualTo(run.SpanId), "the publish links to the run that sent it");
            Assert.That(run.TraceId, Is.Not.EqualTo(requestTrace), "each run is its own trace");
            Assert.That(carried.SpanId, Is.EqualTo(publish.SpanId));
            Assert.That(consumer.TraceId, Is.EqualTo(requestTrace), "x-correlation-id of the request finds the consumer");
        });
    }

    [Test]
    public async Task In_link_mode_the_publish_belongs_to_the_run_and_links_to_the_request()
    {
        var (harness, sender, _) = await StartAsync(mode: "Link");
        await using var _ = harness;
        var requestTrace = await EnqueueInRequestAsync(harness);

        await harness.Get<OutboxPublisher>().PublishPendingAsync(CancellationToken.None);

        var run = harness.Span(OutboxTelemetry.PublishRunProcess);
        var publish = harness.Span("publish OrderPlaced");
        Assert.Multiple(() =>
        {
            Assert.That(publish.TraceId, Is.EqualTo(run.TraceId));
            Assert.That(publish.ParentSpanId, Is.EqualTo(run.SpanId));
            Assert.That(publish.Links.Single().Context.TraceId, Is.EqualTo(requestTrace));
            Assert.That(MessagingTraceContext.TryExtract(sender.Sent.Single().ApplicationProperties, out var carried) && carried.TraceId == run.TraceId, Is.True);
        });
    }

    [Test]
    public async Task An_empty_poll_creates_no_trace()
    {
        var (harness, _, _) = await StartAsync();
        await using var _ = harness;

        var published = await harness.Get<OutboxPublisher>().PublishPendingAsync(CancellationToken.None);

        Assert.That(published, Is.Zero);
        Assert.That(harness.Spans, Is.Empty);
    }

    [Test]
    public async Task A_failed_send_fails_its_span_and_the_run_but_not_the_other_messages()
    {
        var (harness, sender, _) = await StartAsync();
        await using var _ = harness;
        sender.FailFor.Add("PaymentRequested");
        await EnqueueInRequestAsync(harness, "OrderPlaced");
        await EnqueueInRequestAsync(harness, "PaymentRequested");

        var published = await harness.Get<OutboxPublisher>().PublishPendingAsync(CancellationToken.None);

        var store = (InMemoryOutboxStore)harness.Get<IOutboxStore>();
        harness.FlushMetrics();
        Assert.Multiple(() =>
        {
            Assert.That(published, Is.EqualTo(1));
            Assert.That(harness.Span("publish PaymentRequested").Status, Is.EqualTo(ActivityStatusCode.Error));
            Assert.That(harness.Span("publish OrderPlaced").Status, Is.EqualTo(ActivityStatusCode.Unset));
            Assert.That(harness.Span(OutboxTelemetry.PublishRunProcess).Status, Is.EqualTo(ActivityStatusCode.Error));
            Assert.That(store.All.Single(m => m.MessageType == "PaymentRequested").FailedAttempts, Is.EqualTo(1));
            Assert.That(store.All.Single(m => m.MessageType == "PaymentRequested").PublishedAt, Is.Null);
            Assert.That(harness.Sum("outbox.messages.failed", (OutboxTelemetry.MessageTypeTag, "PaymentRequested")), Is.EqualTo(1));
            Assert.That(harness.Sum("outbox.messages.published", (OutboxTelemetry.MessageTypeTag, "OrderPlaced")), Is.EqualTo(1));
            Assert.That(harness.Sum("process.completed", (ProcessBaggage.Name, OutboxTelemetry.PublishRunProcess), (Metrics.ProcessMetrics.ResultTag, "error")), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Message_age_is_the_time_from_writing_to_publishing()
    {
        var (harness, _, time) = await StartAsync();
        await using var _ = harness;
        await EnqueueInRequestAsync(harness);
        time.Advance(TimeSpan.FromSeconds(42));

        await harness.Get<OutboxPublisher>().PublishPendingAsync(CancellationToken.None);

        harness.FlushMetrics();
        var age = harness.Metrics.Single(m => m.Name == "outbox.message.age");
        double sum = 0;
        foreach (ref readonly var point in age.GetMetricPoints())
        {
            sum += point.GetHistogramSum();
        }

        Assert.That(sum, Is.EqualTo(42).Within(0.001));
    }

    [Test]
    public async Task A_message_still_carries_the_request_context_when_the_publish_span_is_not_created()
    {
        var sender = new RecordingSender();
        var store = new InMemoryOutboxStore();
        const string requestTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        await store.AddAsync(new OutboxMessage { Id = Guid.NewGuid(), MessageType = "OrderPlaced", Payload = "{}", EnqueuedAt = DateTimeOffset.UtcNow, TraceParent = requestTraceParent }, CancellationToken.None);

        // A host with the outbox but without tracing: nothing listens to the outbox source.
        var services = new ServiceCollection().AddLogging().AddMetrics();
        services.AddOptions<ObservabilityOptions>().Configure(o => o.ServiceName = "svc");
        services.AddSingleton<ProcessTracer>().AddSingleton<IMessageSender>(sender).AddSingleton<IOutboxStore>(store);
        services.AddOutbox(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), runPublisher: false);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<OutboxPublisher>().PublishPendingAsync(CancellationToken.None);

        Assert.That(sender.Sent.Single().ApplicationProperties[MessagingTraceContext.TraceParent], Is.EqualTo(requestTraceParent));
    }
}
