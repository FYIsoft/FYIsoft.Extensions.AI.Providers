using System.ComponentModel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.Extensions.AI.Jev.Tests;

/// <summary>A fact that only runs when <c>TYPESAFE_API_KEY</c> is set.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(JevClientOptions.ApiKeyEnvironmentVariable)))
        {
            Skip = $"Set {JevClientOptions.ApiKeyEnvironmentVariable} to run live API tests.";
        }
    }
}

[Trait("Category", "Live")]
public class JevLiveTests
{
    public enum Department
    {
        Billing,
        Technical,
        Sales,
    }

    public sealed record TicketTriage(
        [property: Description("Does this convey urgency?")] bool IsUrgent,
        [property: Description("Which team should handle this?")] Department Department);

    [LiveFact]
    public async Task ListModels_ReturnsJevLatest()
    {
        using var client = new JevClient();

        var models = await client.ListModelsAsync();

        models.Should().Contain(m => m.Name == JevClientOptions.DefaultModelId);
    }

    [LiveFact]
    public async Task SystemOne_AnswersAllQuestionTypes()
    {
        using var client = new JevClient();

        var response = await client.SystemOneAsync(
            "Help! I was charged twice for my subscription and my payouts have been failing for 3 days. This is unacceptable.",
            new Dictionary<string, JevQuestion>
            {
                ["is_urgent"] = JevQuestion.Noul("Does this convey urgency?"),
                ["department"] = JevQuestion.Choice("Which team should handle this?", "billing", "technical", "sales"),
                ["frustration"] = JevQuestion.Score("How frustrated is the customer?", "Calm", "Frustrated", "Very angry"),
            });

        response.Model.Should().StartWith("jev-");
        response.Usage.InputTokens.Should().BePositive();
        response.GetAnswer<JevNoulAnswer>("is_urgent").IsYes.Should().BeTrue();
        response.GetAnswer<JevChoiceAnswer>("department").Choice.Should().Be("billing");
        response.GetAnswer<JevScoreAnswer>("frustration").Score.Should().BeGreaterThan(1);
    }

    [LiveFact]
    public async Task ChatClient_StructuredOutput()
    {
        using var client = new JevClient();
        IChatClient chat = client.AsIChatClient();

        var response = await chat.GetResponseAsync<TicketTriage>(
            "Our production integration has returned HTTP 500 errors on every call since 6am. Nothing works.");

        response.Result.Department.Should().Be(Department.Technical);
        response.Result.IsUrgent.Should().BeTrue();
        response.Usage!.InputTokenCount.Should().BePositive();
    }

    [LiveFact]
    public async Task InvalidKey_ThrowsAuthenticationException()
    {
        using var client = new JevClient(new JevClientOptions { ApiKey = "invalid-key", Retry = JevRetryPolicy.None });

        await FluentActions.Awaiting(() => client.ListModelsAsync()).Should().ThrowAsync<JevAuthenticationException>();
    }
}
