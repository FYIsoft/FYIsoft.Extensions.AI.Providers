using System.Collections.ObjectModel;
using System.Text.Json;

namespace Microsoft.Extensions.AI.Decision1;

/// <summary>
/// An answer to one question. Cast to <see cref="Decision1NoulAnswer"/>, <see cref="Decision1ChoiceAnswer"/>
/// or <see cref="Decision1ScoreAnswer"/>, or use <see cref="Decision1Response.GetAnswer{T}(string)"/>.
/// </summary>
public abstract class Decision1Answer
{
    private protected Decision1Answer(string name, string type, JsonElement rawJson)
    {
        Name = name;
        Type = type;
        RawJson = rawJson;
    }

    /// <summary>The question name from the request.</summary>
    public string Name { get; }

    /// <summary>The wire type: <c>noul</c>, <c>choice</c>, <c>score</c>, or a type this SDK does not know.</summary>
    public string Type { get; }

    /// <summary>The answer object exactly as returned by the service.</summary>
    public JsonElement RawJson { get; }
}

/// <summary>The answer to a true-or-false (<c>noul</c>) question.</summary>
public sealed class Decision1NoulAnswer : Decision1Answer
{
    internal Decision1NoulAnswer(string name, JsonElement rawJson, double noul)
        : base(name, "noul", rawJson) => Noul = noul;

    /// <summary>The probability, from 0 through 1, that the answer is true.</summary>
    public double Noul { get; }

    /// <summary>
    /// Applies a caller-chosen threshold to <see cref="Noul"/>. Validate the threshold against labeled
    /// examples before you automate a gate.
    /// </summary>
    /// <param name="threshold">The minimum probability treated as true. Defaults to 0.5.</param>
    public bool IsTrue(double threshold = 0.5) => Noul >= threshold;
}

/// <summary>The answer to a <c>choice</c> question.</summary>
public sealed class Decision1ChoiceAnswer : Decision1Answer
{
    internal Decision1ChoiceAnswer(string name, JsonElement rawJson, string choice, double confidence, IReadOnlyDictionary<string, double> probabilities)
        : base(name, "choice", rawJson)
    {
        Choice = choice;
        Confidence = confidence;
        Probabilities = probabilities;
    }

    /// <summary>The selected option label.</summary>
    public string Choice { get; }

    /// <summary>The service's confidence in <see cref="Choice"/>.</summary>
    public double Confidence { get; }

    /// <summary>The probability of every option, keyed by label, in the order the service returned them.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; }

    /// <summary>Returns the probability of one option.</summary>
    /// <exception cref="KeyNotFoundException">The label is not in the answer.</exception>
    public double GetProbability(string label) =>
        Probabilities.TryGetValue(label, out var p)
            ? p
            : throw new KeyNotFoundException($"Choice answer '{Name}' has no option '{label}'. Options: {string.Join(", ", Probabilities.Keys)}.");
}

/// <summary>One level of a score scale with its probability.</summary>
/// <param name="Index">The zero-based level index; 0 is the lowest level.</param>
/// <param name="Label">The level label from the request.</param>
/// <param name="Probability">The probability of this level.</param>
public sealed record Decision1ScoreLevel(int Index, string Label, double Probability);

/// <summary>The answer to a <c>score</c> question.</summary>
public sealed class Decision1ScoreAnswer : Decision1Answer
{
    internal Decision1ScoreAnswer(string name, JsonElement rawJson, double score, double confidence, IReadOnlyList<Decision1ScoreLevel> levels)
        : base(name, "score", rawJson)
    {
        Score = score;
        Confidence = confidence;
        Levels = levels;
        Legend = levels.Select(l => l.Label).ToArray();
        Probabilities = levels.Select(l => l.Probability).ToArray();

        var byLabel = new OrderedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var level in levels)
            byLabel.TryAdd(level.Label, level.Probability);
        ProbabilitiesByLabel = new ReadOnlyDictionary<string, double>(byLabel);
    }

    /// <summary>
    /// The probability-weighted average of the level indexes, from 0 through <c>Levels.Count - 1</c>.
    /// The value can fall between levels.
    /// </summary>
    public double Score { get; }

    /// <summary>The service's confidence in the score.</summary>
    public double Confidence { get; }

    /// <summary>Every level in index order (lowest first), with its label and probability.</summary>
    public IReadOnlyList<Decision1ScoreLevel> Levels { get; }

    /// <summary>The level labels in index order, as returned in the service's <c>legend</c>.</summary>
    public IReadOnlyList<string> Legend { get; }

    /// <summary>The per-level probabilities in index order.</summary>
    public IReadOnlyList<double> Probabilities { get; }

    /// <summary>The per-level probabilities keyed by level label, in index order.</summary>
    public IReadOnlyDictionary<string, double> ProbabilitiesByLabel { get; }

    /// <summary>The level with the highest probability (client-derived; the lowest index wins ties).</summary>
    public Decision1ScoreLevel MostProbableLevel => Levels.MaxBy(l => l.Probability)!;

    /// <summary>The level nearest to <see cref="Score"/> (client-derived: <see cref="Score"/> rounded, clamped to the scale).</summary>
    public Decision1ScoreLevel NearestLevel =>
        Levels[Math.Clamp((int)Math.Round(Score, MidpointRounding.AwayFromZero), 0, Levels.Count - 1)];

    /// <summary>Returns the probability of the level with the given label.</summary>
    /// <exception cref="KeyNotFoundException">The label is not in the legend.</exception>
    public double GetProbability(string label) =>
        ProbabilitiesByLabel.TryGetValue(label, out var p)
            ? p
            : throw new KeyNotFoundException($"Score answer '{Name}' has no level '{label}'. Levels: {string.Join(", ", Legend)}.");
}

/// <summary>An answer whose type this SDK version does not recognize. Inspect <see cref="Decision1Answer.RawJson"/>.</summary>
public sealed class Decision1UnknownAnswer : Decision1Answer
{
    internal Decision1UnknownAnswer(string name, string type, JsonElement rawJson)
        : base(name, type, rawJson)
    {
    }
}
