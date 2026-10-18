using System.Text.Json;

namespace Microsoft.Extensions.AI.Decision1;

/// <summary>
/// A typed question sent to Microsoft-Decision-1. Create instances with <see cref="Noul(string)"/>,
/// <see cref="Choice(string, IEnumerable{KeyValuePair{string, string}})"/> or
/// <see cref="Score(string, IEnumerable{string})"/>.
/// </summary>
/// <remarks>
/// The factory methods apply the service's limits before any network call and throw
/// <see cref="ArgumentException"/> when a definition would be rejected. Instances are immutable.
/// </remarks>
public abstract class Decision1Question
{
    /// <summary>The fewest options a choice question accepts.</summary>
    public const int MinChoiceOptions = 2;

    /// <summary>The most options a choice question accepts.</summary>
    public const int MaxChoiceOptions = 255;

    /// <summary>The fewest levels a score question accepts.</summary>
    public const int MinScoreLevels = 2;

    /// <summary>The most levels a score question accepts.</summary>
    public const int MaxScoreLevels = 10;

    private protected Decision1Question(string type, string instructions)
    {
        Type = type;
        Instructions = instructions;
    }

    /// <summary>The wire type: <c>noul</c>, <c>choice</c> or <c>score</c>.</summary>
    public string Type { get; }

    /// <summary>The question the model answers about the state.</summary>
    public string Instructions { get; }

    /// <summary>Creates a true-or-false question answered with a probability from 0 through 1.</summary>
    /// <param name="instructions">The question, for example "Does this reply promise a refund?".</param>
    /// <exception cref="ArgumentException">The instructions are null, empty or whitespace.</exception>
    public static Decision1NoulQuestion Noul(string instructions) =>
        new(RequireInstructions(instructions), null, null);

    /// <summary>
    /// Creates a true-or-false question with explicit criteria for the true and false conditions.
    /// </summary>
    /// <param name="instructions">The question.</param>
    /// <param name="whenTrue">What makes the answer true.</param>
    /// <param name="whenFalse">What makes the answer false.</param>
    /// <exception cref="ArgumentException">Any argument is null, empty or whitespace.</exception>
    public static Decision1NoulQuestion Noul(string instructions, string whenTrue, string whenFalse) =>
        new(RequireInstructions(instructions), RequireText(whenTrue, nameof(whenTrue)), RequireText(whenFalse, nameof(whenFalse)));

    /// <summary>
    /// Creates a question that selects exactly one option. Each option is a label and a description;
    /// order is preserved on the wire.
    /// </summary>
    /// <param name="instructions">The question, for example "Which team should handle this ticket?".</param>
    /// <param name="options">Label to description pairs, for example a <see cref="Dictionary{TKey, TValue}"/>.</param>
    /// <exception cref="ArgumentException">
    /// The instructions are empty, there are fewer than 2 or more than 255 options, a label is empty
    /// or repeated, or a description is null.
    /// </exception>
    public static Decision1ChoiceQuestion Choice(string instructions, IEnumerable<KeyValuePair<string, string>> options)
    {
        var text = RequireInstructions(instructions);
        ArgumentNullException.ThrowIfNull(options);

        var list = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (label, description) in options)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("Choice option labels must not be null, empty or whitespace.", nameof(options));
            if (description is null)
                throw new ArgumentException($"Choice option '{label}' has a null description.", nameof(options));
            if (!seen.Add(label))
                throw new ArgumentException($"Choice option '{label}' appears more than once. Labels must be unique.", nameof(options));
            list.Add(new(label, description));
        }

        if (list.Count is < MinChoiceOptions or > MaxChoiceOptions)
            throw new ArgumentException(
                $"A choice question requires {MinChoiceOptions} to {MaxChoiceOptions} options; {list.Count} supplied.", nameof(options));

        return new Decision1ChoiceQuestion(text, list);
    }

    /// <summary>
    /// Creates a question that places the state on an ordered scale. List levels from lowest to highest.
    /// </summary>
    /// <param name="instructions">The question, for example "How severe is this incident?".</param>
    /// <param name="levels">The level labels, lowest first.</param>
    /// <exception cref="ArgumentException">
    /// The instructions are empty, there are fewer than 2 or more than 10 levels, or a level label is
    /// empty or repeated.
    /// </exception>
    public static Decision1ScoreQuestion Score(string instructions, params IEnumerable<string> levels)
    {
        var text = RequireInstructions(instructions);
        ArgumentNullException.ThrowIfNull(levels);

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in levels)
        {
            if (string.IsNullOrWhiteSpace(level))
                throw new ArgumentException("Score level labels must not be null, empty or whitespace.", nameof(levels));
            if (!seen.Add(level))
                throw new ArgumentException($"Score level '{level}' appears more than once. Labels must be unique.", nameof(levels));
            list.Add(level);
        }

        if (list.Count is < MinScoreLevels or > MaxScoreLevels)
            throw new ArgumentException(
                $"A score question requires {MinScoreLevels} to {MaxScoreLevels} levels; {list.Count} supplied.", nameof(levels));

        return new Decision1ScoreQuestion(text, list);
    }

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", Type);
        writer.WriteString("instructions", Instructions);
        WriteCriteria(writer);
        writer.WriteEndObject();
    }

    private protected abstract void WriteCriteria(Utf8JsonWriter writer);

    private static string RequireInstructions(string instructions) => RequireText(instructions, nameof(instructions));

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value must not be null, empty or whitespace.", parameterName);
        return value;
    }
}

/// <summary>A true-or-false question (<c>noul</c>). Create with <see cref="Decision1Question.Noul(string)"/>.</summary>
public sealed class Decision1NoulQuestion : Decision1Question
{
    internal Decision1NoulQuestion(string instructions, string? whenTrue, string? whenFalse)
        : base("noul", instructions)
    {
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
    }

    /// <summary>The optional criterion for a true answer.</summary>
    public string? WhenTrue { get; }

    /// <summary>The optional criterion for a false answer.</summary>
    public string? WhenFalse { get; }

    private protected override void WriteCriteria(Utf8JsonWriter writer)
    {
        if (WhenTrue is null || WhenFalse is null)
            return;
        writer.WriteStartObject("criteria");
        writer.WriteString("true", WhenTrue);
        writer.WriteString("false", WhenFalse);
        writer.WriteEndObject();
    }
}

/// <summary>
/// A question that selects one option (<c>choice</c>). Create with
/// <see cref="Decision1Question.Choice(string, IEnumerable{KeyValuePair{string, string}})"/>.
/// </summary>
public sealed class Decision1ChoiceQuestion : Decision1Question
{
    internal Decision1ChoiceQuestion(string instructions, IReadOnlyList<KeyValuePair<string, string>> options)
        : base("choice", instructions)
    {
        Options = options;
        Labels = options.Select(o => o.Key).ToArray();
    }

    /// <summary>The options in request order, as label and description pairs.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Options { get; }

    /// <summary>The option labels in request order.</summary>
    public IReadOnlyList<string> Labels { get; }

    private protected override void WriteCriteria(Utf8JsonWriter writer)
    {
        // The service requires a JSON object (label -> description). An array is rejected with HTTP 400.
        writer.WriteStartObject("criteria");
        foreach (var (label, description) in Options)
            writer.WriteString(label, description);
        writer.WriteEndObject();
    }
}

/// <summary>
/// A question that places the state on an ordered scale (<c>score</c>). Create with
/// <see cref="Decision1Question.Score(string, IEnumerable{string})"/>.
/// </summary>
public sealed class Decision1ScoreQuestion : Decision1Question
{
    internal Decision1ScoreQuestion(string instructions, IReadOnlyList<string> levels)
        : base("score", instructions) => Levels = levels;

    /// <summary>The level labels, lowest first. Index 0 is the lowest level.</summary>
    public IReadOnlyList<string> Levels { get; }

    private protected override void WriteCriteria(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("criteria");
        foreach (var level in Levels)
            writer.WriteStringValue(level);
        writer.WriteEndArray();
    }
}
