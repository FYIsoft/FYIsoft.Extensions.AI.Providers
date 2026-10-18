using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Decision1.Tests;

public class Decision1ResponseParsingTests
{
    private static async Task<Decision1Response> CombinedAsync()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.CombinedEntraServices));
        return await test.Client.DecideAsync("x", Questions.Combined());
    }

    [Fact]
    public async Task Combined_ParsesModelUsageAndAllAnswersInServiceOrder()
    {
        var response = await CombinedAsync();

        response.Model.Should().Be("microsoft-decision-1");
        response.Usage.Should().Be(new Decision1Usage(155, 4));
        response.Usage.TotalTokens.Should().Be(159);
        response.Answers.Keys.Should().Equal("refund", "repeat_contact", "severity", "team");
    }

    [Fact]
    public async Task Noul_ParsesProbability()
    {
        var response = await CombinedAsync();

        var repeat = response.GetAnswer<Decision1NoulAnswer>("repeat_contact");
        repeat.Name.Should().Be("repeat_contact");
        repeat.Type.Should().Be("noul");
        repeat.Noul.Should().Be(0.9953904259439476);
        repeat.IsTrue().Should().BeTrue();
        response.GetAnswer<Decision1NoulAnswer>("refund").IsTrue().Should().BeFalse();
        repeat.IsTrue(threshold: 0.999).Should().BeFalse();
    }

    [Fact]
    public async Task Choice_ParsesSelectionConfidenceAndOrderedProbabilities()
    {
        var team = (await CombinedAsync()).GetAnswer<Decision1ChoiceAnswer>("team");

        team.Choice.Should().Be("engineering");
        team.Confidence.Should().Be(0.9874681282751889);
        team.Probabilities.Keys.Should().Equal("billing", "engineering", "support");
        team.GetProbability("support").Should().Be(0.005896538289430438);
        team.Invoking(t => t.GetProbability("legal")).Should().Throw<KeyNotFoundException>().WithMessage("*legal*billing, engineering, support*");
    }

    [Fact]
    public async Task Score_KeepsLegendAndProbabilitiesByIndexAndByLabel()
    {
        var severity = (await CombinedAsync()).GetAnswer<Decision1ScoreAnswer>("severity");

        severity.Score.Should().Be(2.2064022742371616);
        severity.Confidence.Should().Be(0.6903605937390916);
        severity.Legend.Should().Equal("Cosmetic", "Minor", "Major", "Critical");
        severity.Probabilities.Should().HaveCount(4);
        severity.Probabilities[2].Should().Be(0.7677704453043187);
        severity.ProbabilitiesByLabel.Keys.Should().Equal("Cosmetic", "Minor", "Major", "Critical");
        severity.ProbabilitiesByLabel["Critical"].Should().Be(0.2199699089115573);
        severity.GetProbability("Minor").Should().Be(0.010951656893852506);
        severity.Levels[3].Should().Be(new Decision1ScoreLevel(3, "Critical", 0.2199699089115573));
        severity.MostProbableLevel.Label.Should().Be("Major");
        severity.NearestLevel.Label.Should().Be("Major");
    }

    [Fact]
    public async Task Score_TenLevels_ParsesEveryLevel()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.Score10Levels));

        var materiality = await test.Client.DecideAsync("x",
            Decision1Question.Score("How material?", Enumerable.Range(1, 10).Select(i => i.ToString())), "materiality");

        materiality.Levels.Should().HaveCount(10);
        materiality.Legend[9].Should().Be("10");
        materiality.Levels.Select(l => l.Index).Should().Equal(Enumerable.Range(0, 10));
        materiality.Probabilities.Sum().Should().BeApproximately(1.0, 1e-9);
        materiality.Score.Should().Be(1.172630589488324);
        materiality.NearestLevel.Label.Should().Be("2");
        materiality.MostProbableLevel.Label.Should().Be("1");
    }

    [Fact]
    public async Task Metadata_SurfacesRequestIdRegionAndRateLimits()
    {
        var metadata = (await CombinedAsync()).Metadata;

        metadata.StatusCode.Should().Be(200);
        metadata.RequestId.Should().Be("5dbe01c2-a4cd-4a4d-b4e5-32dff2563068");
        metadata.Region.Should().Be("East US 2");
        metadata.RateLimitLimitRequests.Should().Be(30);
        metadata.RateLimitRemainingRequests.Should().Be(29);
        metadata.RateLimitResetRequests.Should().Be(TimeSpan.FromSeconds(2));
        metadata.RateLimitRenewalPeriodRequests.Should().Be(TimeSpan.FromSeconds(60));
        metadata.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task GetAnswer_MissingName_ThrowsKeyNotFoundListingAnswers()
    {
        var response = await CombinedAsync();

        response.Invoking(r => r.GetAnswer<Decision1NoulAnswer>("nope")).Should().Throw<KeyNotFoundException>()
            .WithMessage("*'nope'*refund, repeat_contact, severity, team*");
        response.TryGetAnswer<Decision1NoulAnswer>("nope", out var missing).Should().BeFalse();
        missing.Should().BeNull();
    }

    [Fact]
    public async Task GetAnswer_WrongType_ThrowsInvalidCastNamingBothTypes()
    {
        var response = await CombinedAsync();

        response.Invoking(r => r.GetAnswer<Decision1ScoreAnswer>("team")).Should().Throw<InvalidCastException>()
            .WithMessage("*'team'*choice*Decision1ScoreAnswer*");
        response.TryGetAnswer<Decision1ScoreAnswer>("team", out _).Should().BeFalse();
        response.TryGetAnswer<Decision1ChoiceAnswer>("team", out var team).Should().BeTrue();
        team!.Choice.Should().Be("engineering");
    }

    [Fact]
    public async Task SingleQuestionOverloads_ReturnTypedAnswers()
    {
        using var noul = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));
        (await noul.Client.DecideAsync("x", Decision1Question.Noul("Billing?"), "billing")).Noul.Should().Be(0.9980732633607365);

        using var choice = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.CombinedEntraServices));
        (await choice.Client.DecideAsync("x", Decision1Question.Choice("Team?", new Dictionary<string, string> { ["billing"] = "b", ["engineering"] = "e" }), "team"))
            .Choice.Should().Be("engineering");

        using var score = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.CombinedEntraServices));
        (await score.Client.DecideAsync("x", Decision1Question.Score("Severity?", "Cosmetic", "Minor", "Major", "Critical"), "severity"))
            .MostProbableLevel.Label.Should().Be("Major");
    }

    [Fact]
    public async Task SingleQuestionOverload_DefaultsTheQuestionName()
    {
        var body = """{"model":"microsoft-decision-1","answers":{"answer":{"type":"noul","noul":0.25}},"usage":{"input_tokens":5,"output_tokens":1}}""";
        using var test = TestClient.Create(new FakeDecision1Handler(new ProbeFixture(200, body, new Dictionary<string, string>())));

        var answer = await test.Client.DecideAsync("x", Decision1Question.Noul("Is it?"));

        answer.Noul.Should().Be(0.25);
        test.Handler.Last.Json.GetProperty("questions").EnumerateObject().Single().Name.Should().Be("answer");
    }

    [Fact]
    public async Task UnknownAnswerType_IsKeptWithRawJson()
    {
        var body = """{"model":"microsoft-decision-1","answers":{"r":{"type":"rank","order":["a","b"]}},"usage":{"input_tokens":1,"output_tokens":1}}""";
        using var test = TestClient.Create(new FakeDecision1Handler(new ProbeFixture(200, body, new Dictionary<string, string>())));

        var response = await test.Client.DecideAsync("x", Questions.SingleNoul());

        var unknown = response.Answers["r"].Should().BeOfType<Decision1UnknownAnswer>().Subject;
        unknown.Type.Should().Be("rank");
        unknown.RawJson.GetProperty("order").GetArrayLength().Should().Be(2);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"model":"m"}""")]
    [InlineData("""{"model":"m","answers":{"s":{"type":"score","score":1,"confidence":1,"legend":{"0":"a","2":"b"},"probabilities":{"0":0.5,"2":0.5}}}}""")]
    [InlineData("""{"model":"m","answers":{"n":{"type":"noul"}}}""")]
    public async Task MalformedSuccessBody_ThrowsDecision1ExceptionWithRequestId(string body)
    {
        using var test = TestClient.Create(new FakeDecision1Handler(new ProbeFixture(200, body,
            new Dictionary<string, string> { ["apim-request-id"] = "req-123" })));

        var act = () => test.Client.DecideAsync("x", Questions.SingleNoul());

        var thrown = await act.Should().ThrowAsync<Decision1Exception>();
        thrown.Which.Should().NotBeAssignableTo<Decision1ApiException>();
        thrown.Which.Message.Should().Contain("req-123");
    }

    [Fact]
    public async Task RawJson_IsUsableAfterTheResponseIsParsed()
    {
        var team = (await CombinedAsync()).Answers["team"];

        team.RawJson.GetProperty("probabilities").GetProperty("billing").GetDouble().Should().Be(0.0024580428604436297);
        team.RawJson.ValueKind.Should().Be(JsonValueKind.Object);
    }
}
