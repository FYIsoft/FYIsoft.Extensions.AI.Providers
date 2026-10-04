using Anthropic.Models.Messages;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

public class AnthropicOptionsConverterToolModeTests
{
    [Fact]
    public void RequireSpecific_SelectsNamedToolWhenMultipleToolsAreAvailable()
    {
        var request = CreateRequest(ChatToolMode.RequireSpecific("get_balance"));

        request.Tools.Should().HaveCount(2);
        request.ToolChoice!.TryPickTool(out var choice).Should().BeTrue();
        choice!.Name.Should().Be("get_balance");
    }

    [Fact]
    public void None_DisablesToolCallingEvenWhenToolsAreAvailable()
    {
        var request = CreateRequest(ChatToolMode.None);

        request.Tools.Should().HaveCount(2);
        request.ToolChoice!.TryPickNone(out _).Should().BeTrue();
    }

    [Fact]
    public void RequireAny_AllowsAnyAvailableTool()
    {
        var request = CreateRequest(ChatToolMode.RequireAny);

        request.ToolChoice!.TryPickAny(out _).Should().BeTrue();
    }

    [Fact]
    public void Auto_AllowsModelToChooseWhetherToCallTools()
    {
        var request = CreateRequest(ChatToolMode.Auto);

        request.ToolChoice!.TryPickAuto(out _).Should().BeTrue();
    }

    [Fact]
    public void UnspecifiedMode_LeavesProviderDefaultIntact()
    {
        var request = CreateRequest(null);

        request.ToolChoice.Should().BeNull();
    }

    private static MessageCreateParams CreateRequest(ChatToolMode? toolMode) =>
        AnthropicOptionsConverter.ToMessageCreateParams(
            [new MessageParam { Role = Role.User, Content = "Look up my balance." }],
            systemPrompt: null,
            options: new ChatOptions
            {
                ToolMode = toolMode,
                Tools =
                [
                    AIFunctionFactory.Create(() => "Account", "get_account"),
                    AIFunctionFactory.Create(() => 100, "get_balance"),
                ],
            },
            defaultModelId: "test-model");
}
