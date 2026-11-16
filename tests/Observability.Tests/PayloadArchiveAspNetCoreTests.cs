using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Observability.AspNetCore;
using Observability.PayloadArchive;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Observability.Tests;

/// <summary>The inbound archive in a real ASP.NET Core pipeline, against the in-memory store.</summary>
public class PayloadArchiveAspNetCoreTests
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    // A small block limit, so every exchange of more than 1 KiB is written in several Append Block calls.
    private readonly InMemoryPayloadArchiveStore _store = new(maxBlockBytes: 1024);
    private readonly List<Activity> _spans = [];
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private FakeLogCollector _logs = null!;
    private int _linesSeenByEndpoint;

    [OneTimeSetUp]
    public async Task StartApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddFakeLogging();
        builder.Configuration.AddInMemoryCollection(TelemetryHarness.DefaultSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PayloadArchive:ServiceUri"] = "https://my-account.blob.core.windows.net",
            ["PayloadArchive:WriteTimeout"] = "00:00:00.200",
            ["PayloadArchive:Endpoints:0:PathPrefix"] = "/api/orders",
            ["PayloadArchive:Endpoints:0:ContainerName"] = "orders-archive",
            ["PayloadArchive:Endpoints:0:ExcludedPathSegments:0"] = "health",
            ["PayloadArchive:Endpoints:1:PathPrefix"] = "/api/after",
            ["PayloadArchive:Endpoints:1:ContainerName"] = "after-archive",
            ["PayloadArchive:Endpoints:1:WriteAfterResponse"] = "true",
        });
        builder.Services.AddObservability(builder.Configuration)
            .WithAspNetCoreObservability()
            .WithTracing(t => t.AddInMemoryExporter(_spans));
        builder.Services.AddProblemDetails();

        // Registered first: AddPayloadArchive only adds the blob store when no store is registered.
        builder.Services.AddSingleton<IPayloadArchiveStore>(_store);
        builder.Services.AddPayloadArchive(builder.Configuration);

        _app = builder.Build();
        _app.UsePayloadArchive();
        _app.UseExceptionHandler();
        _app.UseObservability();
        _app.MapPost("/api/orders", Echo);
        _app.MapPost("/api/after", Echo);
        _app.MapGet("/api/orders/{id}", (string id) => Results.Ok(new { id }));
        _app.MapGet("/api/orders/health", () => "healthy");
        _app.MapGet("/api/orders-report", () => "report");
        _app.MapPost("/api/orders/fail", IResult () => throw new InvalidOperationException("declined"));
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _logs = _app.Services.GetRequiredService<FakeLogCollector>();
    }

    [OneTimeTearDown]
    public async Task StopApp()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [SetUp]
    public void Clear()
    {
        _store.Reset();
        _logs.Clear();
        _spans.Clear();
        _linesSeenByEndpoint = -1;
    }

    private async Task<IResult> Echo(HttpRequest request)
    {
        _linesSeenByEndpoint = _store.LineCount;
        using var reader = new StreamReader(request.Body);
        return Results.Content(await reader.ReadToEndAsync(), "application/json", statusCode: StatusCodes.Status201Created);
    }

    [Test]
    public async Task The_request_is_archived_before_the_endpoint_runs_and_the_response_is_appended_after_it()
    {
        using var response = await _client.PostAsync("/api/orders", Json("{\"sku\":\"A1\",\"quantity\":1}"));
        var clientBody = await response.Content.ReadAsStringAsync();

        var blob = await WaitForBlob("orders-archive", lines: 2);
        var lines = blob.Lines;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(_linesSeenByEndpoint, Is.EqualTo(1), "the request record is on disk before the endpoint runs");
            Assert.That(clientBody, Is.EqualTo("{\"sku\":\"A1\",\"quantity\":1}"), "the endpoint read the body, and the client got the response");
            Assert.That(lines[0].GetProperty("direction").GetString(), Is.EqualTo("inbound"));
            Assert.That(lines[0].GetProperty("method").GetString(), Is.EqualTo("POST"));
            Assert.That(lines[0].GetProperty("bodyEncoding").GetString(), Is.EqualTo("text"));
            Assert.That(lines[0].GetProperty("body").GetString(), Is.EqualTo("{\"sku\":\"A1\",\"quantity\":1}"));
            Assert.That(lines[1].GetProperty("statusCode").GetInt32(), Is.EqualTo(201));
            Assert.That(lines[1].GetProperty("body").GetString(), Is.EqualTo(clientBody));
            Assert.That(lines[1].GetProperty("durationMs").GetDouble(), Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public async Task The_blob_is_named_and_tagged_after_the_trace_and_a_pointer_to_it_is_logged()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = Json("{}") };
        request.Headers.Add("traceparent", TraceParent);

        using var response = await _client.SendAsync(request);

        var blob = await WaitForBlob("orders-archive", lines: 2);
        Assert.Multiple(() =>
        {
            Assert.That(blob.BlobName, Does.Match(@"^\d{4}/\d{2}/\d{2}/0af7651916cd43dd8448eb211c80319c/[0-9a-f-]{36}\.jsonl$"));
            Assert.That(blob.Tags["traceId"], Is.EqualTo("0af7651916cd43dd8448eb211c80319c"));
            Assert.That(blob.Tags["httpMethod"], Is.EqualTo("POST"));
            Assert.That(blob.Tags["direction"], Is.EqualTo("inbound"));
            Assert.That(blob.Lines[0].GetProperty("traceId").GetString(), Is.EqualTo("0af7651916cd43dd8448eb211c80319c"));
            Assert.That(_logs.GetSnapshot().Single(r => r.Id.Id == 3001).Message, Does.Contain($"orders-archive/{blob.BlobName}"));
            Assert.That(_spans.Where(s => s.DisplayName.Contains("blob", StringComparison.OrdinalIgnoreCase)), Is.Empty);
        });
    }

    [Test]
    public async Task Credentials_in_headers_and_the_query_string_are_redacted()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders/42?code=function-key&page=2");
        request.Headers.Authorization = new("Bearer", "secret-token");
        request.Headers.Add("Cookie", "session=secret-cookie");
        request.Headers.Add("X-Api-Key", "secret-key");

        using var response = await _client.SendAsync(request);

        var blob = await WaitForBlob("orders-archive", lines: 2);
        var line = blob.Lines[0];
        Assert.Multiple(() =>
        {
            Assert.That(line.GetProperty("headers").GetProperty("Authorization").GetString(), Is.EqualTo("[redacted]"));
            Assert.That(line.GetProperty("headers").GetProperty("Cookie").GetString(), Is.EqualTo("[redacted]"));
            Assert.That(line.GetProperty("headers").GetProperty("X-Api-Key").GetString(), Is.EqualTo("[redacted]"));
            Assert.That(line.GetProperty("url").GetString(), Does.EndWith("/api/orders/42?code=[redacted]&page=2"));
            Assert.That(blob.Text, Does.Not.Contain("secret").And.Not.Contain("function-key"));
        });
    }

    [TestCase("/api/orders/health")]
    [TestCase("/API/ORDERS/HEALTH")]
    [TestCase("/api/orders-report")]
    public async Task Excluded_and_unmatched_paths_are_not_archived(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(_store.Operations, Is.Empty);
        });
    }

    [Test]
    public async Task The_path_prefix_matches_case_insensitively()
    {
        using var response = await _client.GetAsync("/API/Orders/42");

        var blob = await WaitForBlob("orders-archive", lines: 2);
        Assert.That(blob.Lines[1].GetProperty("statusCode").GetInt32(), Is.EqualTo(200));
    }

    [Test]
    public async Task The_error_response_from_the_exception_handler_is_archived()
    {
        using var response = await _client.PostAsync("/api/orders/fail", Json("{}"));

        var line = (await WaitForBlob("orders-archive", lines: 2)).Lines[1];
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(line.GetProperty("statusCode").GetInt32(), Is.EqualTo(500));
            Assert.That(line.GetProperty("contentType").GetString(), Does.StartWith("application/problem+json"));
            Assert.That(line.TryGetProperty("error", out _), Is.False, "the exception handler turned the exception into a response");
        });
    }

    [Test]
    public async Task A_record_larger_than_one_block_is_appended_in_chunks_and_reads_back_whole()
    {
        var body = $"{{\"note\":\"{new string('x', 3000)}\"}}";

        using var response = await _client.PostAsync("/api/orders", Json(body));

        var blob = await WaitForBlob("orders-archive", lines: 2);
        Assert.Multiple(() =>
        {
            Assert.That(blob.BlockSizes, Has.All.LessThanOrEqualTo(1024));
            Assert.That(blob.BlockSizes, Has.Count.GreaterThan(4));
            Assert.That(blob.Lines[0].GetProperty("body").GetString(), Is.EqualTo(body));
            Assert.That(blob.Lines[1].GetProperty("body").GetString(), Is.EqualTo(body));
        });
    }

    [Test]
    public async Task A_storage_failure_does_not_affect_the_request_and_is_logged_as_a_warning()
    {
        _store.Fail = operation => operation == "create" ? new InvalidOperationException("storage down") : null;

        using var response = await _client.PostAsync("/api/orders", Json("{\"sku\":\"A1\"}"));
        var clientBody = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(clientBody, Is.EqualTo("{\"sku\":\"A1\"}"));
            Assert.That(_logs.GetSnapshot().Where(r => r.Id.Id == 3002).Select(r => r.Level), Is.EqualTo(new[] { LogLevel.Warning }));
        });
    }

    [Test]
    public async Task A_hanging_store_delays_a_request_by_at_most_the_write_timeout()
    {
        _store.Hang.Add("create");
        var stopwatch = Stopwatch.StartNew();

        using var response = await _client.PostAsync("/api/orders", Json("{}"));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            Assert.That(_store.Operations, Is.EqualTo(new[] { "create" }), "after the timed-out write the exchange is not retried");
        });
    }

    [Test]
    public async Task With_write_after_response_both_records_are_written_after_the_endpoint_ran()
    {
        using var response = await _client.PostAsync("/api/after", Json("{\"sku\":\"B2\"}"));

        var blob = await WaitForBlob("after-archive", lines: 2);
        Assert.Multiple(() =>
        {
            Assert.That(_linesSeenByEndpoint, Is.Zero);
            Assert.That(blob.Lines[0].GetProperty("body").GetString(), Is.EqualTo("{\"sku\":\"B2\"}"));
            Assert.That(blob.Lines[1].GetProperty("statusCode").GetInt32(), Is.EqualTo(201));
            Assert.That(blob.Lines[1].GetProperty("headers").GetProperty(CorrelationIdMiddleware.HeaderName).GetString(),
                Is.Not.Empty, "the final headers, including those added when the response started");
        });
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>The response record may be written just after the client has the response; wait for it.</summary>
    private async Task<StoredBlob> WaitForBlob(string containerName, int lines)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var blob = _store.Blobs.SingleOrDefault(b => b.ContainerName == containerName);
            if (blob is not null && blob.Lines.Count >= lines)
            {
                return blob;
            }

            if (deadline.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail($"Expected {lines} line(s) in {containerName}; found {blob?.Lines.Count ?? 0}.");
            }

            await Task.Delay(10);
        }
    }
}
