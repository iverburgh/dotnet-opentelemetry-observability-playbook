using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Observability.PayloadArchive;

/// <summary>
/// Archives the raw request and response of outgoing calls made with one <see cref="HttpClient"/>. The request
/// content is buffered, not replaced, so its type and headers reach the server unchanged.
/// </summary>
internal sealed class PayloadArchiveHandler(
    string containerName,
    PayloadArchiveWriter writer,
    TimeProvider timeProvider,
    ILogger<PayloadArchiveHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (writer.CurrentOptions() is not { } options)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var started = timeProvider.GetTimestamp();
        byte[]? requestBody = null;
        if (request.Content is not null)
        {
            try
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                requestBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Log.ReadFailed(logger, exception, PayloadDirection.Outbound);
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        // The caller's span: the HTTP client span is created further down the pipeline. The trace id, the key
        // you search on, is the same.
        var activity = Activity.Current;
        var entry = writer.Begin(containerName, PayloadDirection.Outbound, request.Method.Method, activity?.TraceId ?? default);
        await writer.WriteAsync(entry, RequestRecord(request, activity, requestBody, options)).ConfigureAwait(false);

        HttpResponseMessage? response = null;
        byte[] responseBody;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Buffering lets the caller read the content again. It also means a streamed download is held in
            // memory in full: do not archive clients that stream large responses.
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await writer.WriteAsync(entry, ResponseRecord(response, activity, null, exception, started, options)).ConfigureAwait(false);
            response?.Dispose();
            throw;
        }

        await writer.WriteAsync(entry, ResponseRecord(response, activity, responseBody, null, started, options)).ConfigureAwait(false);
        return response;
    }

    private PayloadRecord RequestRecord(HttpRequestMessage request, Activity? activity, byte[]? body, PayloadArchiveOptions options)
    {
        var contentType = request.Content?.Headers.ContentType?.ToString();
        var (encodedBody, encoding) = PayloadBody.Encode(body, contentType);
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = request.Content is null
            ? request.Headers
            : request.Headers.Concat(request.Content.Headers);
        return new PayloadRecord
        {
            Direction = PayloadDirection.Outbound,
            Phase = PayloadRecord.RequestPhase,
            Timestamp = timeProvider.GetUtcNow(),
            TraceId = activity?.TraceId.ToHexString(),
            SpanId = activity?.SpanId.ToHexString(),
            Method = request.Method.Method,
            Url = request.RequestUri is null ? null : PayloadRedaction.Url(request.RequestUri.ToString(), options.EffectiveRedactedQueryParameters),
            Headers = PayloadRedaction.Headers(headers, options.EffectiveRedactedHeaders),
            ContentType = contentType,
            BodyEncoding = encoding,
            Body = encodedBody,
        };
    }

    private PayloadRecord ResponseRecord(HttpResponseMessage? response, Activity? activity, byte[]? body, Exception? failure, long started, PayloadArchiveOptions options)
    {
        var contentType = response?.Content.Headers.ContentType?.ToString();
        var (encodedBody, encoding) = PayloadBody.Encode(body, contentType);
        return new PayloadRecord
        {
            Direction = PayloadDirection.Outbound,
            Phase = PayloadRecord.ResponsePhase,
            Timestamp = timeProvider.GetUtcNow(),
            TraceId = activity?.TraceId.ToHexString(),
            SpanId = activity?.SpanId.ToHexString(),
            StatusCode = response is null ? null : (int)response.StatusCode,
            DurationMs = PayloadRecord.Milliseconds(timeProvider.GetElapsedTime(started)),
            Error = failure?.GetType().FullName,
            Headers = response is null ? null : PayloadRedaction.Headers(response.Headers.Concat(response.Content.Headers), options.EffectiveRedactedHeaders),
            ContentType = contentType,
            BodyEncoding = encoding,
            Body = encodedBody,
        };
    }
}
