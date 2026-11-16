using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Observability.PayloadArchive;

/// <summary>
/// Archives the raw request and response of matching inbound requests. The request record is written before
/// the endpoint runs, so a request that crashes the process is still on disk; the response record is appended
/// after it. With <see cref="PayloadArchiveEndpoint.WriteAfterResponse"/> both are written once the response
/// has been sent.
/// </summary>
internal sealed class PayloadArchiveMiddleware(
    RequestDelegate next,
    PayloadArchiveWriter writer,
    TimeProvider timeProvider,
    ILogger<PayloadArchiveMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var options = writer.CurrentOptions();
        var endpoint = options is null ? null : PayloadPathFilter.Match(options.EffectiveEndpoints, context.Request.Path);
        if (options is null || endpoint is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var started = timeProvider.GetTimestamp();
        byte[] requestBody;
        try
        {
            requestBody = await ReadRequestBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Too large, a broken connection: the endpoint decides what that means, not the archive.
            Log.ReadFailed(logger, exception, PayloadDirection.Inbound);
            await next(context).ConfigureAwait(false);
            return;
        }

        var activity = context.Features.Get<IHttpActivityFeature>()?.Activity ?? Activity.Current;
        var entry = writer.Begin(endpoint.ContainerName, PayloadDirection.Inbound, context.Request.Method, activity?.TraceId ?? default);

        // Captured now: the endpoint may still change the request headers.
        var requestRecord = RequestRecord(context, activity, requestBody, options);
        byte[]? responseBody = null;
        Exception? failure = null;

        if (endpoint.WriteAfterResponse)
        {
            // OnCompleted runs once the last byte has been sent, so writing adds no latency. The response
            // headers are final by then, including those that OnStarting callbacks added.
            context.Response.OnCompleted(async () =>
            {
                await writer.WriteAsync(entry, requestRecord).ConfigureAwait(false);
                await writer.WriteAsync(entry, ResponseRecord(context, activity, responseBody, failure, started, options)).ConfigureAwait(false);
            });
        }
        else
        {
            await writer.WriteAsync(entry, requestRecord).ConfigureAwait(false);
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context).ConfigureAwait(false);

            // Copying starts the response, which runs the OnStarting callbacks; read the headers after it.
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            responseBody = buffer.ToArray();
        }
        catch (Exception exception)
        {
            // Whatever the endpoint wrote before it failed is discarded: the client never gets it either.
            failure = exception;
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
            if (!endpoint.WriteAfterResponse)
            {
                await writer.WriteAsync(entry, ResponseRecord(context, activity, responseBody, failure, started, options)).ConfigureAwait(false);
            }
        }
    }

    private static async Task<byte[]> ReadRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        // Buffering lets the endpoint read the body again; large bodies spill to a temporary file.
        request.EnableBuffering();
        try
        {
            using var copy = new MemoryStream();
            await request.Body.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            return copy.ToArray();
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private PayloadRecord RequestRecord(HttpContext context, Activity? activity, byte[] body, PayloadArchiveOptions options)
    {
        var request = context.Request;
        var (encodedBody, encoding) = PayloadBody.Encode(body, request.ContentType);
        var url = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, request.Path, request.QueryString);
        return new PayloadRecord
        {
            Direction = PayloadDirection.Inbound,
            Phase = PayloadRecord.RequestPhase,
            Timestamp = timeProvider.GetUtcNow(),
            TraceId = activity?.TraceId.ToHexString(),
            SpanId = activity?.SpanId.ToHexString(),
            Method = request.Method,
            Url = PayloadRedaction.Url(url, options.EffectiveRedactedQueryParameters),
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
            Headers = PayloadRedaction.Headers(request.Headers, options.EffectiveRedactedHeaders),
            ContentType = request.ContentType,
            BodyEncoding = encoding,
            Body = encodedBody,
        };
    }

    private PayloadRecord ResponseRecord(HttpContext context, Activity? activity, byte[]? body, Exception? failure, long started, PayloadArchiveOptions options)
    {
        var response = context.Response;
        var (encodedBody, encoding) = PayloadBody.Encode(body, response.ContentType);
        return new PayloadRecord
        {
            Direction = PayloadDirection.Inbound,
            Phase = PayloadRecord.ResponsePhase,
            Timestamp = timeProvider.GetUtcNow(),
            TraceId = activity?.TraceId.ToHexString(),
            SpanId = activity?.SpanId.ToHexString(),
            StatusCode = response.HasStarted || failure is null ? response.StatusCode : null,
            DurationMs = PayloadRecord.Milliseconds(timeProvider.GetElapsedTime(started)),
            Error = failure?.GetType().FullName,
            Headers = PayloadRedaction.Headers(response.Headers, options.EffectiveRedactedHeaders),
            ContentType = response.ContentType,
            BodyEncoding = encoding,
            Body = encodedBody,
        };
    }
}
