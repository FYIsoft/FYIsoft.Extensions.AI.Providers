using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI.Decision1.Internal;

namespace Microsoft.Extensions.AI.Decision1;

/// <summary>
/// Creates Decision1 response and answer objects for unit tests, for example to return from a fake
/// <see cref="IDecision1Client"/>. <see cref="Decision1Response"/> and the answer types have no public
/// constructors; this factory is the only public way to build them.
/// </summary>
/// <remarks>
/// <para>
/// Each answer is built the way the client builds a parsed one: the factory writes the answer JSON
/// in the service's shape and parses it with the SDK's own response parser, so
/// <see cref="Decision1Answer.RawJson"/> and every typed property match what a real response with
/// the same values would produce.
/// </para>
/// <para>
/// Inputs are validated with the same limits as <see cref="Decision1Question"/> and throw
/// <see cref="ArgumentException"/> (or a subclass) when the service could not have sent them.
/// </para>
/// </remarks>
public static class Decision1ModelFactory
{
    /// <summary>The model name the service reports in <see cref="Decision1Response.Model"/>.</summary>
    public const string DefaultModel = "microsoft-decision-1";

    // Malformed-answer errors carry the request id from metadata; a factory-built answer has none.
    private static readonly Decision1ResponseMetadata NoMetadata = new();

    /// <summary>Creates the answer to a true-or-false (<c>noul</c>) question.</summary>
    /// <param name="name">The question name.</param>
    /// <param name="noul">The probability, from 0 through 1, that the answer is true.</param>
    /// <exception cref="ArgumentException">The name is empty or <paramref name="noul"/> is outside 0 through 1.</exception>
    public static Decision1NoulAnswer NoulAnswer(string name, double noul)
    {
        RequireName(name);
        RequireProbability(noul, nameof(noul));
        return (Decision1NoulAnswer)Decision1Wire.ParseAnswer(name, Decision1Wire.WriteNoulAnswer(noul), NoMetadata);
    }

    /// <summary>Creates the answer to a <c>choice</c> question.</summary>
    /// <param name="name">The question name.</param>
    /// <param name="choice">The selected label. Must be one of the labels in <paramref name="probabilities"/>.</param>
    /// <param name="probabilities">The probability of every option, keyed by label, in the order the answer returns them.</param>
    /// <param name="confidence">
    /// The service's confidence in <paramref name="choice"/>. The service computes it separately and it
    /// cannot be derived from the probabilities, so the default is the probability of <paramref name="choice"/>.
    /// Pass a value when the code under test depends on it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The name is empty; there are fewer than 2 or more than 255 options; a label is empty or repeated;
    /// a probability or <paramref name="confidence"/> is outside 0 through 1; or <paramref name="choice"/>
    /// is not one of the labels.
    /// </exception>
    public static Decision1ChoiceAnswer ChoiceAnswer(
        string name,
        string choice,
        IEnumerable<KeyValuePair<string, double>> probabilities,
        double? confidence = null)
    {
        RequireName(name);
        ArgumentNullException.ThrowIfNull(probabilities);

        var list = new List<KeyValuePair<string, double>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (label, probability) in probabilities)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("Choice option labels must not be null, empty or whitespace.", nameof(probabilities));
            if (!seen.Add(label))
                throw new ArgumentException($"Choice option '{label}' appears more than once. Labels must be unique.", nameof(probabilities));
            if (!IsProbability(probability))
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The probability of choice option '{label}' is {probability}; it must be from 0 through 1."),
                    nameof(probabilities));
            list.Add(new(label, probability));
        }

        if (list.Count is < Decision1Question.MinChoiceOptions or > Decision1Question.MaxChoiceOptions)
            throw new ArgumentException(
                $"A choice answer requires {Decision1Question.MinChoiceOptions} to {Decision1Question.MaxChoiceOptions} options; {list.Count} supplied.",
                nameof(probabilities));

        if (choice is null || !seen.Contains(choice))
            throw new ArgumentException(
                $"The choice '{choice}' is not one of the option labels: {string.Join(", ", list.Select(o => o.Key))}.", nameof(choice));

        var selected = confidence ?? list.First(o => string.Equals(o.Key, choice, StringComparison.Ordinal)).Value;
        RequireProbability(selected, nameof(confidence));

        return (Decision1ChoiceAnswer)Decision1Wire.ParseAnswer(name, Decision1Wire.WriteChoiceAnswer(choice, selected, list), NoMetadata);
    }

    /// <summary>Creates the answer to a <c>score</c> question from the level labels and their probabilities.</summary>
    /// <param name="name">The question name.</param>
    /// <param name="legend">The level labels, lowest first, as in <see cref="Decision1Question.Score(string, IEnumerable{string})"/>.</param>
    /// <param name="probabilities">The probability of each level, in the same order as <paramref name="legend"/>.</param>
    /// <param name="score">
    /// The probability-weighted level index. Defaults to the value the service defines: the
    /// probability-weighted average of the level indexes.
    /// </param>
    /// <param name="confidence">
    /// The service's confidence in the score. The service computes it separately and it cannot be
    /// derived from the probabilities, so the default is the probability of the most probable level
    /// (the lowest index wins ties). Pass a value when the code under test depends on it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The name is empty; there are fewer than 2 or more than 10 levels; a label is empty or repeated;
    /// the legend and probabilities differ in length; a probability or <paramref name="confidence"/> is
    /// outside 0 through 1; <paramref name="score"/> is outside 0 through the highest level index; or the
    /// score is omitted and every probability is 0.
    /// </exception>
    public static Decision1ScoreAnswer ScoreAnswer(
        string name,
        IEnumerable<string> legend,
        IEnumerable<double> probabilities,
        double? score = null,
        double? confidence = null)
    {
        RequireName(name);
        ArgumentNullException.ThrowIfNull(legend);
        ArgumentNullException.ThrowIfNull(probabilities);

        var labels = legend.ToList();
        var values = probabilities.ToList();
        if (labels.Count != values.Count)
            throw new ArgumentException(
                $"The legend has {labels.Count} levels but {values.Count} probabilities were supplied; supply one probability per level.",
                nameof(probabilities));

        return CreateScoreAnswer(name, labels.Select((label, index) => new Decision1ScoreLevel(index, label, values[index])),
            score, confidence, labelParameter: nameof(legend), probabilityParameter: nameof(probabilities));
    }

    /// <summary>Creates the answer to a <c>score</c> question from its levels.</summary>
    /// <param name="name">The question name.</param>
    /// <param name="levels">Every level in index order (lowest first). Indexes must run 0, 1, 2 and so on.</param>
    /// <param name="score">
    /// The probability-weighted level index. Defaults to the value the service defines: the
    /// probability-weighted average of the level indexes.
    /// </param>
    /// <param name="confidence">
    /// The service's confidence in the score. Defaults to the probability of the most probable level
    /// (the lowest index wins ties), because the service computes it separately and it cannot be derived
    /// from the probabilities. Pass a value when the code under test depends on it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The name is empty; there are fewer than 2 or more than 10 levels; a level is null, out of index
    /// order, or has an empty or repeated label; a probability or <paramref name="confidence"/> is outside
    /// 0 through 1; <paramref name="score"/> is outside 0 through the highest level index; or the score is
    /// omitted and every probability is 0.
    /// </exception>
    public static Decision1ScoreAnswer ScoreAnswer(
        string name,
        IEnumerable<Decision1ScoreLevel> levels,
        double? score = null,
        double? confidence = null)
    {
        RequireName(name);
        ArgumentNullException.ThrowIfNull(levels);
        return CreateScoreAnswer(name, levels, score, confidence, labelParameter: nameof(levels), probabilityParameter: nameof(levels));
    }

    private static Decision1ScoreAnswer CreateScoreAnswer(
        string name,
        IEnumerable<Decision1ScoreLevel> levels,
        double? score,
        double? confidence,
        string labelParameter,
        string probabilityParameter)
    {
        var list = new List<Decision1ScoreLevel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in levels)
        {
            if (level is null)
                throw new ArgumentException("Score levels must not be null.", labelParameter);
            if (level.Index != list.Count)
                throw new ArgumentException(
                    $"Score level '{level.Label}' has index {level.Index}; levels must be in index order starting at 0, so {list.Count} was expected.",
                    labelParameter);
            if (string.IsNullOrWhiteSpace(level.Label))
                throw new ArgumentException("Score level labels must not be null, empty or whitespace.", labelParameter);
            if (!seen.Add(level.Label))
                throw new ArgumentException($"Score level '{level.Label}' appears more than once. Labels must be unique.", labelParameter);
            if (!IsProbability(level.Probability))
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The probability of score level '{level.Label}' is {level.Probability}; it must be from 0 through 1."),
                    probabilityParameter);
            list.Add(level);
        }

        if (list.Count is < Decision1Question.MinScoreLevels or > Decision1Question.MaxScoreLevels)
            throw new ArgumentException(
                $"A score answer requires {Decision1Question.MinScoreLevels} to {Decision1Question.MaxScoreLevels} levels; {list.Count} supplied.",
                labelParameter);

        var highestIndex = list.Count - 1;
        double weighted;
        if (score is { } explicitScore)
        {
            if (!(explicitScore >= 0 && explicitScore <= highestIndex))
                throw new ArgumentOutOfRangeException(nameof(score), explicitScore,
                    $"The score must be from 0 through {highestIndex}, the highest level index.");
            weighted = explicitScore;
        }
        else
        {
            var total = list.Sum(l => l.Probability);
            if (total <= 0)
                throw new ArgumentException(
                    "Every level probability is 0, so the probability-weighted score is undefined. Pass a score or a positive probability.",
                    probabilityParameter);
            // The service defines Score as the probability-weighted average of the level indexes. Dividing by the
            // total keeps it on the scale when test probabilities do not sum to exactly 1.
            weighted = Math.Clamp(list.Sum(l => l.Index * l.Probability) / total, 0, highestIndex);
        }

        var selected = confidence ?? list.MaxBy(l => l.Probability)!.Probability;
        RequireProbability(selected, nameof(confidence));

        return (Decision1ScoreAnswer)Decision1Wire.ParseAnswer(name, Decision1Wire.WriteScoreAnswer(weighted, selected, list), NoMetadata);
    }

    /// <summary>
    /// Creates an answer of a type this SDK version does not recognize, as the client returns for a new
    /// answer type from the service.
    /// </summary>
    /// <param name="name">The question name.</param>
    /// <param name="type">The wire type. Must not be <c>noul</c>, <c>choice</c> or <c>score</c>, which parse to typed answers.</param>
    /// <param name="rawJson">
    /// The answer object as the service would send it. It must be a JSON object. Its <c>type</c> property,
    /// when present, must equal <paramref name="type"/>; when absent it is added, because every service
    /// answer carries one. Defaults to <c>{"type":"&lt;type&gt;"}</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The name or type is empty; the type is a known type; or <paramref name="rawJson"/> is not an object
    /// or names a different type.
    /// </exception>
    public static Decision1UnknownAnswer UnknownAnswer(string name, string type, JsonElement? rawJson = null)
    {
        RequireName(name);
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("The answer type must not be null, empty or whitespace.", nameof(type));
        if (type is "noul" or "choice" or "score")
            throw new ArgumentException(
                $"'{type}' is a known answer type. Use {char.ToUpperInvariant(type[0])}{type[1..]}Answer to create it.", nameof(type));

        JsonElement answer;
        if (rawJson is { } raw)
        {
            if (raw.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"The raw answer must be a JSON object, not {raw.ValueKind}.", nameof(rawJson));
            if (raw.TryGetProperty("type", out var rawType) &&
                (rawType.ValueKind != JsonValueKind.String || !string.Equals(rawType.GetString(), type, StringComparison.Ordinal)))
                throw new ArgumentException(
                    $"The raw answer's type is {rawType.GetRawText()}, which does not match the type '{type}'.", nameof(rawJson));
            answer = raw.TryGetProperty("type", out _) ? raw.Clone() : WithType(raw, type);
        }
        else
        {
            answer = WithType(default, type);
        }

        return (Decision1UnknownAnswer)Decision1Wire.ParseAnswer(name, answer, NoMetadata);
    }

    /// <summary>Creates token usage.</summary>
    /// <param name="inputTokens">Input tokens (<c>usage.input_tokens</c>).</param>
    /// <param name="outputTokens">Output tokens (<c>usage.output_tokens</c>).</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public static Decision1Usage Usage(int inputTokens = 0, int outputTokens = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        return new Decision1Usage(inputTokens, outputTokens);
    }

    /// <summary>Creates response metadata. Every header value defaults to absent (null).</summary>
    /// <param name="statusCode">HTTP status code of the final attempt. Defaults to 200.</param>
    /// <param name="requestId">The service request id.</param>
    /// <param name="region">The serving region, for example "East US 2".</param>
    /// <param name="rateLimitLimitRequests">Requests allowed per renewal period.</param>
    /// <param name="rateLimitRemainingRequests">Requests left in the current period.</param>
    /// <param name="rateLimitResetRequests">Time until the request limit resets.</param>
    /// <param name="rateLimitRenewalPeriodRequests">Length of the rate-limit period.</param>
    /// <param name="attempts">HTTP attempts, including the final one. Defaults to 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The status code is outside 100 through 599, <paramref name="attempts"/> is less than 1, or a rate-limit
    /// value is negative.
    /// </exception>
    public static Decision1ResponseMetadata ResponseMetadata(
        int statusCode = 200,
        string? requestId = null,
        string? region = null,
        int? rateLimitLimitRequests = null,
        int? rateLimitRemainingRequests = null,
        TimeSpan? rateLimitResetRequests = null,
        TimeSpan? rateLimitRenewalPeriodRequests = null,
        int attempts = 1)
    {
        if (statusCode is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "The status code must be from 100 through 599.");
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        if (rateLimitLimitRequests < 0)
            throw new ArgumentOutOfRangeException(nameof(rateLimitLimitRequests), rateLimitLimitRequests, "The value must not be negative.");
        if (rateLimitRemainingRequests < 0)
            throw new ArgumentOutOfRangeException(nameof(rateLimitRemainingRequests), rateLimitRemainingRequests, "The value must not be negative.");
        if (rateLimitResetRequests < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(rateLimitResetRequests), rateLimitResetRequests, "The value must not be negative.");
        if (rateLimitRenewalPeriodRequests < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(rateLimitRenewalPeriodRequests), rateLimitRenewalPeriodRequests, "The value must not be negative.");

        return new Decision1ResponseMetadata
        {
            StatusCode = statusCode,
            RequestId = requestId,
            Region = region,
            RateLimitLimitRequests = rateLimitLimitRequests,
            RateLimitRemainingRequests = rateLimitRemainingRequests,
            RateLimitResetRequests = rateLimitResetRequests,
            RateLimitRenewalPeriodRequests = rateLimitRenewalPeriodRequests,
            Attempts = attempts,
        };
    }

    /// <summary>Creates a response holding the given answers, keyed by <see cref="Decision1Answer.Name"/> in the given order.</summary>
    /// <param name="model">The underlying model name. Use <see cref="DefaultModel"/> for the value the service reports.</param>
    /// <param name="answers">The answers, typically created with this factory.</param>
    /// <param name="usage">Token usage. Defaults to 0 input and 0 output tokens, as when the service omits usage.</param>
    /// <param name="metadata">Response metadata. Defaults to <see cref="ResponseMetadata"/> with its defaults (HTTP 200, one attempt).</param>
    /// <exception cref="ArgumentException">An answer is null or two answers share a name.</exception>
    public static Decision1Response Response(
        string model,
        IEnumerable<Decision1Answer> answers,
        Decision1Usage? usage = null,
        Decision1ResponseMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(answers);

        var byName = new OrderedDictionary<string, Decision1Answer>(StringComparer.Ordinal);
        foreach (var answer in answers)
        {
            if (answer is null)
                throw new ArgumentException("Answers must not be null.", nameof(answers));
            if (!byName.TryAdd(answer.Name, answer))
                throw new ArgumentException($"Answer name '{answer.Name}' appears more than once. Names must be unique.", nameof(answers));
        }

        return new Decision1Response(model, new ReadOnlyDictionary<string, Decision1Answer>(byName),
            usage ?? Usage(), metadata ?? ResponseMetadata());
    }

    private static void RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Answer names must not be null, empty or whitespace.", nameof(name));
    }

    private static bool IsProbability(double value) => value is >= 0 and <= 1;

    private static void RequireProbability(double value, string parameterName)
    {
        if (!IsProbability(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be from 0 through 1.");
    }

    // Returns a copy of the object with "type" added after its own properties (or just {"type":...} when
    // the source is not an object).
    private static JsonElement WithType(JsonElement source, string type)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (source.ValueKind == JsonValueKind.Object)
                foreach (var property in source.EnumerateObject())
                    property.WriteTo(writer);
            writer.WriteString("type", type);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
