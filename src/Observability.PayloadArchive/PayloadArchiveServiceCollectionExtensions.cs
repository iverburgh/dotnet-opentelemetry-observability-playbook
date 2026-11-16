using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Observability.PayloadArchive;

public static class PayloadArchiveServiceCollectionExtensions
{
    /// <summary>
    /// Registers the payload archive: the options, an append-blob store that signs in with the registered
    /// <see cref="TokenCredential"/> (or <see cref="DefaultAzureCredential"/> when none is registered), and a
    /// <c>payload-archive</c> health check that reports degraded, never unhealthy. Add
    /// <see cref="UsePayloadArchive"/> for inbound requests and
    /// <see cref="AddPayloadArchive(IHttpClientBuilder, string)"/> per outgoing client.
    /// </summary>
    /// <exception cref="OptionsValidationException">The <c>PayloadArchive</c> section is invalid.</exception>
    public static IServiceCollection AddPayloadArchive(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read now as well: the blob client is built once, from the service URI, and a missing URI should stop
        // the host at start-up rather than fail every write.
        var section = configuration.GetSection(PayloadArchiveOptions.SectionName);
        var options = section.Get<PayloadArchiveOptions>() ?? new PayloadArchiveOptions();
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(PayloadArchiveOptions.SectionName, typeof(PayloadArchiveOptions), errors);
        }

        services.AddOptions<PayloadArchiveOptions>().Bind(section).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PayloadArchiveOptions>, PayloadArchiveOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPayloadArchiveStore>(sp =>
            new BlobPayloadArchiveStore(CreateServiceClient(options, sp.GetService<TokenCredential>() ?? new DefaultAzureCredential())));
        services.TryAddSingleton<PayloadArchiveWriter>();
        services.AddHealthChecks().AddCheck<PayloadArchiveHealthCheck>(
            PayloadArchiveHealthCheck.Name, failureStatus: HealthStatus.Degraded, tags: [PayloadArchiveHealthCheck.Name]);
        return services;
    }

    /// <summary>
    /// Archives inbound requests on the configured endpoints. Call it before <c>UseExceptionHandler</c>, so the
    /// archived response is the error response the client received, and before middleware that adds headers.
    /// </summary>
    public static IApplicationBuilder UsePayloadArchive(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<PayloadArchiveMiddleware>();
    }

    /// <summary>
    /// Archives every call this client makes to <paramref name="containerName"/>. Handlers run in the order they
    /// are added: added last, the archive sees each retry and the headers other handlers set (redacted).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="containerName"/> is not a valid container name.</exception>
    public static IHttpClientBuilder AddPayloadArchive(this IHttpClientBuilder builder, string containerName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!PayloadArchiveOptions.IsValidContainerName(containerName))
        {
            throw new ArgumentException(
                $"'{containerName}' is not a valid container name: use 3 to 63 lower-case letters, digits and single hyphens.",
                nameof(containerName));
        }

        builder.Services.AddSingleton(new OutboundArchiveContainer(containerName));
        return builder.AddHttpMessageHandler(sp => new PayloadArchiveHandler(
            containerName,
            sp.GetService<PayloadArchiveWriter>()
                ?? throw new InvalidOperationException("Call services.AddPayloadArchive(configuration) before archiving an HttpClient."),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PayloadArchiveHandler>>()));
    }

    /// <summary>
    /// A client that gives up quickly. The SDK defaults, several retries and a 100-second network timeout,
    /// would hold every archived request for minutes while storage is unreachable.
    /// </summary>
    internal static BlobServiceClient CreateServiceClient(PayloadArchiveOptions options, TokenCredential credential)
    {
        var clientOptions = new BlobClientOptions();
        clientOptions.Retry.MaxRetries = 2;
        clientOptions.Retry.Mode = RetryMode.Exponential;
        clientOptions.Retry.Delay = TimeSpan.FromMilliseconds(100);
        clientOptions.Retry.MaxDelay = TimeSpan.FromSeconds(1);
        clientOptions.Retry.NetworkTimeout = options.WriteTimeout;
        return new BlobServiceClient(options.ServiceUri, credential, clientOptions);
    }
}
