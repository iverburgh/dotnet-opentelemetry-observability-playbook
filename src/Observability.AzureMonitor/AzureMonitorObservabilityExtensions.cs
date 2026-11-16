using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;

namespace Observability.AzureMonitor;

public static class AzureMonitorObservabilityExtensions
{
    public const string ConnectionStringSetting = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>
    /// Exports traces, metrics and logs to Application Insights when <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>
    /// is set, and does nothing otherwise — locally and in tests you get no exporter and no errors.
    /// </summary>
    /// <returns>Whether the exporter was added.</returns>
    public static bool UseAzureMonitorWhenConfigured(this IOpenTelemetryBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration[ConnectionStringSetting];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        var credential = AzureMonitorCredential.Create(configuration[AzureMonitorCredential.SettingName]);
        var samplingRatio = configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>()?.SamplingRatio ?? 1.0;
        builder.UseAzureMonitorExporter(options =>
        {
            options.ConnectionString = connectionString;
            options.Credential = credential;
            AzureMonitorDefaults.Apply(options, samplingRatio);
        });
        return true;
    }
}
