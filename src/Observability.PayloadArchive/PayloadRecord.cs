using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Observability.PayloadArchive;

/// <summary>Where a payload travelled, as written to the archive.</summary>
internal static class PayloadDirection
{
    public const string Inbound = "inbound";
    public const string Outbound = "outbound";
}

/// <summary>
/// One line of the archive: the request or the response of one HTTP exchange. Properties are written in this
/// order and null properties are left out, so a record reads top-down: who, what, then the body.
/// </summary>
internal sealed class PayloadRecord
{
    public const string RequestPhase = "request";
    public const string ResponsePhase = "response";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Keep XML and JSON bodies readable: the default encoder escapes <, > and quotes for HTML, and these
        // lines are never embedded in a web page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public required string Direction { get; init; }

    public required string Phase { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public string? Method { get; init; }

    public string? Url { get; init; }

    public string? ClientIp { get; init; }

    public int? StatusCode { get; init; }

    public double? DurationMs { get; init; }

    /// <summary>The exception type when the exchange failed; never the message, which may contain payload data.</summary>
    public string? Error { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public string? ContentType { get; init; }

    public string? BodyEncoding { get; init; }

    public string? Body { get; init; }

    /// <summary>The record as UTF-8 JSON followed by a newline.</summary>
    public ReadOnlyMemory<byte> ToJsonLine()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonOptions.Encoder }))
        {
            JsonSerializer.Serialize(writer, this, JsonOptions);
        }

        buffer.Write("\n"u8);
        return buffer.WrittenMemory;
    }

    public static double Milliseconds(TimeSpan elapsed) => Math.Round(elapsed.TotalMilliseconds, 1);
}

/// <summary>How a body is stored: as readable text when it is text, as base64 when it is anything else.</summary>
internal static class PayloadBody
{
    public const string Text = "text";
    public const string Base64 = "base64";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Text for <c>text/*</c>, JSON, XML and form content that decodes cleanly in its charset; base64 for
    /// everything else, including content without a content type. Decoding strictly matters: a lenient decoder
    /// turns invalid bytes into replacement characters and the original payload is lost for good.
    /// </summary>
    public static (string? Body, string? Encoding) Encode(ReadOnlySpan<byte> body, string? contentType)
    {
        if (body.IsEmpty)
        {
            return (null, null);
        }

        if (TextEncoding(contentType) is { } encoding)
        {
            try
            {
                return (encoding.GetString(body), Text);
            }
            catch (DecoderFallbackException)
            {
                // Labelled as text but not valid in its charset: keep the exact bytes instead.
            }
        }

        return (Convert.ToBase64String(body), Base64);
    }

    private static Encoding? TextEncoding(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.MediaType is not { } mediaType)
        {
            return null;
        }

        var isText = mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        if (!isText)
        {
            return null;
        }

        var charset = parsed.CharSet?.Trim('"');
        if (string.IsNullOrEmpty(charset) || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return StrictUtf8;
        }

        try
        {
            return Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            // A charset this runtime does not know: base64 keeps the bytes.
            return null;
        }
    }
}
