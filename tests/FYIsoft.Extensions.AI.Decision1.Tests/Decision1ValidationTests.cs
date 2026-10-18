using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Decision1.Tests;

public class Decision1ValidationTests
{
    private static Dictionary<string, string> Options(int count) =>
        Enumerable.Range(1, count).ToDictionary(i => $"option-{i}", i => $"Description {i}");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Factories_RejectEmptyInstructions(string? instructions)
    {
        FluentActions.Invoking(() => Decision1Question.Noul(instructions!)).Should().Throw<ArgumentException>().WithParameterName("instructions");
        FluentActions.Invoking(() => Decision1Question.Choice(instructions!, Options(2))).Should().Throw<ArgumentException>().WithParameterName("instructions");
        FluentActions.Invoking(() => Decision1Question.Score(instructions!, "low", "high")).Should().Throw<ArgumentException>().WithParameterName("instructions");
    }

    [Fact]
    public void Noul_CriteriaMustBothBeNonEmpty()
    {
        FluentActions.Invoking(() => Decision1Question.Noul("Q?", "yes", "")).Should().Throw<ArgumentException>().WithParameterName("whenFalse");
        FluentActions.Invoking(() => Decision1Question.Noul("Q?", " ", "no")).Should().Throw<ArgumentException>().WithParameterName("whenTrue");
        Decision1Question.Noul("Q?", "yes", "no").WhenTrue.Should().Be("yes");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    public void Choice_RejectsOptionCountsOutside2To255(int count)
    {
        FluentActions.Invoking(() => Decision1Question.Choice("Q?", Options(count)))
            .Should().Throw<ArgumentException>().WithMessage("*2 to 255 options*");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void Choice_AcceptsTheLimits(int count) =>
        Decision1Question.Choice("Q?", Options(count)).Options.Should().HaveCount(count);

    [Fact]
    public void Choice_RejectsDuplicateEmptyAndNullDescriptions()
    {
        FluentActions.Invoking(() => Decision1Question.Choice("Q?", [new("a", "x"), new("a", "y")]))
            .Should().Throw<ArgumentException>().WithMessage("*'a'*more than once*");
        FluentActions.Invoking(() => Decision1Question.Choice("Q?", [new("", "x"), new("b", "y")]))
            .Should().Throw<ArgumentException>().WithMessage("*labels*");
        FluentActions.Invoking(() => Decision1Question.Choice("Q?", [new("a", null!), new("b", "y")]))
            .Should().Throw<ArgumentException>().WithMessage("*'a'*null description*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void Score_RejectsLevelCountsOutside2To10(int count)
    {
        FluentActions.Invoking(() => Decision1Question.Score("Q?", Enumerable.Range(1, count).Select(i => i.ToString())))
            .Should().Throw<ArgumentException>().WithMessage("*2 to 10 levels*");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void Score_AcceptsTheLimits(int count) =>
        Decision1Question.Score("Q?", Enumerable.Range(1, count).Select(i => i.ToString())).Levels.Should().HaveCount(count);

    [Fact]
    public void Score_RejectsDuplicateAndEmptyLevels()
    {
        FluentActions.Invoking(() => Decision1Question.Score("Q?", "low", "low")).Should().Throw<ArgumentException>().WithMessage("*'low'*more than once*");
        FluentActions.Invoking(() => Decision1Question.Score("Q?", "low", "")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Client_RejectsEmptyQuestions_WithoutANetworkCall()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));

        var act = () => test.Client.DecideAsync("x", new Dictionary<string, Decision1Question>());

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*At least one question*");
        test.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Client_ThrowsArgumentExceptionSynchronously_BeforeAnyAwait()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));

        // An Action, not a Func<Task>: the exception must escape before a Task is returned.
        Action act = () => _ = test.Client.DecideAsync("x", []);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Client_RejectsDuplicateNullAndBlankQuestionNames()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));
        var q = Decision1Question.Noul("Q?");

        await FluentActions.Invoking(() => test.Client.DecideAsync("x", [new("a", q), new("a", q)]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*'a'*more than once*");
        await FluentActions.Invoking(() => test.Client.DecideAsync("x", [new(" ", q)]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*names*");
        await FluentActions.Invoking(() => test.Client.DecideAsync("x", [new("a", null!)]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*'a' is null*");
        test.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Client_RejectsMissingAndNonObjectState()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));
        var questions = Questions.SingleNoul();

        await FluentActions.Invoking(() => test.Client.DecideAsync((string?)null, questions)).Should().ThrowAsync<ArgumentException>().WithMessage("*State is required*");
        await FluentActions.Invoking(() => test.Client.DecideAsync(default(Decision1State), questions)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => test.Client.DecideAsync(JsonDocument.Parse("[1,2]").RootElement, questions))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Array*");
        await FluentActions.Invoking(() => test.Client.DecideAsync(JsonValue.Create(5), questions))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Number*");
        await FluentActions.Invoking(() => test.Client.DecideAsync(Decision1State.FromObject(new[] { 1, 2 }), questions))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Array*");
        test.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Client_AcceptsAJsonStringElementAsText()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));

        await test.Client.DecideAsync(JsonDocument.Parse("\"hello\"").RootElement, Questions.SingleNoul());

        test.Handler.Last.Json.GetProperty("state").GetString().Should().Be("hello");
    }

    [Fact]
    public void Options_RequireEndpointDeploymentAndAuth()
    {
        Resolve(o => o.Endpoint = null).Should().Throw<ArgumentException>().WithMessage("*Endpoint is required*DECISION1_ENDPOINT*");
        Resolve(o => o.Deployment = " ").Should().Throw<ArgumentException>().WithMessage("*Deployment is required*DECISION1_DEPLOYMENT*");
        Resolve(o => o.ApiKey = null).Should().Throw<ArgumentException>().WithMessage("*Credential*ApiKey*DECISION1_API_KEY*");
    }

    [Fact]
    public void Options_RejectBothCredentialAndApiKey()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        Resolve(o => o.Credential = new FakeTokenCredential(time, TimeSpan.FromHours(1)))
            .Should().Throw<ArgumentException>().WithMessage("*exactly one of Credential or ApiKey*");
    }

    [Theory]
    [InlineData("http://res.services.ai.azure.com")]
    [InlineData("https://res.services.ai.azure.com/?api-version=1")]
    public void Options_RejectNonHttpsOrQueryEndpoints(string endpoint) =>
        Resolve(o => o.Endpoint = new Uri(endpoint)).Should().Throw<ArgumentException>();

    [Fact]
    public void Options_RejectInvalidTimeoutAndRetry()
    {
        Resolve(o => o.Timeout = TimeSpan.Zero).Should().Throw<ArgumentException>().WithMessage("*Timeout*");
        Resolve(o => o.Retry.MaxRetries = -1).Should().Throw<ArgumentException>().WithMessage("*MaxRetries*");
        Resolve(o => o.Retry.JitterRatio = 2).Should().Throw<ArgumentException>().WithMessage("*JitterRatio*");
        Resolve(o => o.Timeout = Timeout.InfiniteTimeSpan).Should().NotThrow();
    }

    [Fact]
    public void Options_DefaultTimeoutIsTenSeconds() =>
        new Decision1ClientOptions().Timeout.Should().Be(TimeSpan.FromSeconds(10));

    [Fact]
    public void Constructor_ThrowsForInvalidOptions()
    {
        FluentActions.Invoking(() => new Decision1Client(new Decision1ClientOptions { UseEnvironmentVariables = false }))
            .Should().Throw<ArgumentException>().WithParameterName("options");
    }

    private static Action Resolve(Action<Decision1ClientOptions> mutate) => () => TestClient.Options(mutate).Resolve();
}
