using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Decision1.Tests;

public class Decision1ModelFactoryTests
{
    // ---- Probe fixture answers, parsed from the real response bodies -------------------------------

    private static JsonElement FixtureAnswer(ProbeFixture fixture, string name)
    {
        using var document = JsonDocument.Parse(fixture.Body);
        return document.RootElement.GetProperty("answers").GetProperty(name).Clone();
    }

    private static IReadOnlyList<string> PropertyNames(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();

    /// <summary>Same property names in the same order, same value kinds, recursively.</summary>
    private static void ShouldHaveSameShape(JsonElement actual, JsonElement expected, string path = "$")
    {
        actual.ValueKind.Should().Be(expected.ValueKind, "the value kind at {0} must match the service", path);
        if (expected.ValueKind != JsonValueKind.Object)
            return;
        PropertyNames(actual).Should().Equal(PropertyNames(expected), "the properties at {0} must match the service", path);
        foreach (var property in expected.EnumerateObject())
            ShouldHaveSameShape(actual.GetProperty(property.Name), property.Value, $"{path}.{property.Name}");
    }

    // ---- Noul --------------------------------------------------------------------------------------

    [Fact]
    public void NoulAnswer_RoundTripsValues()
    {
        var answer = Decision1ModelFactory.NoulAnswer("refund", 0.75);

        answer.Name.Should().Be("refund");
        answer.Type.Should().Be("noul");
        answer.Noul.Should().Be(0.75);
        answer.IsTrue().Should().BeTrue();
        answer.IsTrue(threshold: 0.8).Should().BeFalse();
        answer.RawJson.GetProperty("noul").GetDouble().Should().Be(0.75);
        answer.RawJson.GetProperty("type").GetString().Should().Be("noul");
    }

    [Fact]
    public void NoulAnswer_RawJson_MatchesProbeFixtureExactly()
    {
        var expected = FixtureAnswer(ProbeFixtures.CombinedEntraServices, "refund");

        var answer = Decision1ModelFactory.NoulAnswer("refund", 0.0019267495720550704);

        ShouldHaveSameShape(answer.RawJson, expected);
        answer.RawJson.GetRawText().Should().Be(expected.GetRawText());
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NoulAnswer_ProbabilityOutsideZeroToOne_Throws(double noul)
    {
        var act = () => Decision1ModelFactory.NoulAnswer("q", noul);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("noul");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void EveryAnswer_EmptyName_Throws(string? name)
    {
        var actions = new Action[]
        {
            () => Decision1ModelFactory.NoulAnswer(name!, 0.5),
            () => Decision1ModelFactory.ChoiceAnswer(name!, "a", Probabilities(("a", 0.6), ("b", 0.4))),
            () => Decision1ModelFactory.ScoreAnswer(name!, ["low", "high"], [0.5, 0.5]),
            () => Decision1ModelFactory.ScoreAnswer(name!, [new Decision1ScoreLevel(0, "low", 0.5), new Decision1ScoreLevel(1, "high", 0.5)]),
            () => Decision1ModelFactory.UnknownAnswer(name!, "ranking"),
        };

        foreach (var act in actions)
            act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("name");
    }

    // ---- Choice ------------------------------------------------------------------------------------

    private static KeyValuePair<string, double>[] Probabilities(params (string Label, double Probability)[] options) =>
        options.Select(o => new KeyValuePair<string, double>(o.Label, o.Probability)).ToArray();

    [Fact]
    public void ChoiceAnswer_RoundTripsValuesInCallerOrder()
    {
        var answer = Decision1ModelFactory.ChoiceAnswer("team", "engineering",
            Probabilities(("support", 0.05), ("engineering", 0.9), ("billing", 0.05)), confidence: 0.85);

        answer.Name.Should().Be("team");
        answer.Type.Should().Be("choice");
        answer.Choice.Should().Be("engineering");
        answer.Confidence.Should().Be(0.85);
        answer.Probabilities.Keys.Should().Equal("support", "engineering", "billing");
        answer.GetProbability("engineering").Should().Be(0.9);
        answer.RawJson.GetProperty("choice").GetString().Should().Be("engineering");
        answer.RawJson.GetProperty("confidence").GetDouble().Should().Be(0.85);
        PropertyNames(answer.RawJson.GetProperty("probabilities")).Should().Equal("support", "engineering", "billing");
    }

    [Fact]
    public void ChoiceAnswer_AcceptsADictionary()
    {
        var answer = Decision1ModelFactory.ChoiceAnswer("team", "billing",
            new Dictionary<string, double> { ["billing"] = 0.7, ["support"] = 0.3 });

        answer.Probabilities.Keys.Should().Equal("billing", "support");
    }

    [Fact]
    public void ChoiceAnswer_DefaultConfidence_IsTheChosenOptionsProbability()
    {
        var answer = Decision1ModelFactory.ChoiceAnswer("team", "billing", Probabilities(("billing", 0.7), ("support", 0.3)));

        answer.Confidence.Should().Be(0.7);
        answer.RawJson.GetProperty("confidence").GetDouble().Should().Be(0.7);
    }

    [Fact]
    public void ChoiceAnswer_RawJson_MatchesProbeFixtureExactly()
    {
        var expected = FixtureAnswer(ProbeFixtures.CombinedEntraServices, "team");

        var answer = Decision1ModelFactory.ChoiceAnswer("team", "engineering",
            Probabilities(("billing", 0.0024580428604436297), ("engineering", 0.9916454188501259), ("support", 0.005896538289430438)),
            confidence: 0.9874681282751889);

        ShouldHaveSameShape(answer.RawJson, expected);
        answer.RawJson.GetRawText().Should().Be(expected.GetRawText());
    }

    [Fact]
    public void ChoiceAnswer_ChoiceNotAmongLabels_Throws()
    {
        var act = () => Decision1ModelFactory.ChoiceAnswer("team", "legal", Probabilities(("billing", 0.5), ("support", 0.5)));

        act.Should().Throw<ArgumentException>().WithMessage("*legal*billing, support*").Which.ParamName.Should().Be("choice");
    }

    [Fact]
    public void ChoiceAnswer_ChoiceIsCaseSensitive()
    {
        var act = () => Decision1ModelFactory.ChoiceAnswer("team", "Billing", Probabilities(("billing", 0.5), ("support", 0.5)));

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("choice");
    }

    [Fact]
    public void ChoiceAnswer_NullChoice_Throws()
    {
        var act = () => Decision1ModelFactory.ChoiceAnswer("team", null!, Probabilities(("billing", 0.5), ("support", 0.5)));

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("choice");
    }

    public static TheoryData<KeyValuePair<string, double>[], string> InvalidChoiceProbabilities => new()
    {
        { Probabilities(("only", 1.0)), "*2 to 255 options; 1 supplied*" },
        { Enumerable.Range(0, 256).Select(i => new KeyValuePair<string, double>($"o{i}", 0)).ToArray(), "*2 to 255 options; 256 supplied*" },
        { Probabilities(("a", 0.5), ("a", 0.5)), "*'a' appears more than once*" },
        { Probabilities(("a", 0.5), (" ", 0.5)), "*labels must not be null, empty or whitespace*" },
        { Probabilities(("a", 0.5), ("b", 1.5)), "*'b' is 1.5; it must be from 0 through 1*" },
        { Probabilities(("a", -0.1), ("b", 0.5)), "*'a' is -0.1; it must be from 0 through 1*" },
        { Probabilities(("a", double.NaN), ("b", 0.5)), "*'a'*from 0 through 1*" },
    };

    [Theory]
    [MemberData(nameof(InvalidChoiceProbabilities))]
    public void ChoiceAnswer_InvalidProbabilities_Throw(KeyValuePair<string, double>[] probabilities, string message)
    {
        var act = () => Decision1ModelFactory.ChoiceAnswer("team", "a", probabilities);

        act.Should().Throw<ArgumentException>().WithMessage(message).Which.ParamName.Should().Be("probabilities");
    }

    [Fact]
    public void ChoiceAnswer_NullProbabilities_Throws()
    {
        var act = () => Decision1ModelFactory.ChoiceAnswer("team", "a", null!);

        act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("probabilities");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void ChoiceAnswer_ConfidenceOutsideZeroToOne_Throws(double confidence)
    {
        var act = () => Decision1ModelFactory.ChoiceAnswer("team", "a", Probabilities(("a", 0.5), ("b", 0.5)), confidence);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("confidence");
    }

    [Fact]
    public void ChoiceAnswer_AcceptsTheMaximumOptionCount()
    {
        var options = Enumerable.Range(0, Decision1Question.MaxChoiceOptions)
            .Select(i => new KeyValuePair<string, double>($"o{i}", i == 0 ? 1 : 0)).ToArray();

        Decision1ModelFactory.ChoiceAnswer("q", "o0", options).Probabilities.Should().HaveCount(255);
    }

    // ---- Score -------------------------------------------------------------------------------------

    private static readonly string[] Severity = ["Cosmetic", "Minor", "Major", "Critical"];

    [Fact]
    public void ScoreAnswer_FromLegend_RoundTripsValues()
    {
        var answer = Decision1ModelFactory.ScoreAnswer("severity", Severity, [0.1, 0.2, 0.6, 0.1], score: 1.9, confidence: 0.55);

        answer.Name.Should().Be("severity");
        answer.Type.Should().Be("score");
        answer.Score.Should().Be(1.9);
        answer.Confidence.Should().Be(0.55);
        answer.Legend.Should().Equal(Severity);
        answer.Probabilities.Should().Equal(0.1, 0.2, 0.6, 0.1);
        answer.Levels.Should().Equal(
            new Decision1ScoreLevel(0, "Cosmetic", 0.1), new Decision1ScoreLevel(1, "Minor", 0.2),
            new Decision1ScoreLevel(2, "Major", 0.6), new Decision1ScoreLevel(3, "Critical", 0.1));
        answer.ProbabilitiesByLabel["Major"].Should().Be(0.6);
        answer.GetProbability("Critical").Should().Be(0.1);
        answer.MostProbableLevel.Label.Should().Be("Major");
        answer.NearestLevel.Label.Should().Be("Major");
    }

    [Fact]
    public void ScoreAnswer_FromLevels_RoundTripsValues()
    {
        Decision1ScoreLevel[] levels = [new(0, "low", 0.25), new(1, "mid", 0.25), new(2, "high", 0.5)];

        var answer = Decision1ModelFactory.ScoreAnswer("risk", levels, confidence: 0.4);

        answer.Levels.Should().Equal(levels);
        answer.Legend.Should().Equal("low", "mid", "high");
        answer.Score.Should().Be(1.25);
        answer.Confidence.Should().Be(0.4);
    }

    [Fact]
    public void ScoreAnswer_DefaultScore_IsTheProbabilityWeightedIndex()
    {
        var answer = Decision1ModelFactory.ScoreAnswer("severity", Severity, [0.1, 0.2, 0.3, 0.4]);

        // 0*0.1 + 1*0.2 + 2*0.3 + 3*0.4 = 2.0
        answer.Score.Should().BeApproximately(2.0, 1e-12);
        answer.RawJson.GetProperty("score").GetDouble().Should().Be(answer.Score);
    }

    [Fact]
    public void ScoreAnswer_DefaultScore_MatchesTheServiceForBothProbeFixtures()
    {
        var severity = FixtureAnswer(ProbeFixtures.CombinedEntraServices, "severity");
        var materiality = FixtureAnswer(ProbeFixtures.Score10Levels, "materiality");

        foreach (var expected in new[] { severity, materiality })
        {
            var legend = expected.GetProperty("legend").EnumerateObject().Select(p => p.Value.GetString()!).ToArray();
            var probabilities = expected.GetProperty("probabilities").EnumerateObject().Select(p => p.Value.GetDouble()).ToArray();

            var answer = Decision1ModelFactory.ScoreAnswer("s", legend, probabilities);

            answer.Score.Should().BeApproximately(expected.GetProperty("score").GetDouble(), 1e-6);
        }
    }

    [Fact]
    public void ScoreAnswer_DefaultScore_NormalizesProbabilitiesThatDoNotSumToOne()
    {
        var answer = Decision1ModelFactory.ScoreAnswer("s", ["a", "b", "c"], [1, 1, 1]);

        answer.Score.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void ScoreAnswer_DefaultConfidence_IsTheMostProbableLevelsProbability()
    {
        var answer = Decision1ModelFactory.ScoreAnswer("s", ["a", "b", "c"], [0.4, 0.4, 0.2]);

        answer.Confidence.Should().Be(0.4);
        answer.MostProbableLevel.Label.Should().Be("a");
    }

    [Fact]
    public void ScoreAnswer_RawJson_MatchesProbeFixtureExactly()
    {
        var expected = FixtureAnswer(ProbeFixtures.CombinedEntraServices, "severity");

        var answer = Decision1ModelFactory.ScoreAnswer("severity", Severity,
            [0.0013079888902714876, 0.010951656893852506, 0.7677704453043187, 0.2199699089115573],
            score: 2.2064022742371616, confidence: 0.6903605937390916);

        ShouldHaveSameShape(answer.RawJson, expected);
        answer.RawJson.GetRawText().Should().Be(expected.GetRawText());
    }

    [Fact]
    public void ScoreAnswer_TenLevels_RawJsonMatchesProbeFixtureShape()
    {
        var expected = FixtureAnswer(ProbeFixtures.Score10Levels, "materiality");

        var answer = Decision1ModelFactory.ScoreAnswer("materiality", Enumerable.Range(1, 10).Select(i => i.ToString()),
            Enumerable.Repeat(0.1, 10));

        ShouldHaveSameShape(answer.RawJson, expected);
        PropertyNames(answer.RawJson.GetProperty("legend")).Should().Equal(Enumerable.Range(0, 10).Select(i => i.ToString()));
    }

    public static TheoryData<string[], double[], string, string> InvalidScoreInputs => new()
    {
        { ["only"], [1.0], "*2 to 10 levels; 1 supplied*", "legend" },
        { Enumerable.Range(1, 11).Select(i => $"l{i}").ToArray(), Enumerable.Repeat(1.0 / 11, 11).ToArray(), "*2 to 10 levels; 11 supplied*", "legend" },
        { ["a", "a"], [0.5, 0.5], "*'a' appears more than once*", "legend" },
        { ["a", ""], [0.5, 0.5], "*labels must not be null, empty or whitespace*", "legend" },
        { ["a", "b"], [0.5], "*2 levels but 1 probabilities*", "probabilities" },
        { ["a", "b"], [0.5, 1.5], "*'b' is 1.5; it must be from 0 through 1*", "probabilities" },
        { ["a", "b"], [-0.5, 0.5], "*'a' is -0.5*", "probabilities" },
        { ["a", "b"], [0, 0], "*Every level probability is 0*", "probabilities" },
    };

    [Theory]
    [MemberData(nameof(InvalidScoreInputs))]
    public void ScoreAnswer_FromLegend_InvalidInputs_Throw(string[] legend, double[] probabilities, string message, string parameter)
    {
        var act = () => Decision1ModelFactory.ScoreAnswer("s", legend, probabilities);

        act.Should().Throw<ArgumentException>().WithMessage(message).Which.ParamName.Should().Be(parameter);
    }

    [Fact]
    public void ScoreAnswer_AllZeroProbabilities_AreAllowedWithAnExplicitScore()
    {
        var answer = Decision1ModelFactory.ScoreAnswer("s", ["a", "b"], [0, 0], score: 0.5, confidence: 0);

        answer.Score.Should().Be(0.5);
        answer.Confidence.Should().Be(0);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(3.01)]
    [InlineData(double.NaN)]
    public void ScoreAnswer_ScoreOffTheScale_Throws(double score)
    {
        var act = () => Decision1ModelFactory.ScoreAnswer("s", Severity, [0.25, 0.25, 0.25, 0.25], score);

        act.Should().Throw<ArgumentException>().WithMessage("*0 through 3*").Which.ParamName.Should().Be("score");
    }

    [Fact]
    public void ScoreAnswer_ScoreOnTheTopLevel_IsAllowed()
    {
        Decision1ModelFactory.ScoreAnswer("s", Severity, [0, 0, 0, 1], score: 3).NearestLevel.Label.Should().Be("Critical");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void ScoreAnswer_ConfidenceOutsideZeroToOne_Throws(double confidence)
    {
        var act = () => Decision1ModelFactory.ScoreAnswer("s", Severity, [0.25, 0.25, 0.25, 0.25], confidence: confidence);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("confidence");
    }

    [Fact]
    public void ScoreAnswer_FromLevels_IndexesOutOfOrder_Throw()
    {
        var act = () => Decision1ModelFactory.ScoreAnswer("s", [new Decision1ScoreLevel(1, "b", 0.5), new Decision1ScoreLevel(0, "a", 0.5)]);

        act.Should().Throw<ArgumentException>().WithMessage("*index 1*0 was expected*").Which.ParamName.Should().Be("levels");
    }

    [Fact]
    public void ScoreAnswer_FromLevels_NullLevel_Throws()
    {
        var act = () => Decision1ModelFactory.ScoreAnswer("s", [new Decision1ScoreLevel(0, "a", 0.5), null!]);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("levels");
    }

    [Fact]
    public void ScoreAnswer_FromLevels_InvalidProbability_Throws()
    {
        var act = () => Decision1ModelFactory.ScoreAnswer("s", [new Decision1ScoreLevel(0, "a", 0.5), new Decision1ScoreLevel(1, "b", 2)]);

        act.Should().Throw<ArgumentException>().WithMessage("*'b' is 2*").Which.ParamName.Should().Be("levels");
    }

    // ---- Unknown -----------------------------------------------------------------------------------

    [Fact]
    public void UnknownAnswer_Default_HasOnlyTheType()
    {
        var answer = Decision1ModelFactory.UnknownAnswer("future", "ranking");

        answer.Name.Should().Be("future");
        answer.Type.Should().Be("ranking");
        answer.RawJson.GetRawText().Should().Be("""{"type":"ranking"}""");
    }

    [Fact]
    public void UnknownAnswer_KeepsTheRawJson()
    {
        using var document = JsonDocument.Parse("""{"order":["b","a"],"type":"ranking"}""");

        var answer = Decision1ModelFactory.UnknownAnswer("future", "ranking", document.RootElement);

        answer.RawJson.GetRawText().Should().Be("""{"order":["b","a"],"type":"ranking"}""");
    }

    [Fact]
    public void UnknownAnswer_AddsTheTypeWhenTheRawJsonHasNone()
    {
        using var document = JsonDocument.Parse("""{"order":["b","a"]}""");

        var answer = Decision1ModelFactory.UnknownAnswer("future", "ranking", document.RootElement);

        answer.RawJson.GetProperty("order").GetArrayLength().Should().Be(2);
        answer.RawJson.GetProperty("type").GetString().Should().Be("ranking");
    }

    [Theory]
    [InlineData("noul")]
    [InlineData("choice")]
    [InlineData("score")]
    [InlineData("")]
    [InlineData(" ")]
    public void UnknownAnswer_KnownOrEmptyType_Throws(string type)
    {
        var act = () => Decision1ModelFactory.UnknownAnswer("q", type);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("type");
    }

    [Theory]
    [InlineData("""[1,2]""")]
    [InlineData("""{"type":"other"}""")]
    [InlineData("""{"type":3}""")]
    public void UnknownAnswer_RawJsonThatIsNotAMatchingObject_Throws(string json)
    {
        using var document = JsonDocument.Parse(json);

        var act = () => Decision1ModelFactory.UnknownAnswer("q", "ranking", document.RootElement);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("rawJson");
    }

    // ---- Usage and metadata ------------------------------------------------------------------------

    [Fact]
    public void Usage_RoundTripsValues()
    {
        var usage = Decision1ModelFactory.Usage(155, 4);

        usage.Should().Be(new Decision1Usage(155, 4));
        usage.TotalTokens.Should().Be(159);
        Decision1ModelFactory.Usage().Should().Be(new Decision1Usage(0, 0));
    }

    [Theory]
    [InlineData(-1, 0, "inputTokens")]
    [InlineData(0, -1, "outputTokens")]
    public void Usage_NegativeCounts_Throw(int input, int output, string parameter)
    {
        var act = () => Decision1ModelFactory.Usage(input, output);

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(parameter);
    }

    [Fact]
    public void ResponseMetadata_RoundTripsValues()
    {
        var metadata = Decision1ModelFactory.ResponseMetadata(
            statusCode: 200,
            requestId: "req-1",
            region: "East US 2",
            rateLimitLimitRequests: 30,
            rateLimitRemainingRequests: 29,
            rateLimitResetRequests: TimeSpan.FromSeconds(2),
            rateLimitRenewalPeriodRequests: TimeSpan.FromSeconds(60),
            attempts: 2);

        metadata.StatusCode.Should().Be(200);
        metadata.RequestId.Should().Be("req-1");
        metadata.Region.Should().Be("East US 2");
        metadata.RateLimitLimitRequests.Should().Be(30);
        metadata.RateLimitRemainingRequests.Should().Be(29);
        metadata.RateLimitResetRequests.Should().Be(TimeSpan.FromSeconds(2));
        metadata.RateLimitRenewalPeriodRequests.Should().Be(TimeSpan.FromSeconds(60));
        metadata.Attempts.Should().Be(2);
    }

    [Fact]
    public void ResponseMetadata_Defaults_AreASingleSuccessfulAttemptWithNoHeaders()
    {
        var metadata = Decision1ModelFactory.ResponseMetadata();

        metadata.StatusCode.Should().Be(200);
        metadata.Attempts.Should().Be(1);
        metadata.RequestId.Should().BeNull();
        metadata.Region.Should().BeNull();
        metadata.RateLimitLimitRequests.Should().BeNull();
        metadata.RateLimitRemainingRequests.Should().BeNull();
        metadata.RateLimitResetRequests.Should().BeNull();
        metadata.RateLimitRenewalPeriodRequests.Should().BeNull();
    }

    [Fact]
    public void ResponseMetadata_InvalidValues_Throw()
    {
        var actions = new (Action Act, string Parameter)[]
        {
            (() => Decision1ModelFactory.ResponseMetadata(statusCode: 99), "statusCode"),
            (() => Decision1ModelFactory.ResponseMetadata(statusCode: 600), "statusCode"),
            (() => Decision1ModelFactory.ResponseMetadata(attempts: 0), "attempts"),
            (() => Decision1ModelFactory.ResponseMetadata(rateLimitLimitRequests: -1), "rateLimitLimitRequests"),
            (() => Decision1ModelFactory.ResponseMetadata(rateLimitRemainingRequests: -1), "rateLimitRemainingRequests"),
            (() => Decision1ModelFactory.ResponseMetadata(rateLimitResetRequests: TimeSpan.FromSeconds(-1)), "rateLimitResetRequests"),
            (() => Decision1ModelFactory.ResponseMetadata(rateLimitRenewalPeriodRequests: TimeSpan.FromSeconds(-1)), "rateLimitRenewalPeriodRequests"),
        };

        foreach (var (act, parameter) in actions)
            act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(parameter);
    }

    // ---- Response ----------------------------------------------------------------------------------

    private static Decision1Response CombinedResponse(Decision1Usage? usage = null, Decision1ResponseMetadata? metadata = null) =>
        Decision1ModelFactory.Response(Decision1ModelFactory.DefaultModel,
        [
            Decision1ModelFactory.NoulAnswer("refund", 0.0019267495720550704),
            Decision1ModelFactory.NoulAnswer("repeat_contact", 0.9953904259439476),
            Decision1ModelFactory.ScoreAnswer("severity", Severity,
                [0.0013079888902714876, 0.010951656893852506, 0.7677704453043187, 0.2199699089115573],
                score: 2.2064022742371616, confidence: 0.6903605937390916),
            Decision1ModelFactory.ChoiceAnswer("team", "engineering",
                Probabilities(("billing", 0.0024580428604436297), ("engineering", 0.9916454188501259), ("support", 0.005896538289430438)),
                confidence: 0.9874681282751889),
        ], usage, metadata);

    [Fact]
    public void Response_RoundTripsValuesInAnswerOrder()
    {
        var metadata = Decision1ModelFactory.ResponseMetadata(requestId: "req-1");
        var response = CombinedResponse(Decision1ModelFactory.Usage(155, 4), metadata);

        response.Model.Should().Be("microsoft-decision-1");
        response.Answers.Keys.Should().Equal("refund", "repeat_contact", "severity", "team");
        response.Usage.Should().Be(new Decision1Usage(155, 4));
        response.Metadata.Should().BeSameAs(metadata);
    }

    [Fact]
    public void Response_Defaults_AreZeroUsageAndDefaultMetadata()
    {
        var response = Decision1ModelFactory.Response("m", [Decision1ModelFactory.NoulAnswer("q", 0.5)]);

        response.Model.Should().Be("m");
        response.Usage.Should().Be(new Decision1Usage(0, 0));
        response.Metadata.StatusCode.Should().Be(200);
        response.Metadata.Attempts.Should().Be(1);
    }

    [Fact]
    public void Response_GetAnswerAndTryGetAnswer_WorkOnFactoryBuiltResponses()
    {
        var response = CombinedResponse();

        response.GetAnswer<Decision1NoulAnswer>("repeat_contact").Noul.Should().Be(0.9953904259439476);
        response.GetAnswer<Decision1ChoiceAnswer>("team").Choice.Should().Be("engineering");
        response.GetAnswer<Decision1ScoreAnswer>("severity").NearestLevel.Label.Should().Be("Major");

        response.TryGetAnswer<Decision1ScoreAnswer>("severity", out var severity).Should().BeTrue();
        severity!.Score.Should().Be(2.2064022742371616);
        response.TryGetAnswer<Decision1NoulAnswer>("severity", out var wrongType).Should().BeFalse();
        wrongType.Should().BeNull();
        response.TryGetAnswer<Decision1NoulAnswer>("missing", out var missing).Should().BeFalse();
        missing.Should().BeNull();

        response.Invoking(r => r.GetAnswer<Decision1NoulAnswer>("missing")).Should().Throw<KeyNotFoundException>()
            .WithMessage("*missing*refund, repeat_contact, severity, team*");
        response.Invoking(r => r.GetAnswer<Decision1NoulAnswer>("team")).Should().Throw<InvalidCastException>()
            .WithMessage("*choice answer*Decision1NoulAnswer*");
    }

    [Fact]
    public void Response_UnknownAnswer_IsReturnedAsTheBaseType()
    {
        var response = Decision1ModelFactory.Response("m", [Decision1ModelFactory.UnknownAnswer("future", "ranking")]);

        response.GetAnswer<Decision1UnknownAnswer>("future").Type.Should().Be("ranking");
        response.GetAnswer<Decision1Answer>("future").Should().BeOfType<Decision1UnknownAnswer>();
    }

    [Fact]
    public void Response_DuplicateAnswerNames_Throw()
    {
        var act = () => Decision1ModelFactory.Response("m",
            [Decision1ModelFactory.NoulAnswer("q", 0.1), Decision1ModelFactory.NoulAnswer("q", 0.2)]);

        act.Should().Throw<ArgumentException>().WithMessage("*'q' appears more than once*").Which.ParamName.Should().Be("answers");
    }

    [Fact]
    public void Response_NullArguments_Throw()
    {
        ((Action)(() => Decision1ModelFactory.Response(null!, []))).Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("model");
        ((Action)(() => Decision1ModelFactory.Response("m", null!))).Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("answers");
        ((Action)(() => Decision1ModelFactory.Response("m", [null!]))).Should().Throw<ArgumentException>().Which.ParamName.Should().Be("answers");
    }

    // ---- Factory-built answers equal parsed ones ---------------------------------------------------

    [Fact]
    public async Task FactoryBuiltResponse_SerializesTheSameWayAsTheParsedProbeResponse()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.CombinedEntraServices));
        var parsed = await test.Client.DecideAsync("x", Questions.Combined());

        var built = CombinedResponse(Decision1ModelFactory.Usage(155, 4));

        built.Model.Should().Be(parsed.Model);
        built.Usage.Should().Be(parsed.Usage);
        built.Answers.Keys.Should().Equal(parsed.Answers.Keys);
        foreach (var (name, answer) in parsed.Answers)
        {
            built.Answers[name].GetType().Should().Be(answer.GetType());
            built.Answers[name].RawJson.GetRawText().Should().Be(answer.RawJson.GetRawText(), "answer '{0}' must serialize like the service", name);
        }

        built.GetAnswer<Decision1ScoreAnswer>("severity").Levels.Should().Equal(parsed.GetAnswer<Decision1ScoreAnswer>("severity").Levels);
        built.GetAnswer<Decision1ChoiceAnswer>("team").Probabilities.Should().Equal(parsed.GetAnswer<Decision1ChoiceAnswer>("team").Probabilities);
    }

    [Fact]
    public async Task FactoryBuiltAnswers_ParseBackToEqualAnswersThroughTheClient()
    {
        Decision1Answer[] answers =
        [
            Decision1ModelFactory.NoulAnswer("n", 0.123456789),
            Decision1ModelFactory.ChoiceAnswer("c", "z", Probabilities(("z", 0.6), ("a", 0.3), ("m", 0.1))),
            Decision1ModelFactory.ScoreAnswer("s", ["low", "mid", "high"], [0.2, 0.3, 0.5]),
            Decision1ModelFactory.UnknownAnswer("u", "ranking"),
        ];
        var body = new StringBuilder("""{"model":"microsoft-decision-1","answers":{""");
        body.AppendJoin(",", answers.Select(a => $"{JsonSerializer.Serialize(a.Name)}:{a.RawJson.GetRawText()}"));
        body.Append("""},"usage":{"input_tokens":1,"output_tokens":1}}""");
        using var test = TestClient.Create(new FakeDecision1Handler(new ProbeFixture(200, body.ToString(), new Dictionary<string, string>())));

        var parsed = await test.Client.DecideAsync("x", Questions.SingleNoul());

        foreach (var answer in answers)
        {
            parsed.Answers[answer.Name].GetType().Should().Be(answer.GetType());
            parsed.Answers[answer.Name].RawJson.GetRawText().Should().Be(answer.RawJson.GetRawText());
            parsed.Answers[answer.Name].Should().BeEquivalentTo(answer, o => o.RespectingRuntimeTypes().Excluding(a => a.RawJson));
        }
    }

    // ---- A fake IDecision1Client -------------------------------------------------------------------

    /// <summary>The kind of hand-written fake a consumer writes: record the call, return a canned response.</summary>
    private sealed class StubDecision1Client(Decision1Response response) : IDecision1Client
    {
        public List<IReadOnlyList<KeyValuePair<string, Decision1Question>>> Calls { get; } = [];

        public Task<Decision1Response> DecideAsync(
            Decision1State state,
            IEnumerable<KeyValuePair<string, Decision1Question>> questions,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(questions.ToArray());
            return Task.FromResult(response);
        }
    }

    /// <summary>Code under test: routes a ticket using an injected client.</summary>
    private sealed class TicketRouter(IDecision1Client decisions)
    {
        public async Task<string> RouteAsync(string ticket)
        {
            var team = await decisions.DecideAsync(ticket,
                Decision1Question.Choice("Which team?", new Dictionary<string, string> { ["billing"] = "Money", ["engineering"] = "Bugs" }),
                name: "team");
            return team.Confidence >= 0.8 ? team.Choice : "triage";
        }
    }

    [Fact]
    public async Task FakeClient_ReturnsAFactoryBuiltResponse()
    {
        var confident = new StubDecision1Client(Decision1ModelFactory.Response(Decision1ModelFactory.DefaultModel,
            [Decision1ModelFactory.ChoiceAnswer("team", "engineering", Probabilities(("billing", 0.05), ("engineering", 0.95)))]));
        var unsure = new StubDecision1Client(Decision1ModelFactory.Response(Decision1ModelFactory.DefaultModel,
            [Decision1ModelFactory.ChoiceAnswer("team", "billing", Probabilities(("billing", 0.55), ("engineering", 0.45)))]));

        (await new TicketRouter(confident).RouteAsync("The API returns 500.")).Should().Be("engineering");
        (await new TicketRouter(unsure).RouteAsync("Something is off.")).Should().Be("triage");
        confident.Calls.Should().ContainSingle().Which.Should().ContainSingle().Which.Key.Should().Be("team");
    }
}
