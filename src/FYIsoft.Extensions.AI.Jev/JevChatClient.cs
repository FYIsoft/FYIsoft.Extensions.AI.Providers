using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Extensions.AI.Jev;

/// <summary>
/// An <see cref="IChatClient"/> adapter over <see cref="JevClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Jev answers typed questions about a state; it does not generate text, stream, or call tools.
/// This adapter maps the chat abstraction onto one System One call:
/// </para>
/// <list type="bullet">
/// <item>
/// <strong>State</strong>: a single user text message is sent as a plain string. Anything else (several messages,
/// system messages, <see cref="ChatOptions.Instructions"/>) is sent as
/// <c>{"messages":[{"from":"user","text":"..."}]}</c>. Non-text content is rejected.
/// </item>
/// <item>
/// <strong>Questions</strong>: taken from <see cref="JevChatOptionsExtensions.WithJevQuestions"/> when present.
/// Otherwise derived from a JSON-schema <see cref="ChatOptions.ResponseFormat"/>, so
/// <c>chatClient.GetResponseAsync&lt;T&gt;(...)</c> works for records whose properties are
/// <see cref="bool"/> (noul) or enums (choice). A property's <c>[Description]</c> becomes its instructions.
/// </item>
/// <item>
/// <strong>Response text</strong>: for schema-derived questions, a JSON object matching the schema
/// (noul → <c>true</c> when p ≥ 0.5, choice → the chosen label). For explicit questions, the raw answers JSON.
/// The full <see cref="SystemOneResponse"/> is always on <see cref="ChatResponse.RawRepresentation"/>.
/// </item>
/// <item><strong>Streaming</strong> yields the complete response as a single update.</item>
/// <item><strong>Tools</strong> in <see cref="ChatOptions.Tools"/> are ignored.</item>
/// </list>
/// </remarks>
public sealed class JevChatClient : IChatClient
{
    private readonly JevClient _client;
    private readonly string? _modelId;
    private readonly bool _disposeClient;

    /// <summary>
    /// Creates the adapter.
    /// </summary>
    /// <param name="client">The underlying Jev client.</param>
    /// <param name="modelId">Default model; falls back to <see cref="JevClient.DefaultModel"/>.</param>
    /// <param name="disposeClient">Whether disposing this adapter disposes <paramref name="client"/>.</param>
    public JevChatClient(JevClient client, string? modelId = null, bool disposeClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _modelId = modelId;
        _disposeClient = disposeClient;
        Metadata = new ChatClientMetadata("typesafe", client.BaseUrl, modelId ?? client.DefaultModel);
    }

    /// <summary>Provider metadata.</summary>
    public ChatClientMetadata Metadata { get; }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var (questions, schemaKinds) = ResolveQuestions(options);
        var request = new SystemOneRequest
        {
            State = BuildState(messages, options?.Instructions),
            Model = options?.ModelId ?? _modelId,
            Questions = questions,
        };

        var result = await _client.SystemOneAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = schemaKinds is null
            ? JsonSerializer.Serialize(result.Answers, JevJson.SerializerOptions)
            : BuildSchemaJson(result, schemaKinds).ToJsonString();

        var message = new ChatMessage(ChatRole.Assistant, text)
        {
            MessageId = result.RequestId,
            RawRepresentation = result,
        };

        return new ChatResponse(message)
        {
            ResponseId = result.RequestId,
            ModelId = result.Model,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = result,
            Usage = new UsageDetails
            {
                InputTokenCount = result.Usage.InputTokens,
                OutputTokenCount = result.Usage.OutputTokens,
                TotalTokenCount = result.Usage.InputTokens + result.Usage.OutputTokens,
            },
        };
    }

    /// <inheritdoc/>
    /// <remarks>Jev does not stream; this yields the complete response as one update.</remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return Metadata;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceType.IsInstanceOfType(_client) ? _client : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposeClient)
        {
            _client.Dispose();
        }
    }

    internal static JsonNode BuildState(IEnumerable<ChatMessage> messages, string? instructions)
    {
        var entries = new List<(ChatRole Role, string Text)>();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            entries.Add((ChatRole.System, instructions));
        }

        foreach (var message in messages)
        {
            if (message.Contents.FirstOrDefault(c => c is DataContent or UriContent or HostedFileContent) is { } unsupported)
            {
                throw new NotSupportedException(
                    $"Jev accepts text input only; message content of type {unsupported.GetType().Name} is not supported.");
            }

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                entries.Add((message.Role, message.Text));
            }
        }

        if (entries.Count == 0)
        {
            throw new ArgumentException("At least one message with text is required.", nameof(messages));
        }

        if (entries is [{ Role: var role, Text: var only }] && role == ChatRole.User)
        {
            return JsonValue.Create(only);
        }

        return new JsonObject
        {
            ["messages"] = new JsonArray(entries
                .Select(e => (JsonNode)new JsonObject { ["from"] = e.Role.Value, ["text"] = e.Text })
                .ToArray()),
        };
    }

    private static (IDictionary<string, JevQuestion> Questions, IReadOnlyDictionary<string, string>? SchemaKinds) ResolveQuestions(
        ChatOptions? options)
    {
        if (options?.GetJevQuestions() is { Count: > 0 } explicitQuestions)
        {
            return (explicitQuestions, null);
        }

        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema })
        {
            return QuestionsFromSchema(schema);
        }

        throw new InvalidOperationException(
            "Jev needs questions. Pass them with ChatOptions.WithJevQuestions(...) or request structured output " +
            "(e.g. GetResponseAsync<T>) where T has bool and enum properties.");
    }

    internal static (IDictionary<string, JevQuestion> Questions, IReadOnlyDictionary<string, string> Kinds) QuestionsFromSchema(
        JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            throw new NotSupportedException("The response schema must be an object with properties.");
        }

        var questions = new Dictionary<string, JevQuestion>();
        var kinds = new Dictionary<string, string>();

        foreach (var property in properties.EnumerateObject())
        {
            var instructions = property.Value.TryGetProperty("description", out var description)
                && description.GetString() is { Length: > 0 } text
                    ? text
                    : property.Name;

            if (property.Value.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                var labels = values.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)
                    .ToList();
                questions[property.Name] = JevQuestion.Choice(instructions, labels);
                kinds[property.Name] = "choice";
            }
            else if (HasType(property.Value, "boolean"))
            {
                questions[property.Name] = JevQuestion.Noul(instructions);
                kinds[property.Name] = "noul";
            }
            else
            {
                throw new NotSupportedException(
                    $"Property '{property.Name}' cannot be mapped to a Jev question. " +
                    "Only bool (noul) and enum (choice) properties are supported; use WithJevQuestions for score questions.");
            }
        }

        return (questions, kinds);
    }

    private static bool HasType(JsonElement schema, string type) =>
        schema.TryGetProperty("type", out var value) && value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() == type,
            JsonValueKind.Array => value.EnumerateArray().Any(t => t.GetString() == type),
            _ => false,
        };

    private static JsonObject BuildSchemaJson(SystemOneResponse response, IReadOnlyDictionary<string, string> kinds)
    {
        var result = new JsonObject();
        foreach (var (name, kind) in kinds)
        {
            result[name] = kind == "noul"
                ? JsonValue.Create(response.GetAnswer<JevNoulAnswer>(name).IsYes)
                : JsonValue.Create(response.GetAnswer<JevChoiceAnswer>(name).Choice);
        }

        return result;
    }
}

/// <summary>
/// <see cref="ChatOptions"/> helpers for passing Jev questions through <see cref="IChatClient"/>.
/// </summary>
public static class JevChatOptionsExtensions
{
    /// <summary>The <see cref="ChatOptions.AdditionalProperties"/> key holding Jev questions.</summary>
    public const string QuestionsKey = "jev.questions";

    /// <summary>Sets the questions Jev answers for this request.</summary>
    public static ChatOptions WithJevQuestions(this ChatOptions options, IDictionary<string, JevQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(questions);

        (options.AdditionalProperties ??= [])[QuestionsKey] = questions;
        return options;
    }

    /// <summary>Gets the questions set by <see cref="WithJevQuestions"/>, if any.</summary>
    public static IDictionary<string, JevQuestion>? GetJevQuestions(this ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.AdditionalProperties?.TryGetValue(QuestionsKey, out var value) == true
            ? value as IDictionary<string, JevQuestion>
            : null;
    }

    /// <summary>Gets the <see cref="SystemOneResponse"/> behind a response from <see cref="JevChatClient"/>.</summary>
    public static SystemOneResponse? AsJevResponse(this ChatResponse response) =>
        response?.RawRepresentation as SystemOneResponse;
}
