using Observability;
using Observability.AspNetCore;
using Observability.AzureMonitor;
using Observability.Outbox;
using Observability.PayloadArchive;
using OpenTelemetry;
using OpenTelemetry.Trace;
using OrderDemo;

var builder = WebApplication.CreateBuilder(args);

var telemetry = builder.Services
    .AddObservability(builder.Configuration)
    .WithAspNetCoreObservability()
    .WithOutboxInstrumentation()
    .WithTracing(tracing => tracing.AddSource(OrderDemoTelemetry.SourceName));

// One exporter, chosen by configuration: Application Insights, any OTLP backend, or a one-line summary per span.
if (!telemetry.UseAzureMonitorWhenConfigured(builder.Configuration))
{
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    {
        telemetry.UseOtlpExporter();
    }
    else
    {
        telemetry.WithTracing(tracing => tracing.AddProcessor(new ConsoleSummaryProcessor()));
    }
}

builder.Services.AddProblemDetails();
builder.Services.AddOutbox(builder.Configuration);
builder.Services.AddSingleton<InMemoryBus>();
builder.Services.AddSingleton<IMessageSender>(sp => sp.GetRequiredService<InMemoryBus>());
builder.Services.AddHostedService<OrderPlacedConsumer>();

// Raw request and response bodies to append blobs, found by trace id. Off unless a storage account is set
// (PayloadArchive__ServiceUri), so the sample runs without Azure.
var archivePayloads = !string.IsNullOrWhiteSpace(builder.Configuration["PayloadArchive:ServiceUri"]);
if (archivePayloads)
{
    builder.Services.AddPayloadArchive(builder.Configuration);
}

var app = builder.Build();

// First, so the archive holds the response the client received, including the 500 the exception handler writes.
if (archivePayloads)
{
    app.UsePayloadArchive();
}

// An exception that reaches the server clears every response header, x-correlation-id included. Handle it
// here so the 500 still carries the id the support desk needs.
app.UseExceptionHandler();
app.UseObservability();

app.MapGet("/health", () => Results.Ok("healthy"));

app.MapPost("/orders", async (PlaceOrder order, bool? fail, OutboxWriter outbox, ILogger<PlaceOrder> logger, CancellationToken cancellationToken) =>
    {
        if (fail == true)
        {
            throw new InvalidOperationException("The order could not be placed.");
        }

        var orderId = Guid.NewGuid();
        using (logger.BeginScope(new Dictionary<string, object?> { ["OrderId"] = orderId }))
        {
            await outbox.EnqueueAsync("OrderPlaced", $$"""{"orderId":"{{orderId}}","sku":"{{order.Sku}}","quantity":{{order.Quantity}}}""", cancellationToken);
            OrderDemoLog.OrderPlaced(logger, order.Sku, order.Quantity);
        }

        return Results.Accepted($"/orders/{orderId}", new { orderId });
    })
    .WithMetadata(new ProcessAttribute("Place order"));

app.Run();

public sealed record PlaceOrder(string Sku, int Quantity);
