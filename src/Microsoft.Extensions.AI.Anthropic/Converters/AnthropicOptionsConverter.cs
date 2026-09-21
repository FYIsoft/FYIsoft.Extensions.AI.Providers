using Anthropic.Models.Messages;

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>
/// Converts between Microsoft.Extensions.AI chat options and Anthropic message creation parameters.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Parameter Mapping</strong>:
/// <list type="table">
/// <listheader>
/// <term>ChatOptions Property</term>
/// <description>MessageCreateParams Property</description>
/// </listheader>
/// <item>
/// <term><see cref="ChatOptions.ModelId"/></term>
/// <description><see cref="MessageCreateParams.Model"/></description>
/// </item>
/// <item>
/// <term><see cref="ChatOptions.MaxOutputTokens"/></term>
/// <description><see cref="MessageCreateParams.MaxTokens"/></description>
/// </item>
/// <item>
/// <term><see cref="ChatOptions.Temperature"/></term>
/// <description><see cref="MessageCreateParams.Temperature"/></description>
/// </item>
/// <item>
/// <term><see cref="ChatOptions.TopP"/></term>
/// <description><see cref="MessageCreateParams.TopP"/></description>
/// </item>
/// <item>
/// <term><see cref="ChatOptions.StopSequences"/></term>
/// <description><see cref="MessageCreateParams.StopSequences"/></description>
/// </item>
/// <item>
/// <term><see cref="ChatOptions.Tools"/></term>
/// <description><see cref="MessageCreateParams.Tools"/></description>
/// </item>
/// </list>
/// </para>
///
/// <para>
/// <strong>Tool Mode Mapping</strong>:
/// <list type="bullet">
/// <item><see cref="AutoChatToolMode"/> → tool_choice: "auto"</item>
/// <item><see cref="RequiredChatToolMode"/> → tool_choice: "required"</item>
/// <item>Specific function → tool_choice: {type: "tool", name: "function_name"}</item>
/// </list>
/// </para>
/// </remarks>
internal static class AnthropicOptionsConverter
{
    /// <summary>
    /// Converts ChatOptions and messages to Anthropic MessageCreateParams.
    /// </summary>
    /// <param name="messages">The Anthropic messages (already converted).</param>
    /// <param name="systemPrompt">The extracted system prompt, or null if none.</param>
    /// <param name="options">The chat options to convert.</param>
    /// <param name="defaultModelId">The default model ID to use if not specified in options.</param>
    /// <returns>A configured <see cref="MessageCreateParams"/> instance.</returns>
    public static MessageCreateParams ToMessageCreateParams(
        List<MessageParam> messages,
        string? systemPrompt,
        ChatOptions? options,
        string? defaultModelId)
    {
        var modelId = options?.ModelId ?? defaultModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                "Model ID must be specified either in the constructor or in ChatOptions.ModelId");
        }

        // Collect all parameters first
        var maxTokens = options?.MaxOutputTokens ?? 4096; // Anthropic requires MaxTokens
        double? temperature = null;
        double? topP = null;
        long? topK = null;
        List<string>? stopSequences = null;
        List<ToolUnion>? tools = null;
        ToolChoice? toolChoice = null;
        Metadata? metadata = null;

        // Apply optional parameters
        if (options is not null)
        {
            // Temperature (0.0 to 1.0)
            if (options.Temperature.HasValue)
            {
                var temp = options.Temperature.Value;
                if (temp < 0 || temp > 1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(options.Temperature),
                        temp,
                        "Temperature must be between 0.0 and 1.0");
                }
                temperature = temp;
            }

            // Top-P (0.0 to 1.0)
            if (options.TopP.HasValue)
            {
                var topPValue = options.TopP.Value;
                if (topPValue < 0 || topPValue > 1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(options.TopP),
                        topPValue,
                        "TopP must be between 0.0 and 1.0");
                }
                topP = topPValue;
            }

            // Top-K (Anthropic specific)
            if (options.AdditionalProperties?.TryGetValue("top_k", out var topKValue) == true)
            {
                if (topKValue is int topKInt)
                {
                    if (topKInt < 0)
                    {
                        throw new ArgumentOutOfRangeException(
                            "top_k",
                            topKInt,
                            "TopK must be a positive integer");
                    }
                    topK = topKInt;
                }
            }

            // Stop sequences
            if (options.StopSequences is { Count: > 0 })
            {
                stopSequences = options.StopSequences.ToList();
            }

            // Tools and tool choice
            if (options.Tools is { Count: > 0 })
            {
                tools = AnthropicToolConverter.ToAnthropicTools(options.Tools);

                // Tool choice mode
                if (options.ToolMode is not null)
                {
                    toolChoice = ConvertToolMode(options.ToolMode);
                }
            }

            // Metadata
            if (options.AdditionalProperties?.TryGetValue("user_id", out var userId) == true &&
                userId is string userIdStr)
            {
                metadata = new Metadata
                {
                    UserID = userIdStr
                };
            }
        }

        // Merge ChatOptions.Instructions (the agent's primary system prompt) with any system
        // text extracted from the message array. ChatClientAgent passes the agent instructions
        // via ChatOptions.Instructions rather than as a system ChatMessage, so without this the
        // agent's system prompt is silently dropped and only incidental system messages survive.
        // Instructions take precedence and lead, followed by message-derived system text.
        var effectiveSystem = (options?.Instructions, systemPrompt) switch
        {
            ({ Length: > 0 } instr, { Length: > 0 } sys) => $"{instr}\n\n{sys}",
            ({ Length: > 0 } instr, _) => instr,
            (_, { Length: > 0 } sys) => sys,
            _ => null
        };

        // Create MessageCreateParams with all properties in initializer.
        // Temperature/TopP/TopK are deprecated for Claude Opus 4.6+ (rejected with 400 if non-default),
        // but still accepted by older models (Sonnet 4.5, Haiku 4.5, Opus 4). The SDK marks them obsolete;
        // we forward when the caller supplies them so older models continue to honor sampling overrides.
#pragma warning disable CS0618 // Type or member is obsolete
        var createParams = new MessageCreateParams
        {
            Model = modelId,
            Messages = messages,
            MaxTokens = maxTokens,
            System = !string.IsNullOrEmpty(effectiveSystem) ? (MessageCreateParamsSystem)effectiveSystem! : null,
            Temperature = temperature,
            TopP = topP,
            TopK = topK,
            StopSequences = stopSequences,
            Tools = tools,
            ToolChoice = toolChoice,
            Metadata = metadata
        };
#pragma warning restore CS0618

        return createParams;
    }


    /// <summary>
    /// Converts a ChatToolMode to Anthropic's tool choice format.
    /// </summary>
    private static ToolChoice? ConvertToolMode(ChatToolMode toolMode)
    {
        return toolMode switch
        {
            AutoChatToolMode => new ToolChoice(new ToolChoiceAuto()),
            RequiredChatToolMode => new ToolChoice(new ToolChoiceAny()), // Anthropic uses "any" for required
            _ when toolMode.GetType().Name.Contains("Function", StringComparison.OrdinalIgnoreCase) =>
                ConvertSpecificFunctionMode(toolMode),
            _ => new ToolChoice(new ToolChoiceAuto()) // Default to auto
        };
    }

    /// <summary>
    /// Converts a specific function tool mode to Anthropic format.
    /// </summary>
    private static ToolChoice ConvertSpecificFunctionMode(ChatToolMode toolMode)
    {
        // Try to extract function name from the tool mode
        // This would require accessing the specific function name property
        var functionNameProperty = toolMode.GetType().GetProperty("FunctionName");
        if (functionNameProperty?.GetValue(toolMode) is string functionName)
        {
            return new ToolChoice(new ToolChoiceTool { Name = functionName });
        }

        // Fallback to auto if we can't determine the function name
        return new ToolChoice(new ToolChoiceAuto());
    }
}
