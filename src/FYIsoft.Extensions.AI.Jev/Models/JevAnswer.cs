using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Microsoft.Extensions.AI.Jev;

/// <summary>
/// The answer to one question. Concrete type follows the question type.
/// </summary>
public abstract class JevAnswer
{
    /// <summary>The wire type: <c>noul</c>, <c>choice</c>, <c>score</c>, or an unrecognized value.</summary>
    [JsonPropertyOrder(-1)]
    public abstract string Type { get; }
}

/// <summary>Answer to a <see cref="JevNoulQuestion"/>.</summary>
public sealed class JevNoulAnswer : JevAnswer
{
    /// <inheritdoc/>
    public override string Type => "noul";

    /// <summary>Probability of yes, 0 to 1.</summary>
    public double Noul { get; init; }

    /// <summary>True when <see cref="Noul"/> is at least 0.5.</summary>
    [JsonIgnore]
    public bool IsYes => Noul >= 0.5;
}

/// <summary>Answer to a <see cref="JevChoiceQuestion"/>.</summary>
public sealed class JevChoiceAnswer : JevAnswer
{
    /// <inheritdoc/>
    public override string Type => "choice";

    /// <summary>The highest-probability label.</summary>
    public required string Choice { get; init; }

    /// <summary>Probability of every label; sums to 1.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();

    /// <summary>Confidence, 0 to 1.</summary>
    public double Confidence { get; init; }
}

/// <summary>Answer to a <see cref="JevScoreQuestion"/>.</summary>
public sealed class JevScoreAnswer : JevAnswer
{
    /// <inheritdoc/>
    public override string Type => "score";

    /// <summary>Probability-weighted level; can fall between levels.</summary>
    public double Score { get; init; }

    /// <summary>Level index (as a string key) to its description.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Legend { get; init; } = new Dictionary<string, JsonNode?>();

    /// <summary>Level index (as a string key) to its probability.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();

    /// <summary>Confidence, 0 to 1.</summary>
    public double Confidence { get; init; }

    /// <summary>The most probable level index.</summary>
    [JsonIgnore]
    public int MostLikelyLevel =>
        Probabilities.Count == 0 ? (int)Math.Round(Score) : int.Parse(Probabilities.MaxBy(p => p.Value).Key);
}

/// <summary>An answer whose <c>type</c> this client does not recognize. The raw JSON is kept.</summary>
public sealed class JevUnknownAnswer : JevAnswer
{
    private readonly string _type;

    internal JevUnknownAnswer(string type, JsonElement raw)
    {
        _type = type;
        Raw = raw;
    }

    /// <inheritdoc/>
    public override string Type => _type;

    /// <summary>The answer as received.</summary>
    public JsonElement Raw { get; }
}

internal sealed class JevAnswerConverter : JsonConverter<JevAnswer>
{
    public override JevAnswer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()!
            : string.Empty;

        return type switch
        {
            "noul" => root.Deserialize<JevNoulAnswer>(options)!,
            "choice" => root.Deserialize<JevChoiceAnswer>(options)!,
            "score" => root.Deserialize<JevScoreAnswer>(options)!,
            _ => new JevUnknownAnswer(type, root.Clone()),
        };
    }

    public override void Write(Utf8JsonWriter writer, JevAnswer value, JsonSerializerOptions options)
    {
        if (value is JevUnknownAnswer unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
