using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Microsoft.Extensions.AI.Jev;

/// <summary>
/// Body of <c>POST /v1/systemone</c>.
/// </summary>
public sealed class SystemOneRequest
{
    /// <summary>What to judge: text, or any JSON object or array.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonNode? State { get; set; }

    /// <summary>Model alias or versioned ID. Null uses <see cref="JevClient.DefaultModel"/>.</summary>
    public string? Model { get; set; }

    /// <summary>Questions keyed by caller-chosen IDs. The model never sees the keys; answers return under them.</summary>
    public IDictionary<string, JevQuestion> Questions { get; set; } = new Dictionary<string, JevQuestion>();

    /// <summary>Extra top-level properties passed through as-is.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// Response of <c>POST /v1/systemone</c>.
/// </summary>
public sealed class SystemOneResponse
{
    /// <summary>The versioned model that answered, even when an alias was requested.</summary>
    public required string Model { get; init; }

    /// <summary>Answers keyed by the question IDs from the request.</summary>
    public required IReadOnlyDictionary<string, JevAnswer> Answers { get; init; }

    /// <summary>Token usage. Only input tokens are billed.</summary>
    public required JevUsage Usage { get; init; }

    /// <summary>The <c>x-typesafe-request-id</c> response header.</summary>
    [JsonIgnore]
    public string? RequestId { get; internal set; }

    /// <summary>Gets an answer by question ID as its concrete type.</summary>
    /// <exception cref="KeyNotFoundException">No answer has that ID.</exception>
    /// <exception cref="InvalidCastException">The answer is a different type.</exception>
    public T GetAnswer<T>(string questionId) where T : JevAnswer =>
        Answers.TryGetValue(questionId, out var answer)
            ? answer as T ?? throw new InvalidCastException(
                $"Answer '{questionId}' is of type '{answer.Type}', not {typeof(T).Name}.")
            : throw new KeyNotFoundException($"No answer for question '{questionId}'.");
}

/// <summary>Token usage for a System One call.</summary>
public sealed class JevUsage
{
    /// <summary>Input tokens (billed).</summary>
    public int InputTokens { get; init; }

    /// <summary>Output tokens (free).</summary>
    public int OutputTokens { get; init; }
}

/// <summary>An entry from <c>GET /v1/models</c>.</summary>
public sealed class JevModelCard
{
    /// <summary>Model name or alias, e.g. <c>jev-latest</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Release date as reported by the API.</summary>
    public string ReleaseDate { get; init; } = string.Empty;
}

internal sealed class JevModelList
{
    public List<JevModelCard>? Models { get; init; }
}
