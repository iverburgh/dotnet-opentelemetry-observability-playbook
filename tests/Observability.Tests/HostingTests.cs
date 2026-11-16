using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Observability.AzureMonitor;
using Observability.Heartbeat;

namespace Observability.Tests;

public class HeartbeatTests
{
    private static Task<TelemetryHarness> StartAsync(bool enabled) =>
        TelemetryHarness.StartAsync(
            new Dictionary<string, string?> { ["Observability:EnableHeartbeat"] = enabled ? "true" : "false" },
            (services, _) =>
            {
                services.AddSingleton<TimeProvider>(new FakeTimeProvider());
                services.AddLogging(logging => logging.AddFakeLogging());
            });

    [Test]
    public async Task An_enabled_heartbeat_beats_at_start_on_both_meter_paths_and_logs()
    {
        await using var harness = await StartAsync(enabled: true);
        var logs = harness.Get<FakeLogCollector>();

        // Since .NET 10 a BackgroundService runs ExecuteAsync on a thread-pool thread, after StartAsync returns.
        for (var attempt = 0; attempt < 100 && !logs.GetSnapshot().Any(r => r.Id.Id == 1001); attempt++)
        {
            await Task.Delay(20);
        }

        harness.FlushMetrics();

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sum("telemetry.heartbeat", (HeartbeatMetrics.PathTag, "wildcard")), Is.EqualTo(1));
            Assert.That(harness.Sum("telemetry.heartbeat", (HeartbeatMetrics.PathTag, "explicit")), Is.EqualTo(1));
            Assert.That(logs.GetSnapshot().Count(r => r.Id.Id == 1001), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_disabled_heartbeat_emits_nothing()
    {
        await using var harness = await StartAsync(enabled: false);

        harness.FlushMetrics();

        Assert.That(harness.Metrics.Any(m => m.Name == "telemetry.heartbeat"), Is.False);
    }
}

public class AzureMonitorTests
{
    [TestCase(null)]
    [TestCase("")]
    public void No_authentication_string_means_key_based_ingestion(string? value) =>
        Assert.That(AzureMonitorCredential.Create(value), Is.Null);

    [TestCase("Authorization=AAD")]
    [TestCase("Authorization=AAD;ClientId=00000000-0000-0000-0000-000000000001")]
    public void An_AAD_authentication_string_gives_a_managed_identity(string value) =>
        Assert.That(AzureMonitorCredential.Create(value), Is.TypeOf<ManagedIdentityCredential>());

    [TestCase("Authorization=Key")]
    [TestCase("Authorization=AAD;ClientId=not-a-guid")]
    [TestCase("Authorization=AAD;Tenant=x")]
    [TestCase("AAD")]
    public void Any_other_authentication_string_is_rejected(string value) =>
        Assert.That(() => AzureMonitorCredential.Create(value), Throws.TypeOf<FormatException>());

    [Test]
    public void The_defaults_that_change_cost_and_visibility_are_pinned()
    {
        var options = new AzureMonitorExporterOptions();

        AzureMonitorDefaults.Apply(options, 0.25);

        Assert.Multiple(() =>
        {
            Assert.That(options.SamplingRatio, Is.EqualTo(0.25f));
            Assert.That(options.TracesPerSecond, Is.Null, "rate limiting would override the ratio");
            Assert.That(options.EnableTraceBasedLogsSampler, Is.False, "logs of unsampled traces are kept");
            Assert.That(options.EnableStandardMetrics, Is.True);
            Assert.That(options.EnablePerformanceCounters, Is.False);
            Assert.That(options.Transport, Is.Not.Null);
        });
    }

    [Test]
    public void The_exporter_is_added_only_when_a_connection_string_is_configured()
    {
        static bool Use(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(TelemetryHarness.DefaultSettings()).AddInMemoryCollection(settings).Build();
            return new ServiceCollection().AddObservability(configuration).UseAzureMonitorWhenConfigured(configuration);
        }

        Assert.Multiple(() =>
        {
            Assert.That(Use([]), Is.False);
            Assert.That(Use(new() { [AzureMonitorObservabilityExtensions.ConnectionStringSetting] = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/" }), Is.True);
        });
    }
}

public class OptionsTests
{
    private static ObservabilityOptions Bind(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>()!;

    [Test]
    public void A_missing_service_name_fails_at_registration()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.That(() => new ServiceCollection().AddObservability(configuration), Throws.TypeOf<OptionsValidationException>().With.Message.Contains("ServiceName"));
    }

    [Test]
    public void Every_invalid_value_is_reported()
    {
        var options = Bind(new()
        {
            ["Observability:ServiceName"] = "svc",
            ["Observability:SamplingRatio"] = "1.5",
            ["Observability:HeartbeatInterval"] = "00:00:00.5",
            ["Observability:BaggageTagKeys:0"] = "Tenant.Id",
            ["Observability:ExpectedDependencyResponses:0:PathContains"] = "/locks/",
        });

        Assert.That(options.Validate(), Has.Count.EqualTo(4));
    }

    [Test]
    public void A_configured_list_replaces_the_default_instead_of_adding_to_it()
    {
        var configured = Bind(new() { ["Observability:ServiceName"] = "svc", ["Observability:NonFailureStatusCodes:0"] = "409" });
        var defaults = Bind(new() { ["Observability:ServiceName"] = "svc" });

        Assert.Multiple(() =>
        {
            Assert.That(configured.EffectiveNonFailureStatusCodes, Is.EqualTo(new[] { 409 }));
            Assert.That(defaults.EffectiveNonFailureStatusCodes, Is.EqualTo(new[] { 401, 404 }));
            Assert.That(defaults.EffectiveExcludedRequestPaths, Is.EqualTo(new[] { "/health", "/alive" }));
        });
    }
}
