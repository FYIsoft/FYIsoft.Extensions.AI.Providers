using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Microsoft.Extensions.AI.Jev;

/// <summary>
/// A typed question asked about a state. The wire discriminator is <c>type</c>.
/// </summary>
/// <remarks>
/// <see cref="Instructions"/> and criteria values are JSON entries: a string, object, array or null.
/// A <see cref="string"/> converts to <see cref="JsonNode"/> implicitly.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JevNoulQuestion), "noul")]
[JsonDerivedType(typeof(JevChoiceQuestion), "choice")]
[JsonDerivedType(typeof(JevScoreQuestion), "score")]
public abstract class JevQuestion
{
    /// <summary>Maximum options for a choice question.</summary>
    public const int MaxChoiceOptions = 255;

    /// <summary>Minimum levels for a score question.</summary>
    public const int MinScoreLevels = 2;

    /// <summary>Maximum levels for a score question.</summary>
    public const int MaxScoreLevels = 10;

    /// <summary>What to judge. Structured instructions may embed reference data.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyOrder(-1)]
    public JsonNode? Instructions { get; init; }

    /// <summary>Creates a yes/no question.</summary>
    /// <param name="instructions">What to judge.</param>
    /// <param name="whenTrue">Optional description of a yes.</param>
    /// <param name="whenFalse">Optional description of a no.</param>
    public static JevNoulQuestion Noul(JsonNode? instructions = null, JsonNode? whenTrue = null, JsonNode? whenFalse = null) => new()
    {
        Instructions = instructions,
        Criteria = whenTrue is null && whenFalse is null ? null : new JevNoulCriteria { True = whenTrue, False = whenFalse },
    };

    /// <summary>Creates a choice question whose options have no descriptions.</summary>
    public static JevChoiceQuestion Choice(JsonNode? instructions, params IEnumerable<string> labels) => new()
    {
        Instructions = instructions,
        Criteria = labels.ToDictionary(label => label, JsonNode? (_) => null),
    };

    /// <summary>Creates a choice question from option labels mapped to descriptions.</summary>
    public static JevChoiceQuestion Choice(JsonNode? instructions, IDictionary<string, JsonNode?> criteria) => new()
    {
        Instructions = instructions,
        Criteria = criteria,
    };

    /// <summary>Creates a score question from ordered level descriptions (level 0 first).</summary>
    public static JevScoreQuestion Score(JsonNode? instructions, params IEnumerable<JsonNode?> levels) => new()
    {
        Instructions = instructions,
        Criteria = levels.ToList(),
    };

    /// <summary>Throws <see cref="ArgumentException"/> when the question violates API limits.</summary>
    internal abstract void Validate(string key);
}

/// <summary>A yes/no question. The answer is the probability of yes.</summary>
public sealed class JevNoulQuestion : JevQuestion
{
    /// <summary>Optional descriptions of what counts as yes and no.</summary>
    public JevNoulCriteria? Criteria { get; init; }

    internal override void Validate(string key)
    {
    }
}

/// <summary>Descriptions of the two outcomes of a <see cref="JevNoulQuestion"/>.</summary>
public sealed class JevNoulCriteria
{
    /// <summary>What counts as yes.</summary>
    [JsonPropertyName("true")]
    public JsonNode? True { get; init; }

    /// <summary>What counts as no.</summary>
    [JsonPropertyName("false")]
    public JsonNode? False { get; init; }
}

/// <summary>A question that picks one label from a fixed set.</summary>
public sealed class JevChoiceQuestion : JevQuestion
{
    /// <summary>Option labels mapped to optional descriptions. At most 255 options.</summary>
    public required IDictionary<string, JsonNode?> Criteria { get; init; }

    internal override void Validate(string key)
    {
        if (Criteria is null || Criteria.Count == 0)
        {
            throw new ArgumentException($"Choice question '{key}' requires at least one option.");
        }

        if (Criteria.Count > MaxChoiceOptions)
        {
            throw new ArgumentException($"Choice question '{key}' has {Criteria.Count} options; the maximum is {MaxChoiceOptions}.");
        }
    }
}

/// <summary>A question that rates the state on an ordered scale.</summary>
public sealed class JevScoreQuestion : JevQuestion
{
    /// <summary>Ordered level descriptions, level 0 first. Entries may be null. 2 to 10 levels.</summary>
    public required IList<JsonNode?> Criteria { get; init; }

    internal override void Validate(string key)
    {
        var count = Criteria?.Count ?? 0;
        if (count is < MinScoreLevels or > MaxScoreLevels)
        {
            throw new ArgumentException(
                $"Score question '{key}' has {count} levels; between {MinScoreLevels} and {MaxScoreLevels} are required.");
        }
    }
}
