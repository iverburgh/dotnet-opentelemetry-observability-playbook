using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Observability.PayloadArchive;

namespace Observability.Tests;

public class PayloadArchiveTests
{
    private const string ServiceUri = "https://my-account.blob.core.windows.net";

    private InMemoryPayloadArchiveStore _store = null!;
    private FakeLogCollector _logs = null!;

    [SetUp]
    public void Setup()
    {
        _store = new InMemoryPayloadArchiveStore();
        _logs = new FakeLogCollector();
    }

    // ---- Blob layout, chunking, encoding, redaction, path matching ----

    [Test]
    public void Blob_names_are_grouped_per_utc_day_and_per_trace()
    {
        var lateEvening = new DateTimeOffset(2025, 1, 31, 23, 30, 0, TimeSpan.FromHours(-2));
        var traceId = ActivityTraceId.CreateFromString("0af7651916cd43dd8448eb211c80319c");
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");

        Assert.Multiple(() =>
        {
            Assert.That(PayloadArchiveWriter.BlobName(lateEvening, traceId, id),
                Is.EqualTo("2025/02/01/0af7651916cd43dd8448eb211c80319c/00000000-0000-0000-0000-000000000001.jsonl"));
            Assert.That(PayloadArchiveWriter.BlobName(lateEvening, default, id),
                Is.EqualTo("2025/02/01/no-trace/00000000-0000-0000-0000-000000000001.jsonl"));
        });
    }

    [Test]
    public void A_record_is_split_into_blocks_no_larger_than_the_limit()
    {
        var data = Encoding.ASCII.GetBytes("0123456789");

        var blocks = PayloadArchiveWriter.Chunks(data, 4).Select(block => Encoding.ASCII.GetString(block.Span)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(blocks, Is.EqualTo(new[] { "0123", "4567", "89" }));
            Assert.That(PayloadArchiveWriter.Chunks(data, 10).Count(), Is.EqualTo(1));
            Assert.That(PayloadArchiveWriter.Chunks(ReadOnlyMemory<byte>.Empty, 4), Is.Empty);
        });
    }

    [TestCase("application/json", "{\"id\":1}", "text")]
    [TestCase("application/problem+json; charset=utf-8", "{\"id\":1}", "text")]
    [TestCase("text/xml", "<a>é</a>", "text")]
    [TestCase("application/x-www-form-urlencoded", "a=1&b=2", "text")]
    [TestCase("application/octet-stream", "abc", "base64")]
    [TestCase(null, "abc", "base64")]
    [TestCase("application/json; charset=unknown-charset", "abc", "base64")]
    public void Text_bodies_are_kept_as_text_and_everything_else_as_base64(string? contentType, string body, string expectedEncoding)
    {
        var bytes = Encoding.UTF8.GetBytes(body);

        var (encoded, encoding) = PayloadBody.Encode(bytes, contentType);

        Assert.Multiple(() =>
        {
            Assert.That(encoding, Is.EqualTo(expectedEncoding));
            Assert.That(encoded, Is.EqualTo(expectedEncoding == "text" ? body : Convert.ToBase64String(bytes)));
        });
    }

    [Test]
    public void A_text_body_that_is_not_valid_in_its_charset_is_kept_as_base64_so_no_byte_is_lost()
    {
        byte[] invalidUtf8 = [0x7B, 0xC3, 0x28, 0x7D];

        var (encoded, encoding) = PayloadBody.Encode(invalidUtf8, "application/json");

        Assert.Multiple(() =>
        {
            Assert.That(encoding, Is.EqualTo("base64"));
            Assert.That(Convert.FromBase64String(encoded!), Is.EqualTo(invalidUtf8));
        });
    }

    [Test]
    public void A_text_body_is_decoded_with_its_declared_charset_and_an_empty_body_is_left_out()
    {
        var latin1 = Encoding.Latin1.GetBytes("café");

        Assert.Multiple(() =>
        {
            Assert.That(PayloadBody.Encode(latin1, "text/plain; charset=iso-8859-1"), Is.EqualTo(("café", "text")));
            Assert.That(PayloadBody.Encode([], "application/json"), Is.EqualTo(((string?)null, (string?)null)));
        });
    }

    [Test]
    public void Sensitive_headers_are_redacted_case_insensitively_and_repeated_headers_are_joined()
    {
        var options = new PayloadArchiveOptions { RedactedHeaders = ["X-Partner-Secret"] };
        var headers = new[]
        {
            KeyValuePair.Create("authorization", new[] { "Bearer secret" }),
            KeyValuePair.Create("x-partner-secret", new[] { "secret" }),
            KeyValuePair.Create("Accept", new[] { "application/json" }),
            KeyValuePair.Create("Accept", new[] { "text/plain" }),
        };

        var redacted = PayloadRedaction.Headers(headers, options.EffectiveRedactedHeaders);

        Assert.Multiple(() =>
        {
            Assert.That(redacted["Authorization"], Is.EqualTo("[redacted]"));
            Assert.That(redacted["X-Partner-Secret"], Is.EqualTo("[redacted]"));
            Assert.That(redacted["Accept"], Is.EqualTo("application/json, text/plain"));
        });
    }

    [Test]
    public void Sensitive_query_parameters_are_redacted_and_the_fragment_is_dropped()
    {
        var options = new PayloadArchiveOptions();

        var url = PayloadRedaction.Url("https://api.example/orders?code=abc&page=2&SIG=xyz#top", options.EffectiveRedactedQueryParameters);

        Assert.That(url, Is.EqualTo("https://api.example/orders?code=[redacted]&page=2&SIG=[redacted]"));
    }

    [TestCase("/api/orders", "orders-archive")]
    [TestCase("/API/Orders/42", "orders-archive")]
    [TestCase("/api/orders/42/items", "orders-archive")]
    [TestCase("/api/orders-report", "catch-all")]
    [TestCase("/api/orders/health", null)]
    [TestCase("/api/orders/42/HEALTH", null)]
    [TestCase("/api/payments/1", "catch-all")]
    [TestCase("/", "catch-all")]
    public void Paths_match_per_segment_case_insensitively_and_an_excluded_segment_is_final(string path, string? expectedContainer)
    {
        PayloadArchiveEndpoint[] endpoints =
        [
            new() { PathPrefix = "/api/orders", ContainerName = "orders-archive", ExcludedPathSegments = ["health"] },
            new() { PathPrefix = "/", ContainerName = "catch-all" },
        ];

        var match = PayloadPathFilter.Match(endpoints, new PathString(path));

        Assert.That(match?.ContainerName, Is.EqualTo(expectedContainer));
    }

    // ---- Options ----

    [Test]
    public void Valid_options_pass_validation()
    {
        var options = ValidOptions(
            new PayloadArchiveEndpoint { PathPrefix = "/api/orders", ContainerName = "orders-archive", ExcludedPathSegments = ["health"] },
            new PayloadArchiveEndpoint { PathPrefix = "/api", ContainerName = "api-archive", WriteAfterResponse = true });

        Assert.That(options.Validate(), Is.Empty);
    }

    [TestCase("no-uri", "ServiceUri")]
    [TestCase("http-uri", "ServiceUri")]
    [TestCase("timeout", "WriteTimeout")]
    [TestCase("relative-prefix", "PathPrefix 'api'")]
    [TestCase("trailing-slash", "PathPrefix '/api/'")]
    [TestCase("container", "ContainerName 'Orders_Archive'")]
    [TestCase("segment", "excluded segment 'a/b'")]
    [TestCase("shadowed", "never matches: /api comes first")]
    [TestCase("catch-all-first", "never matches: / comes first")]
    public void Invalid_options_are_reported(string problem, string expectedError)
    {
        var options = ValidOptions(new PayloadArchiveEndpoint { PathPrefix = "/api/orders", ContainerName = "orders-archive" });
        switch (problem)
        {
            case "no-uri": options.ServiceUri = null; break;
            case "http-uri": options.ServiceUri = new Uri("http://my-account.blob.core.windows.net"); break;
            case "timeout": options.WriteTimeout = TimeSpan.Zero; break;
            case "relative-prefix": options.Endpoints![0].PathPrefix = "api"; break;
            case "trailing-slash": options.Endpoints![0].PathPrefix = "/api/"; break;
            case "container": options.Endpoints![0].ContainerName = "Orders_Archive"; break;
            case "segment": options.Endpoints![0].ExcludedPathSegments = ["a/b"]; break;
            case "shadowed": options.Endpoints = [new() { PathPrefix = "/api", ContainerName = "api-archive" }, .. options.Endpoints!]; break;
            case "catch-all-first": options.Endpoints = [new() { PathPrefix = "/", ContainerName = "all-archive" }, .. options.Endpoints!]; break;
        }

        Assert.That(options.Validate(), Has.One.Contains(expectedError));
    }

    [Test]
    public void Registering_the_archive_with_an_invalid_section_fails_at_start_up()
    {
        var configuration = Configuration(new() { ["PayloadArchive:Endpoints:0:PathPrefix"] = "/api" });

        Assert.That(() => new ServiceCollection().AddPayloadArchive(configuration),
            Throws.TypeOf<OptionsValidationException>().With.Message.Contains("ServiceUri"));
    }

    [Test]
    public void An_http_client_needs_a_valid_container_and_the_archive_registered()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("partner").AddPayloadArchive("calls-archive");
        using var provider = services.BuildServiceProvider();

        Assert.Multiple(() =>
        {
            Assert.That(() => new ServiceCollection().AddHttpClient("partner").AddPayloadArchive("Calls"), Throws.ArgumentException);
            Assert.That(() => provider.GetRequiredService<IHttpClientFactory>().CreateClient("partner"),
                Throws.InvalidOperationException.With.Message.Contains("AddPayloadArchive"));
        });
    }

    [Test]
    public void A_configuration_change_is_picked_up_and_an_invalid_one_switches_the_archive_off_until_it_is_fixed()
    {
        var configuration = Configuration(new()
        {
            ["PayloadArchive:ServiceUri"] = ServiceUri,
            ["PayloadArchive:Endpoints:0:PathPrefix"] = "/api/orders",
            ["PayloadArchive:Endpoints:0:ContainerName"] = "orders-archive",
        });
        using var provider = Services(configuration).BuildServiceProvider();
        var writer = provider.GetRequiredService<PayloadArchiveWriter>();

        configuration["PayloadArchive:Endpoints:0:ContainerName"] = "orders-archive-v2";
        configuration.Reload();
        var changed = writer.CurrentOptions()?.Endpoints?[0].ContainerName;

        configuration["PayloadArchive:Endpoints:0:ContainerName"] = "Not Valid";
        ReloadIgnoringValidation(configuration);
        var whileInvalid = writer.CurrentOptions();

        configuration["PayloadArchive:Endpoints:0:ContainerName"] = "orders-archive-v3";
        configuration.Reload();

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo("orders-archive-v2"));
            Assert.That(whileInvalid, Is.Null);
            Assert.That(LogIds(provider), Does.Contain(3004));
            Assert.That(writer.CurrentOptions()?.Endpoints?[0].ContainerName, Is.EqualTo("orders-archive-v3"));
        });
    }

    // ---- Writer: best effort ----

    [Test]
    public async Task A_record_is_written_as_one_json_line_with_tags_while_instrumentation_is_suppressed()
    {
        var writer = Writer(ValidOptions());
        var traceId = ActivityTraceId.CreateRandom();

        var entry = writer.Begin("orders-archive", PayloadDirection.Inbound, "POST", traceId);
        await writer.WriteAsync(entry, new PayloadRecord { Direction = PayloadDirection.Inbound, Phase = PayloadRecord.RequestPhase, Body = "<a/>" });

        var blob = _store.Blobs.Single();
        Assert.Multiple(() =>
        {
            Assert.That(blob.ContainerName, Is.EqualTo("orders-archive"));
            Assert.That(blob.BlobName, Does.Contain($"/{traceId.ToHexString()}/"));
            Assert.That(blob.Tags, Is.EquivalentTo(new Dictionary<string, string>
            {
                ["direction"] = "inbound",
                ["httpMethod"] = "POST",
                ["traceId"] = traceId.ToHexString(),
            }));
            Assert.That(blob.Text, Does.EndWith("\n").And.Contain("\"body\":\"<a/>\""));
            Assert.That(_store.AlwaysSuppressed, Is.True);
            Assert.That(_logs.GetSnapshot().Single(r => r.Id.Id == 3001).Message, Does.Contain($"orders-archive/{blob.BlobName}"));
        });
    }

    [Test]
    public void An_exchange_without_a_trace_is_not_tagged_with_one()
    {
        var entry = Writer(ValidOptions()).Begin("orders-archive", PayloadDirection.Outbound, "GET", default);

        Assert.Multiple(() =>
        {
            Assert.That(entry.Tags.ContainsKey("traceId"), Is.False);
            Assert.That(entry.BlobName, Does.Contain("/no-trace/"));
        });
    }

    [Test]
    public async Task A_storage_failure_is_logged_as_a_warning_and_never_thrown()
    {
        _store.Fail = operation => operation == "create" ? new InvalidOperationException("storage down") : null;
        var writer = Writer(ValidOptions());
        var entry = writer.Begin("orders-archive", PayloadDirection.Inbound, "POST", default);

        await writer.WriteAsync(entry, Record(PayloadRecord.RequestPhase));

        var warning = _logs.GetSnapshot().Single(r => r.Id.Id == 3002);
        Assert.Multiple(() =>
        {
            Assert.That(warning.Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(warning.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(entry.Broken, Is.True);
        });
    }

    [Test]
    public async Task After_a_failed_append_no_further_record_is_glued_onto_the_partial_line()
    {
        var store = new InMemoryPayloadArchiveStore(maxBlockBytes: 16);
        var appends = 0;
        store.Fail = operation => operation == "append" && ++appends == 2 ? new IOException("connection reset") : null;
        var writer = Writer(ValidOptions(), store);
        var entry = writer.Begin("orders-archive", PayloadDirection.Inbound, "POST", default);

        await writer.WriteAsync(entry, Record(PayloadRecord.RequestPhase));
        await writer.WriteAsync(entry, Record(PayloadRecord.ResponsePhase));

        Assert.Multiple(() =>
        {
            Assert.That(store.Operations, Is.EqualTo(new[] { "create", "append", "append" }));
            Assert.That(store.Blobs.Single().Lines, Is.Empty);
        });
    }

    [Test]
    public async Task A_hanging_store_costs_at_most_the_write_timeout()
    {
        _store.Hang.Add("create");
        var options = ValidOptions();
        options.WriteTimeout = TimeSpan.FromMilliseconds(100);
        var writer = Writer(options);
        var entry = writer.Begin("orders-archive", PayloadDirection.Inbound, "POST", default);

        var stopwatch = Stopwatch.StartNew();
        await writer.WriteAsync(entry, Record(PayloadRecord.RequestPhase));

        Assert.Multiple(() =>
        {
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            Assert.That(_logs.GetSnapshot().Single(r => r.Id.Id == 3002).Exception, Is.InstanceOf<OperationCanceledException>());
        });
    }

    // ---- Inbound middleware, without a server ----

    [Test]
    public async Task With_write_after_response_nothing_is_written_until_the_response_has_been_sent()
    {
        var writer = Writer(ValidOptions(new PayloadArchiveEndpoint { PathPrefix = "/api", ContainerName = "api-archive", WriteAfterResponse = true }));
        var (context, response) = Context("POST", "/api/orders", "{\"sku\":\"A1\"}");
        var linesDuringRequest = -1;
        var middleware = Middleware(writer, async ctx =>
        {
            linesDuringRequest = _store.LineCount;
            ctx.Response.StatusCode = StatusCodes.Status201Created;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"id\":1}");
        });

        await middleware.InvokeAsync(context);
        var linesBeforeCompletion = _store.LineCount;
        await response.CompleteAsync();

        var lines = _store.Blobs.Single().Lines;
        Assert.Multiple(() =>
        {
            Assert.That(linesDuringRequest, Is.Zero);
            Assert.That(linesBeforeCompletion, Is.Zero);
            Assert.That(lines.Select(l => l.GetProperty("phase").GetString()), Is.EqualTo(new[] { "request", "response" }));
            Assert.That(lines[0].GetProperty("body").GetString(), Is.EqualTo("{\"sku\":\"A1\"}"));
            Assert.That(lines[1].GetProperty("statusCode").GetInt32(), Is.EqualTo(201));
            Assert.That(lines[1].GetProperty("body").GetString(), Is.EqualTo("{\"id\":1}"));
        });
    }

    [Test]
    public void When_the_endpoint_throws_the_response_record_names_the_exception_and_the_exception_is_rethrown()
    {
        var writer = Writer(ValidOptions(new PayloadArchiveEndpoint { PathPrefix = "/api", ContainerName = "api-archive" }));
        var (context, _) = Context("POST", "/api/orders", "{}");
        var middleware = Middleware(writer, _ => throw new InvalidOperationException("secret detail"));

        Assert.That(() => middleware.InvokeAsync(context), Throws.InvalidOperationException);

        var response = _store.Blobs.Single().Lines[1];
        Assert.Multiple(() =>
        {
            Assert.That(response.GetProperty("error").GetString(), Is.EqualTo("System.InvalidOperationException"));
            Assert.That(response.TryGetProperty("statusCode", out _), Is.False, "the response never started");
            Assert.That(_store.Blobs.Single().Text, Does.Not.Contain("secret detail"));
        });
    }

    [Test]
    public async Task Invalid_options_after_a_reload_let_requests_through_unarchived()
    {
        var monitor = new ThrowingOptionsMonitor();
        var writer = new PayloadArchiveWriter(_store, monitor, TimeProvider.System, new FakeLogger<PayloadArchiveWriter>(_logs));
        var (context, _) = Context("GET", "/api/orders", "");
        var called = false;

        await Middleware(writer, _ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(called, Is.True);
            Assert.That(_store.Operations, Is.Empty);
            Assert.That(_logs.GetSnapshot().Select(r => r.Id.Id), Does.Contain(3004));
        });
    }

    // ---- Outbound handler ----

    [Test]
    public async Task An_outgoing_call_is_archived_and_the_server_and_the_caller_still_get_every_byte()
    {
        byte[]? receivedBody = null;
        string? receivedContentType = null;
        using var provider = OutboundServices(async request =>
        {
            receivedContentType = request.Content!.Headers.ContentType!.ToString();
            receivedBody = await request.Content.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") };
        });
        using var activity = new Activity("place-order").Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://partner.example/orders?sig=secret&page=1")
        {
            Content = new StringContent("<order id=\"1\"/>", Encoding.UTF8, "application/xml"),
        };
        request.Headers.Authorization = new("Bearer", "secret-token");

        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("partner").SendAsync(request);
        var callerBody = await response.Content.ReadAsStringAsync();

        var blob = _store.Blobs.Single();
        var lines = blob.Lines;
        Assert.Multiple(() =>
        {
            Assert.That(receivedContentType, Is.EqualTo("application/xml; charset=utf-8"));
            Assert.That(Encoding.UTF8.GetString(receivedBody!), Is.EqualTo("<order id=\"1\"/>"));
            Assert.That(callerBody, Is.EqualTo("{\"ok\":true}"));
            Assert.That(blob.ContainerName, Is.EqualTo("calls-archive"));
            Assert.That(blob.Tags["direction"], Is.EqualTo("outbound"));
            Assert.That(blob.Tags["traceId"], Is.EqualTo(activity.TraceId.ToHexString()));
            Assert.That(lines[0].GetProperty("url").GetString(), Is.EqualTo("https://partner.example/orders?sig=[redacted]&page=1"));
            Assert.That(lines[0].GetProperty("headers").GetProperty("Authorization").GetString(), Is.EqualTo("[redacted]"));
            Assert.That(lines[0].GetProperty("body").GetString(), Is.EqualTo("<order id=\"1\"/>"));
            Assert.That(lines[1].GetProperty("statusCode").GetInt32(), Is.EqualTo(200));
            Assert.That(lines[1].GetProperty("body").GetString(), Is.EqualTo("{\"ok\":true}"));
            Assert.That(blob.Text, Does.Not.Contain("secret"));
        });
    }

    [Test]
    public async Task A_binary_outgoing_body_reaches_the_server_unchanged_and_is_archived_as_base64()
    {
        byte[] payload = [0x00, 0xFF, 0x10, 0x80];
        byte[]? receivedBody = null;
        using var provider = OutboundServices(async request =>
        {
            receivedBody = await request.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("partner")
            .PostAsync("https://partner.example/files", new ByteArrayContent(payload) { Headers = { ContentType = new("application/octet-stream") } });

        var request = _store.Blobs.Single().Lines[0];
        Assert.Multiple(() =>
        {
            Assert.That(receivedBody, Is.EqualTo(payload));
            Assert.That(request.GetProperty("bodyEncoding").GetString(), Is.EqualTo("base64"));
            Assert.That(Convert.FromBase64String(request.GetProperty("body").GetString()!), Is.EqualTo(payload));
        });
    }

    [Test]
    public void A_failed_outgoing_call_is_archived_with_its_error_and_still_fails_for_the_caller()
    {
        using var provider = OutboundServices(_ => throw new HttpRequestException("connection refused"));
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("partner");

        Assert.That(() => client.GetAsync("https://partner.example/orders"), Throws.TypeOf<HttpRequestException>());

        var response = _store.Blobs.Single().Lines[1];
        Assert.Multiple(() =>
        {
            Assert.That(response.GetProperty("error").GetString(), Is.EqualTo("System.Net.Http.HttpRequestException"));
            Assert.That(response.TryGetProperty("statusCode", out _), Is.False);
        });
    }

    [Test]
    public async Task An_outgoing_call_succeeds_when_the_archive_is_down()
    {
        _store.Fail = _ => new InvalidOperationException("storage down");
        using var provider = OutboundServices(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("partner").GetAsync("https://partner.example/orders");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(LogIds(provider), Does.Contain(3002));
        });
    }

    // ---- Health check ----

    [Test]
    public async Task The_health_check_is_healthy_when_every_container_is_reachable_and_searchable()
    {
        var result = await HealthCheck(ValidOptions(new PayloadArchiveEndpoint { PathPrefix = "/api", ContainerName = "api-archive" }), "calls-archive")
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
            Assert.That(result.Data.Keys, Is.EquivalentTo(new[] { "api-archive", "calls-archive" }));
            Assert.That(_store.Operations, Is.EqualTo(new[] { "access", "tags", "access", "tags" }));
            Assert.That(_store.AlwaysSuppressed, Is.True);
        });
    }

    [Test]
    public async Task An_unreachable_container_is_degraded_not_unhealthy_and_its_tag_query_is_skipped()
    {
        _store.Fail = operation => operation == "access" ? new UnauthorizedAccessException("no token\nstack details") : null;

        var result = await HealthCheck(ValidOptions(new PayloadArchiveEndpoint { PathPrefix = "/api", ContainerName = "api-archive" }))
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(HealthStatus.Degraded));
            Assert.That(result.Data["api-archive"], Is.EqualTo("unreachable: no token"));
            Assert.That(_store.Operations, Is.EqualTo(new[] { "access" }));
        });
    }

    [Test]
    public async Task A_container_whose_blobs_cannot_be_found_by_tag_is_degraded()
    {
        _store.Fail = operation => operation == "tags" ? new InvalidOperationException("This request is not authorized") : null;

        var result = await HealthCheck(ValidOptions(new PayloadArchiveEndpoint { PathPrefix = "/api", ContainerName = "api-archive" }))
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(HealthStatus.Degraded));
            Assert.That((string)result.Data["api-archive"], Does.StartWith("finding blobs by tag is not allowed"));
        });
    }

    [Test]
    public async Task The_registered_health_check_reports_degraded_through_the_health_check_service()
    {
        _store.Fail = operation => operation == "access" ? new InvalidOperationException("storage down") : null;
        var configuration = Configuration(new()
        {
            ["PayloadArchive:ServiceUri"] = ServiceUri,
            ["PayloadArchive:Endpoints:0:PathPrefix"] = "/api",
            ["PayloadArchive:Endpoints:0:ContainerName"] = "api-archive",
        });
        using var provider = Services(configuration).BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.That(report.Entries["payload-archive"].Status, Is.EqualTo(HealthStatus.Degraded));
    }

    // ---- Helpers ----

    private static PayloadArchiveOptions ValidOptions(params PayloadArchiveEndpoint[] endpoints) =>
        new() { ServiceUri = new Uri(ServiceUri), Endpoints = endpoints };

    private static PayloadRecord Record(string phase) =>
        new() { Direction = PayloadDirection.Inbound, Phase = phase, Body = new string('x', 40) };

    private static IConfigurationRoot Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private ServiceCollection Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddFakeLogging());
        services.AddSingleton<IPayloadArchiveStore>(_store);
        services.AddPayloadArchive(configuration);
        return services;
    }

    private ServiceProvider OutboundServices(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var services = Services(Configuration(new() { ["PayloadArchive:ServiceUri"] = ServiceUri }));
        services.AddHttpClient("partner")
            .AddPayloadArchive("calls-archive")
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(respond));
        return services.BuildServiceProvider();
    }

    private static IEnumerable<int> LogIds(IServiceProvider provider) =>
        provider.GetRequiredService<FakeLogCollector>().GetSnapshot().Select(r => r.Id.Id);

    private static void ReloadIgnoringValidation(IConfigurationRoot configuration)
    {
        // The options monitor rebuilds the options inside the change callback, so an invalid change surfaces
        // here as well as on every later read. A file-based provider swallows it; the archive must too.
        try
        {
            configuration.Reload();
        }
        catch (Exception exception) when (exception is OptionsValidationException or AggregateException)
        {
        }
    }

    private PayloadArchiveWriter Writer(PayloadArchiveOptions options, InMemoryPayloadArchiveStore? store = null) =>
        new(store ?? _store, new FixedOptionsMonitor(options), TimeProvider.System, new FakeLogger<PayloadArchiveWriter>(_logs));

    private PayloadArchiveMiddleware Middleware(PayloadArchiveWriter writer, RequestDelegate next) =>
        new(next, writer, TimeProvider.System, new FakeLogger<PayloadArchiveMiddleware>(_logs));

    private PayloadArchiveHealthCheck HealthCheck(PayloadArchiveOptions options, params string[] outboundContainers) =>
        new(_store, new FixedOptionsMonitor(options), outboundContainers.Select(name => new OutboundArchiveContainer(name)));

    private static (DefaultHttpContext Context, DeferredResponseFeature Response) Context(string method, string path, string body)
    {
        var response = new DeferredResponseFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(response);
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example");
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return (context, response);
    }

    /// <summary>A response that is only "sent" when the test says so, like a server finishing the response.</summary>
    private sealed class DeferredResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onCompleted = [];

        public override void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public override void OnCompleted(Func<object, Task> callback, object state) => _onCompleted.Add((callback, state));

        public async Task CompleteAsync()
        {
            for (var i = _onCompleted.Count - 1; i >= 0; i--)
            {
                await _onCompleted[i].Callback(_onCompleted[i].State);
            }
        }
    }

    private sealed class FixedOptionsMonitor(PayloadArchiveOptions options) : IOptionsMonitor<PayloadArchiveOptions>
    {
        public PayloadArchiveOptions CurrentValue => options;

        public PayloadArchiveOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<PayloadArchiveOptions, string?> listener) => null;
    }

    private sealed class ThrowingOptionsMonitor : IOptionsMonitor<PayloadArchiveOptions>
    {
        public PayloadArchiveOptions CurrentValue => throw new OptionsValidationException(
            PayloadArchiveOptions.SectionName, typeof(PayloadArchiveOptions), ["invalid"]);

        public PayloadArchiveOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<PayloadArchiveOptions, string?> listener) => null;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
