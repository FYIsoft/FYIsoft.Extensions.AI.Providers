using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Microsoft.Extensions.AI.Decision1.Internal;

/// <summary>Request serialization and response parsing for <c>POST /providers/microsoft/v1/systemone</c>.</summary>
internal static class Decision1Wire
{
    /// <summary>Validates the questions and writes the request body. Throws <see cref="ArgumentException"/> before any network call.</summary>
    public static byte[] WriteRequest(
        string deployment,
        Decision1State state,
        IEnumerable<KeyValuePair<string, Decision1Question>> questions,
        JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        if (state.IsEmpty)
            throw new ArgumentException("State is required. Supply text, a JSON object, or Decision1State.FromObject(value).", nameof(state));

        var list = new List<KeyValuePair<string, Decision1Question>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, question) in questions)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Question names must not be null, empty or whitespace.", nameof(questions));
            if (question is null)
                throw new ArgumentException($"Question '{name}' is null.", nameof(questions));
            if (!names.Add(name))
                throw new ArgumentException($"Question name '{name}' appears more than once. Names must be unique.", nameof(questions));
            list.Add(new(name, question));
        }
        if (list.Count == 0)
            throw new ArgumentException("At least one question is required.", nameof(questions));

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", deployment);
            writer.WritePropertyName("state");
            state.WriteTo(writer, serializerOptions);
            writer.WriteStartObject("questions");
            foreach (var (name, question) in list)
            {
                writer.WritePropertyName(name);
                question.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static Decision1Response ParseResponse(byte[] body, Decision1ResponseMetadata metadata)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Malformed("the body is not a JSON object", metadata);

            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";

            if (!root.TryGetProperty("answers", out var answersElement) || answersElement.ValueKind != JsonValueKind.Object)
                throw Malformed("the answers object is missing", metadata);

            var answers = new OrderedDictionary<string, Decision1Answer>(StringComparer.Ordinal);
            foreach (var property in answersElement.EnumerateObject())
                answers[property.Name] = ParseAnswer(property.Name, property.Value.Clone(), metadata);

            var usage = new Decision1Usage(0, 0);
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                usage = new Decision1Usage(ReadInt(u, "input_tokens"), ReadInt(u, "output_tokens"));

            return new Decision1Response(model, new ReadOnlyDictionary<string, Decision1Answer>(answers), usage, metadata);
        }
        catch (JsonException ex)
        {
            throw new Decision1Exception($"The Decision1 service returned invalid JSON.{RequestIdSuffix(metadata)}", ex);
        }
    }

    /// <summary>
    /// Writes a <c>noul</c> answer object in the service's shape: <c>{"noul":p,"type":"noul"}</c>.
    /// Keys follow the service's alphabetical order (see the probe fixtures).
    /// </summary>
    public static JsonElement WriteNoulAnswer(double noul) => WriteAnswer(writer =>
    {
        writer.WriteNumber("noul", noul);
        writer.WriteString("type", "noul");
    });

    /// <summary>
    /// Writes a <c>choice</c> answer object in the service's shape:
    /// <c>{"choice":label,"confidence":c,"probabilities":{label:p,...},"type":"choice"}</c>.
    /// </summary>
    public static JsonElement WriteChoiceAnswer(string choice, double confidence, IEnumerable<KeyValuePair<string, double>> probabilities) => WriteAnswer(writer =>
    {
        writer.WriteString("choice", choice);
        writer.WriteNumber("confidence", confidence);
        writer.WriteStartObject("probabilities");
        foreach (var (label, probability) in probabilities)
            writer.WriteNumber(label, probability);
        writer.WriteEndObject();
        writer.WriteString("type", "choice");
    });

    /// <summary>
    /// Writes a <c>score</c> answer object in the service's shape:
    /// <c>{"confidence":c,"legend":{"0":label,...},"probabilities":{"0":p,...},"score":s,"type":"score"}</c>.
    /// </summary>
    public static JsonElement WriteScoreAnswer(double score, double confidence, IEnumerable<Decision1ScoreLevel> levels) => WriteAnswer(writer =>
    {
        writer.WriteNumber("confidence", confidence);
        writer.WriteStartObject("legend");
        foreach (var level in levels)
            writer.WriteString(level.Index.ToString(CultureInfo.InvariantCulture), level.Label);
        writer.WriteEndObject();
        writer.WriteStartObject("probabilities");
        foreach (var level in levels)
            writer.WriteNumber(level.Index.ToString(CultureInfo.InvariantCulture), level.Probability);
        writer.WriteEndObject();
        writer.WriteNumber("score", score);
        writer.WriteString("type", "score");
    });

    private static JsonElement WriteAnswer(Action<Utf8JsonWriter> writeProperties)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Builds the typed answer for one answer object, exactly as a response body is parsed.</summary>
    public static Decision1Answer ParseAnswer(string name, JsonElement answer, Decision1ResponseMetadata metadata)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
            throw Malformed($"answer '{name}' has no type", metadata);

        var type = t.GetString()!;
        switch (type)
        {
            case "noul":
                return new Decision1NoulAnswer(name, answer, RequireDouble(answer, "noul", name, metadata));

            case "choice":
            {
                if (!answer.TryGetProperty("choice", out var c) || c.ValueKind != JsonValueKind.String)
                    throw Malformed($"choice answer '{name}' has no choice", metadata);
                var probabilities = new OrderedDictionary<string, double>(StringComparer.Ordinal);
                if (answer.TryGetProperty("probabilities", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    foreach (var option in p.EnumerateObject())
                        probabilities[option.Name] = AsDouble(option.Value, name, metadata);
                }
                return new Decision1ChoiceAnswer(name, answer, c.GetString()!, RequireDouble(answer, "confidence", name, metadata),
                    new ReadOnlyDictionary<string, double>(probabilities));
            }

            case "score":
            {
                if (!answer.TryGetProperty("legend", out var legend) || legend.ValueKind != JsonValueKind.Object)
                    throw Malformed($"score answer '{name}' has no legend", metadata);
                if (!answer.TryGetProperty("probabilities", out var p) || p.ValueKind != JsonValueKind.Object)
                    throw Malformed($"score answer '{name}' has no probabilities", metadata);

                var labels = new SortedDictionary<int, string>();
                foreach (var entry in legend.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || entry.Value.ValueKind != JsonValueKind.String)
                        throw Malformed($"score answer '{name}' has an invalid legend entry '{entry.Name}'", metadata);
                    labels[index] = entry.Value.GetString()!;
                }
                var levelProbabilities = new Dictionary<int, double>();
                foreach (var entry in p.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                        throw Malformed($"score answer '{name}' has an invalid probability key '{entry.Name}'", metadata);
                    levelProbabilities[index] = AsDouble(entry.Value, name, metadata);
                }

                var levels = new List<Decision1ScoreLevel>(labels.Count);
                var expected = 0;
                foreach (var (index, label) in labels)
                {
                    if (index != expected++)
                        throw Malformed($"score answer '{name}' legend indexes are not contiguous from 0", metadata);
                    if (!levelProbabilities.TryGetValue(index, out var probability))
                        throw Malformed($"score answer '{name}' has no probability for level {index}", metadata);
                    levels.Add(new Decision1ScoreLevel(index, label, probability));
                }
                if (levels.Count == 0)
                    throw Malformed($"score answer '{name}' has an empty legend", metadata);

                return new Decision1ScoreAnswer(name, answer, RequireDouble(answer, "score", name, metadata),
                    RequireDouble(answer, "confidence", name, metadata), levels.AsReadOnly());
            }

            default:
                return new Decision1UnknownAnswer(name, type, answer);
        }
    }

    /// <summary>Builds the typed exception for a non-success response.</summary>
    public static Decision1ApiException CreateApiException(HttpStatusCode status, string? reasonPhrase, byte[] body, HttpResponseHeaders headers, Decision1ResponseMetadata metadata, TimeSpan? retryAfter)
    {
        string? code = null, message = null, details = null;
        try
        {
            if (body.Length > 0)
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    code = ReadText(error, "code");
                    message = ReadText(error, "message");
                    details = ReadText(error, "details");
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON error bodies (for example a gateway page) are not surfaced; the status and headers are.
        }

        code = string.IsNullOrWhiteSpace(code) ? Header(headers, "x-ms-error-code") : code;
        message = string.IsNullOrWhiteSpace(message)
            ? $"HTTP {(int)status} {reasonPhrase ?? status.ToString()}".TrimEnd()
            : message;
        var requestId = metadata.RequestId;

        return (int)status switch
        {
            400 => new Decision1BadRequestException(code, message, details, requestId, metadata),
            401 => new Decision1AuthenticationException(code, message, details, requestId, metadata),
            403 => new Decision1PermissionDeniedException(code, message, details, requestId, metadata),
            404 => new Decision1NotFoundException(code, message, details, requestId, metadata),
            422 => new Decision1UnprocessableEntityException(code, message, details, requestId, metadata),
            429 => new Decision1RateLimitException(code, message, details, requestId, retryAfter, metadata),
            >= 500 and <= 599 => new Decision1ServerException(status, code, message, details, requestId, metadata),
            _ => new Decision1ApiException(status, code, message, details, requestId, metadata),
        };
    }

    public static Decision1ResponseMetadata ReadMetadata(HttpResponseMessage response, int attempts) => new()
    {
        StatusCode = (int)response.StatusCode,
        RequestId = Header(response.Headers, "apim-request-id") ?? Header(response.Headers, "x-request-id") ?? Header(response.Headers, "x-ms-client-request-id"),
        Region = Header(response.Headers, "x-ms-region"),
        RateLimitLimitRequests = HeaderInt(response.Headers, "x-ratelimit-limit-requests"),
        RateLimitRemainingRequests = HeaderInt(response.Headers, "x-ratelimit-remaining-requests"),
        RateLimitResetRequests = HeaderSeconds(response.Headers, "x-ratelimit-reset-requests"),
        RateLimitRenewalPeriodRequests = HeaderSeconds(response.Headers, "x-ratelimit-renewalperiod-requests"),
        Attempts = attempts,
    };

    /// <summary>
    /// The server's requested delay: <c>retry-after-ms</c>, then <c>Retry-After</c> (seconds or HTTP date),
    /// then <c>x-ratelimit-reset-requests</c> for 429 only. Null when absent or not positive.
    /// </summary>
    public static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var headers = response.Headers;
        if (Header(headers, "retry-after-ms") is { } ms &&
            double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0)
            return TimeSpan.FromMilliseconds(milliseconds);

        if (headers.RetryAfter is RetryConditionHeaderValue retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
                return delta;
            if (retryAfter.Date is { } date && date > now)
                return date - now;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests &&
            HeaderSeconds(headers, "x-ratelimit-reset-requests") is { } reset && reset > TimeSpan.Zero)
            return reset;

        return null;
    }

    internal static string RequestIdSuffix(Decision1ResponseMetadata metadata) =>
        metadata.RequestId is null ? "" : $" Request id: {metadata.RequestId}.";

    private static Decision1Exception Malformed(string reason, Decision1ResponseMetadata metadata) =>
        new($"The Decision1 service returned an unexpected response: {reason}.{RequestIdSuffix(metadata)}");

    private static double RequireDouble(JsonElement obj, string property, string name, Decision1ResponseMetadata metadata) =>
        obj.TryGetProperty(property, out var value)
            ? AsDouble(value, name, metadata)
            : throw Malformed($"answer '{name}' has no {property}", metadata);

    private static double AsDouble(JsonElement value, string name, Decision1ResponseMetadata metadata) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
            ? d
            : throw Malformed($"answer '{name}' has a non-numeric value", metadata);

    private static int ReadInt(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : 0;

    private static string? ReadText(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
    }

    private static string? Header(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() : null;

    private static int? HeaderInt(HttpResponseHeaders headers, string name) =>
        Header(headers, name) is { } raw && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static TimeSpan? HeaderSeconds(HttpResponseHeaders headers, string name) =>
        Header(headers, name) is { } raw && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
