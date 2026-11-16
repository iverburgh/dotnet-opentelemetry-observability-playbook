using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Observability.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the writer, the publisher and its background service. Register an <see cref="IOutboxStore"/>
    /// and an <see cref="IMessageSender"/> yourself; an in-memory store is used when none is registered.
    /// </summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration, bool runPublisher = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutboxStore, InMemoryOutboxStore>();
        services.TryAddSingleton<OutboxWriter>();
        services.TryAddSingleton<OutboxPublisher>();
        services.AddMetricsBundle<OutboxMetrics>();
        if (runPublisher)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxPublisherService>());
        }

        return services;
    }

    /// <summary>Subscribes to the outbox activity source and meter.</summary>
    public static IOpenTelemetryBuilder WithOutboxInstrumentation(this IOpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithTracing(tracing => tracing.AddSource(OutboxTelemetry.SourceName))
            .WithMetrics(metrics => metrics.AddMeter(OutboxTelemetry.MeterName));
    }
}
