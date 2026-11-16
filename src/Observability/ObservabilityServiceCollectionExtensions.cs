using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Observability.Heartbeat;
using Observability.Metrics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Observability;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>The span the Azure SDK creates around each HTTP call it makes.</summary>
    internal const string AzureCoreHttpSourceName = "Azure.Core.Http";

    /// <summary>
    /// Registers OpenTelemetry tracing, metrics and logging with one service identity, one sampling ratio,
    /// process spans and metrics, allow-listed baggage, scoped baggage propagation and a telemetry heartbeat.
    /// Add host instrumentation and an exporter on the builder it returns.
    /// </summary>
    /// <exception cref="OptionsValidationException">The <c>Observability</c> section is invalid.</exception>
    public static IOpenTelemetryBuilder AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read the options now as well: the resource and the sampler are fixed when the providers are built,
        // and a missing service name should stop the host at start-up, not produce anonymous telemetry.
        var section = configuration.GetSection(ObservabilityOptions.SectionName);
        var options = section.Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(ObservabilityOptions.SectionName, typeof(ObservabilityOptions), errors);
        }

        services.AddOptions<ObservabilityOptions>().Bind(section).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();

        services.TryAddSingleton<ProcessTracer>();
        services.AddMetricsBundle<ProcessMetrics>();
        services.AddMetricsBundle<HeartbeatMetrics>();
        services.TryAddSingleton<ProcessMetricsListener>();

        var builder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName, serviceNamespace: options.ServiceNamespace))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.SamplingRatio)))
                .AddSource(Telemetry.ProcessSourceName, Telemetry.MessagingSourceName, "Azure.*")
                .AddHttpClientInstrumentation(http => http.FilterHttpRequestMessage = _ => !IsAzureSdkCall(Activity.Current))
                .AddProcessor(sp => new BaggageToTagsProcessor(sp.GetRequiredService<IOptions<ObservabilityOptions>>()))
                .AddProcessor(sp => new ExpectedDependencyStatusProcessor(sp.GetRequiredService<IOptions<ObservabilityOptions>>())))
            .WithMetrics(metrics => metrics
                .AddMeter(Telemetry.ProcessMeterName, Telemetry.HeartbeatMeterName, MetricsBundle.WildcardFor(options.ServiceName))
                .AddHttpClientInstrumentation())
            .WithLogging(configureBuilder: null, configureOptions: logging =>
            {
                logging.IncludeScopes = true;
                logging.IncludeFormattedMessage = true;
            });

        // After AddOpenTelemetry: hosted services start in registration order, and a measurement taken before
        // the meter provider exists is lost. The heartbeat's first beat would never be exported.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ProcessMetricsListener>(sp => sp.GetRequiredService<ProcessMetricsListener>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BaggagePropagationService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TelemetryHeartbeat>());
        return builder;
    }

    /// <summary>
    /// Registers a <see cref="MetricsBundle"/> as a singleton. Its meter is exported through the
    /// <c>{service.name}.*</c> subscription unless it has a fixed <see cref="MeterNameAttribute"/>; then add
    /// that name with <c>AddMeter</c>.
    /// </summary>
    public static IServiceCollection AddMetricsBundle<TBundle>(this IServiceCollection services)
        where TBundle : MetricsBundle
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TBundle>();
        return services;
    }

    /// <summary>
    /// The Azure SDK traces each HTTP call itself; the HttpClient span below it is a duplicate dependency.
    /// When the filter runs, the current span is the HttpClient span, so its parent tells who made the call.
    /// </summary>
    internal static bool IsAzureSdkCall(Activity? httpClientActivity) =>
        httpClientActivity?.Parent?.Source.Name == AzureCoreHttpSourceName;
}
