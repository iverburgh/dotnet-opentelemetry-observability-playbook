using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Observability.Functions;

namespace Observability.Tests;

public class FunctionsTests
{
    [Test]
    public void Application_properties_are_read_from_JSON_or_a_dictionary_and_bad_input_gives_none()
    {
        var fromJson = ServiceBusApplicationProperties.Parse("""{"traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01","Retries":3}""");
        var fromDictionary = ServiceBusApplicationProperties.Parse(new Dictionary<string, object?> { ["a"] = "b" });

        Assert.Multiple(() =>
        {
            Assert.That(fromJson["TraceParent"], Is.EqualTo("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"), "keys are case-insensitive");
            Assert.That(fromJson["Retries"], Is.EqualTo("3"));
            Assert.That(fromDictionary["a"], Is.EqualTo("b"));
            Assert.That(ServiceBusApplicationProperties.Parse("not json"), Is.Empty);
            Assert.That(ServiceBusApplicationProperties.Parse("[1]"), Is.Empty);
            Assert.That(ServiceBusApplicationProperties.Parse(42), Is.Empty);
        });
    }

    [Process("Import orders")]
    public sealed class ImportFunction
    {
        public void Run() { }

        [Process("Retry import", Step = "Retry")]
        public void Retry() { }
    }

    [Test]
    public void A_function_entry_point_resolves_to_its_process()
    {
        var assemblies = new[] { typeof(FunctionsTests).Assembly };
        var type = typeof(ImportFunction).FullName!;

        Assert.Multiple(() =>
        {
            Assert.That(FunctionEntryPoint.Resolve($"{type}.Run", assemblies)?.Name, Is.EqualTo("Import orders"));
            Assert.That(FunctionEntryPoint.Resolve($"{type}.Retry", assemblies)?.Step, Is.EqualTo("Retry"));
            Assert.That(FunctionEntryPoint.Resolve($"{type}.Missing", assemblies), Is.Null);
            Assert.That(FunctionEntryPoint.Resolve("NoSeparator", assemblies), Is.Null);
        });
    }

    [Test]
    public async Task A_Service_Bus_invocation_joins_the_message_trace_and_runs_its_process()
    {
        await using var harness = await TelemetryHarness.StartAsync();
        const string upstream = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var context = new FakeFunctionContext(
            $"{typeof(ImportFunction).FullName}.Run",
            ServiceBusTraceContextMiddleware.TriggerType,
            new Dictionary<string, object?> { [ServiceBusApplicationProperties.BindingDataKey] = $$"""{"traceparent":"{{upstream}}"}""" });
        var serviceBus = new ServiceBusTraceContextMiddleware();
        var process = new ProcessFunctionMiddleware(harness.Get<ProcessTracer>());

        await serviceBus.Invoke(context, ctx => process.Invoke(ctx, _ => Task.CompletedTask));

        var consumer = harness.Span("process ImportOrders");
        var importSpan = harness.Span("Import orders");
        Assert.Multiple(() =>
        {
            Assert.That(consumer.Kind, Is.EqualTo(ActivityKind.Consumer));
            Assert.That(consumer.TraceId.ToHexString(), Is.EqualTo("0af7651916cd43dd8448eb211c80319c"));
            Assert.That(importSpan.ParentSpanId, Is.EqualTo(consumer.SpanId));
        });
    }

    [Test]
    public async Task A_failing_function_fails_its_process_and_the_exception_reaches_the_host()
    {
        await using var harness = await TelemetryHarness.StartAsync();
        var context = new FakeFunctionContext($"{typeof(ImportFunction).FullName}.Run", "timerTrigger", new Dictionary<string, object?>());
        var process = new ProcessFunctionMiddleware(harness.Get<ProcessTracer>());

        Assert.That(() => process.Invoke(context, _ => throw new TimeoutException("store unavailable")), Throws.TypeOf<TimeoutException>());
        Assert.That(harness.Span("Import orders").Status, Is.EqualTo(ActivityStatusCode.Error));
        Assert.That(ServiceBusTraceContextMiddleware.IsServiceBusTriggered(context), Is.False);
    }

    private sealed class FakeFunctionContext(string entryPoint, string triggerType, IReadOnlyDictionary<string, object?> bindingData) : FunctionContext
    {
        public override string InvocationId => "invocation-1";

        public override string FunctionId => "function-1";

        public override TraceContext TraceContext => throw new NotSupportedException();

        public override BindingContext BindingContext { get; } = new FakeBindingContext(bindingData);

        public override RetryContext RetryContext => throw new NotSupportedException();

        public override IServiceProvider InstanceServices { get; set; } = new ServiceCollection().BuildServiceProvider();

        public override FunctionDefinition FunctionDefinition { get; } = new FakeFunctionDefinition(entryPoint, triggerType);

        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

        public override IInvocationFeatures Features => throw new NotSupportedException();
    }

    private sealed class FakeBindingContext(IReadOnlyDictionary<string, object?> bindingData) : BindingContext
    {
        public override IReadOnlyDictionary<string, object?> BindingData => bindingData;
    }

    private sealed class FakeFunctionDefinition(string entryPoint, string triggerType) : FunctionDefinition
    {
        public override ImmutableArray<FunctionParameter> Parameters => [];

        public override string PathToAssembly => typeof(FunctionsTests).Assembly.Location;

        public override string EntryPoint => entryPoint;

        public override string Id => "function-1";

        public override string Name => "ImportOrders";

        public override IImmutableDictionary<string, BindingMetadata> InputBindings { get; } =
            ImmutableDictionary<string, BindingMetadata>.Empty.Add("message", new FakeBindingMetadata(triggerType));

        public override IImmutableDictionary<string, BindingMetadata> OutputBindings => ImmutableDictionary<string, BindingMetadata>.Empty;
    }

    private sealed class FakeBindingMetadata(string type) : BindingMetadata
    {
        public override string Name => "message";

        public override string Type => type;

        public override BindingDirection Direction => BindingDirection.In;
    }
}
