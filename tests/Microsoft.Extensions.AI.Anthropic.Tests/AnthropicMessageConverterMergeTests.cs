using Anthropic.Models.Messages;
using FluentAssertions;
using Microsoft.Extensions.AI.Anthropic;
using Xunit;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

/// <summary>
/// Anthropic requires alternating roles, so consecutive same-role messages have to be merged rather than
/// rejected. Microsoft.Extensions.AI's structured output and parallel tool results both produce them.
/// </summary>
public class AnthropicMessageConverterMergeTests
{
    [Fact]
    public void ConsecutiveUserMessages_MergeIntoOneMessage()
    {
        var (messages, _) = AnthropicMessageConverter.ToAnthropicMessages(
        [
            new ChatMessage(ChatRole.User, "Route this ticket."),
            new ChatMessage(ChatRole.User, "Respond with JSON matching this schema: {...}"),
        ]);

        messages.Should().HaveCount(1);
        Blocks(messages[0]).Should().HaveCount(2);
    }

    [Fact]
    public void ConsecutiveToolResults_MergeIntoOneUserMessage()
    {
        var (messages, _) = AnthropicMessageConverter.ToAnthropicMessages(
        [
            new ChatMessage(ChatRole.User, "Look both up."),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("1", "get_account", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("1", "first")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("2", "second")]),
        ]);

        messages.Should().HaveCount(3);
        Blocks(messages[2]).Should().HaveCount(2, "Anthropic expects several tool_result blocks in one user message");
    }

    [Fact]
    public void AlternatingMessages_AreLeftAlone()
    {
        var (messages, _) = AnthropicMessageConverter.ToAnthropicMessages(
        [
            new ChatMessage(ChatRole.User, "one"),
            new ChatMessage(ChatRole.Assistant, "two"),
            new ChatMessage(ChatRole.User, "three"),
        ]);

        messages.Should().HaveCount(3);
    }

    private static IReadOnlyList<ContentBlockParam> Blocks(MessageParam message)
    {
        message.Content.TryPickContentBlockParams(out var blocks).Should().BeTrue();
        return blocks!;
    }
}
