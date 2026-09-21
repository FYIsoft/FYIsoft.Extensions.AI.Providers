using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Extensions.AI.Jev;

/// <summary>Base type for every error raised by <see cref="JevClient"/>.</summary>
/// <remarks>Caller cancellation surfaces as <see cref="OperationCanceledException"/>, not as a <see cref="JevException"/>.</remarks>
public class JevException : Exception
{
    /// <summary>Creates the exception.</summary>
    public JevException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The API returned a non-success status code.</summary>
public class JevApiException : JevException
{
    private const int MaxRawBodyInMessage = 200;

    /// <summary>Creates the exception.</summary>
    public JevApiException(
        HttpStatusCode statusCode,
        string message,
        HttpResponseHeaders? headers = null,
        JsonNode? body = null,
        string? rawBody = null,
        string? requestId = null)
        : base(message)
    {
        StatusCode = statusCode;
        Headers = headers;
        Body = body;
        RawBody = rawBody;
        RequestId = requestId;
    }

    /// <summary>HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Response headers.</summary>
    public HttpResponseHeaders? Headers { get; }

    /// <summary>Response body parsed as JSON, when it was JSON.</summary>
    public JsonNode? Body { get; }

    /// <summary>Response body as text.</summary>
    public string? RawBody { get; }

    /// <summary>The <c>x-typesafe-request-id</c> response header.</summary>
    public string? RequestId { get; }

    internal static JevApiException Create(HttpResponseMessage response, byte[] content, string? requestId)
    {
        var status = response.StatusCode;
        var rawBody = content.Length == 0 ? null : Encoding.UTF8.GetString(content);
        var body = TryParseJson(rawBody);
        var message = $"{(int)status} {ExtractDetail(body, rawBody)}";
        var headers = response.Headers;

        return (int)status switch
        {
            400 => new JevBadRequestException(message, headers, body, rawBody, requestId),
            401 => new JevAuthenticationException(message, headers, body, rawBody, requestId),
            403 => new JevPermissionDeniedException(message, headers, body, rawBody, requestId),
            404 => new JevNotFoundException(message, headers, body, rawBody, requestId),
            422 => new JevUnprocessableEntityException(message, headers, body, rawBody, requestId),
            429 => new JevRateLimitException(message, headers, body, rawBody, requestId, JevClient.ReadRetryAfter(response)),
            >= 500 => new JevInternalServerException(status, message, headers, body, rawBody, requestId),
            _ => new JevApiException(status, message, headers, body, rawBody, requestId),
        };
    }

    private static JsonNode? TryParseJson(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(rawBody);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Port of the JS SDK's <c>extractMessage</c>.</summary>
    private static string ExtractDetail(JsonNode? body, string? rawBody)
    {
        var detail = body switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject obj => FromObject(obj),
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(detail))
        {
            return detail;
        }

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return "status code (no body)";
        }

        return rawBody.Length > MaxRawBodyInMessage ? rawBody[..MaxRawBodyInMessage] : rawBody;
    }

    private static string? FromObject(JsonObject obj)
    {
        if (StringOrMessage(obj["error"]) is { } error)
        {
            return error;
        }

        if (AsString(obj["message"]) is { } message)
        {
            return message;
        }

        return obj["detail"] switch
        {
            JsonArray items => string.Join("; ", items.OfType<JsonObject>().Select(FormatValidationItem).Where(s => s is not null)),
            var detail => StringOrMessage(detail),
        };
    }

    private static string? FormatValidationItem(JsonObject item)
    {
        var msg = AsString(item["msg"]);
        if (msg is null)
        {
            return null;
        }

        var path = item["loc"] is JsonArray loc
            ? string.Join('.', loc.Select(part => part?.ToString()).Where(part => part is not null && part != "body"))
            : string.Empty;

        return path.Length == 0 ? msg : $"{path}: {msg}";
    }

    private static string? StringOrMessage(JsonNode? node) =>
        AsString(node) ?? (node is JsonObject obj ? AsString(obj["message"]) : null);

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>HTTP 400.</summary>
public sealed class JevBadRequestException(string message, HttpResponseHeaders? headers, JsonNode? body, string? rawBody, string? requestId)
    : JevApiException(HttpStatusCode.BadRequest, message, headers, body, rawBody, requestId);

/// <summary>HTTP 401: missing or invalid API key.</summary>
public sealed class JevAuthenticationException(string message, HttpResponseHeaders? headers, JsonNode? body, string? rawBody, string? requestId)
    : JevApiException(HttpStatusCode.Unauthorized, message, headers, body, rawBody, requestId);

/// <summary>HTTP 403.</summary>
public sealed class JevPermissionDeniedException(string message, HttpResponseHeaders? headers, JsonNode? body, string? rawBody, string? requestId)
    : JevApiException(HttpStatusCode.Forbidden, message, headers, body, rawBody, requestId);

/// <summary>HTTP 404.</summary>
public sealed class JevNotFoundException(string message, HttpResponseHeaders? headers, JsonNode? body, string? rawBody, string? requestId)
    : JevApiException(HttpStatusCode.NotFound, message, headers, body, rawBody, requestId);

/// <summary>HTTP 422: request validation failed. The message names the field.</summary>
public sealed class JevUnprocessableEntityException(string message, HttpResponseHeaders? headers, JsonNode? body, string? rawBody, string? requestId)
    : JevApiException(HttpStatusCode.UnprocessableEntity, message, headers, body, rawBody, requestId);

/// <summary>HTTP 429: rate limited.</summary>
public sealed class JevRateLimitException(
    string message,
    HttpResponseHeaders? headers,
    JsonNode? body,
    string? rawBody,
    string? requestId,
    TimeSpan? retryAfter)
    : JevApiException(HttpStatusCode.TooManyRequests, message, headers, body, rawBody, requestId)
{
    /// <summary>The delay the server asked for, if any.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>HTTP 5xx, including 529 (overloaded).</summary>
public sealed class JevInternalServerException(
    HttpStatusCode statusCode,
    string message,
    HttpResponseHeaders? headers,
    JsonNode? body,
    string? rawBody,
    string? requestId)
    : JevApiException(statusCode, message, headers, body, rawBody, requestId);

/// <summary>The request could not reach the API.</summary>
public class JevConnectionException(string message = "Connection error.", Exception? innerException = null)
    : JevException(message, innerException);

/// <summary>An attempt exceeded its timeout.</summary>
public sealed class JevTimeoutException(TimeSpan timeout, Exception? innerException = null)
    : JevConnectionException($"Request timed out after {timeout.TotalMilliseconds:0}ms.", innerException)
{
    /// <summary>The per-attempt timeout that elapsed.</summary>
    public TimeSpan Timeout { get; } = timeout;
}
