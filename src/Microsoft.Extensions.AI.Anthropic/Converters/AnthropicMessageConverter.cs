using System.Text;
using Anthropic.Models.Messages;

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>
/// Converts between Microsoft.Extensions.AI message types and Anthropic SDK message types.
/// </summary>
/// <remarks>
/// <para>
/// <strong>System Message Handling</strong>:
/// Anthropic's API requires system messages to be sent via a separate <c>system</c> parameter,
/// not as part of the message array. This converter automatically:
/// <list type="number">
/// <item>Extracts all <see cref="ChatRole.System"/> messages from the message list</item>
/// <item>Combines their text content (preserving order)</item>
/// <item>Returns the combined system prompt separately</item>
/// <item>Filters system messages out of the main message array</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>Role Mapping</strong>:
/// <list type="bullet">
/// <item><see cref="ChatRole.User"/> → <see cref="Role.User"/></item>
/// <item><see cref="ChatRole.Assistant"/> → <see cref="Role.Assistant"/></item>
/// <item><see cref="ChatRole.System"/> → Extracted to system parameter</item>
/// <item><see cref="ChatRole.Tool"/> → Tool result content blocks in user message</item>
/// </list>
/// </para>
/// </remarks>
internal static class AnthropicMessageConverter
{
    /// <summary>
    /// Converts Microsoft.Extensions.AI messages to Anthropic message format.
    /// </summary>
    /// <param name="messages">The chat messages to convert.</param>
    /// <returns>
    /// A tuple containing:
    /// <list type="bullet">
    /// <item><c>messages</c>: List of Anthropic messages (system messages excluded)</item>
    /// <item><c>systemPrompt</c>: Combined system prompt from all system messages, or null if none</item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="messages"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the message list is empty, contains only system messages,
    /// or violates Anthropic's alternating user/assistant pattern.
    /// </exception>
    public static (List<MessageParam> messages, string? systemPrompt) ToAnthropicMessages(
        IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageList = messages.ToList();
        if (messageList.Count == 0)
        {
            throw new ArgumentException("Message list cannot be empty.", nameof(messages));
        }

        // Extract and combine system messages
        var systemMessages = messageList
            .Where(m => m.Role == ChatRole.System)
            .ToList();

        string? systemPrompt = null;
        if (systemMessages.Count > 0)
        {
            var systemBuilder = new StringBuilder();
            foreach (var systemMsg in systemMessages)
            {
                var textContent = systemMsg.Text;
                if (!string.IsNullOrWhiteSpace(textContent))
                {
                    if (systemBuilder.Length > 0)
                    {
                        systemBuilder.Append('\n'); // Separate multiple system messages with Unix newline
                    }
                    systemBuilder.Append(textContent);
                }
            }

            systemPrompt = systemBuilder.Length > 0 ? systemBuilder.ToString() : null;
        }

        // Convert non-system messages
        var anthropicMessages = new List<MessageParam>();
        var nonSystemMessages = messageList.Where(m => m.Role != ChatRole.System).ToList();

        if (nonSystemMessages.Count == 0)
        {
            throw new ArgumentException(
                "Message list must contain at least one non-system message.", nameof(messages));
        }

        Role? lastRole = null;

        foreach (var message in nonSystemMessages)
        {
            Role role;
            if (message.Role == ChatRole.User)
            {
                role = Role.User;
            }
            else if (message.Role == ChatRole.Assistant)
            {
                role = Role.Assistant;
            }
            else if (message.Role == ChatRole.Tool)
            {
                role = Role.User; // Tool results are sent as user messages in Anthropic
            }
            else
            {
                throw new ArgumentException(
                    $"Unsupported message role: {message.Role}. Supported roles are User, Assistant, System, and Tool.",
                    nameof(messages));
            }

            // Convert content
            var contentBlocks = AnthropicContentConverter.ToAnthropicContent(message.Contents);

            // Anthropic requires the roles to alternate, so neighbouring messages that share a role are
            // merged into one message carrying both sets of content blocks. Two callers rely on this:
            // Microsoft.Extensions.AI's structured output appends a second user message holding the schema,
            // and parallel tool calls produce one tool message per result, which Anthropic expects to
            // arrive as several tool_result blocks inside a single user message.
            if (anthropicMessages.Count > 0 &&
                lastRole == role &&
                anthropicMessages[^1].Content.TryPickContentBlockParams(out var previousBlocks))
            {
                anthropicMessages[^1] = new MessageParam
                {
                    Role = role,
                    Content = new List<ContentBlockParam>([.. previousBlocks, .. contentBlocks]),
                };
                continue;
            }

            var anthropicMessage = new MessageParam
            {
                Role = role,
                Content = contentBlocks
            };

            anthropicMessages.Add(anthropicMessage);
            lastRole = role;
        }

        // Validate alternating user/assistant pattern
        ValidateMessagePattern(anthropicMessages);

        return (anthropicMessages, systemPrompt);
    }

    /// <summary>
    /// Converts an Anthropic message response to a Microsoft.Extensions.AI ChatResponse.
    /// </summary>
    /// <param name="message">The Anthropic message response.</param>
    /// <param name="metadata">The chat client metadata.</param>
    /// <returns>A <see cref="ChatResponse"/> containing the converted message and metadata.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is null.</exception>
    public static ChatResponse FromAnthropicMessage(Message message, ChatClientMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Convert role
        // Message.Role is always "assistant" for response messages from Anthropic
        var roleStr = message.Role.GetString();
        var role = roleStr switch
        {
            "assistant" => ChatRole.Assistant,
            "user" => ChatRole.User,
            _ => ChatRole.Assistant // Default to assistant if unknown
        };

        // Convert content blocks
        var contents = new List<AIContent>();

        if (message.Content is not null)
        {
            foreach (var block in message.Content)
            {
                var convertedContent = AnthropicContentConverter.FromAnthropicContent(block);
                if (convertedContent is not null)
                {
                    contents.Add(convertedContent);
                }
            }
        }

        // Add usage information if available
        if (message.Usage is not null)
        {
            var usageDetails = new UsageDetails
            {
                InputTokenCount = message.Usage.InputTokens,
                OutputTokenCount = message.Usage.OutputTokens,
                TotalTokenCount = message.Usage.InputTokens + message.Usage.OutputTokens
            };
            contents.Add(new UsageContent(usageDetails));
        }

        // Create the response message
        var responseMessage = new ChatMessage(role, contents);

        // Set metadata properties
        if (!string.IsNullOrEmpty(message.ID))
        {
            responseMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            responseMessage.AdditionalProperties["anthropic_message_id"] = message.ID;
        }

        if (!string.IsNullOrEmpty(message.Model))
        {
            responseMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            responseMessage.AdditionalProperties["model"] = message.Model;
        }

        // Map stop reason
        ChatFinishReason? finishReason = message.StopReason?.Raw() switch
        {
            "end_turn" => ChatFinishReason.Stop,
            "max_tokens" => ChatFinishReason.Length,
            "stop_sequence" => ChatFinishReason.Stop,
            "tool_use" => ChatFinishReason.ToolCalls,
            _ => null
        };

        // Create and return the response
        return new ChatResponse(responseMessage)
        {
            ModelId = message.Model,
            FinishReason = finishReason,
            Usage = message.Usage is not null
                ? new UsageDetails
                {
                    InputTokenCount = message.Usage.InputTokens,
                    OutputTokenCount = message.Usage.OutputTokens,
                    TotalTokenCount = message.Usage.InputTokens + message.Usage.OutputTokens
                }
                : null,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["anthropic_message_id"] = message.ID,
                ["anthropic_stop_reason"] = message.StopReason?.Raw()
            }
        };
    }

    /// <summary>
    /// Validates that messages follow Anthropic's required alternating user/assistant pattern.
    /// The first message must be from the user.
    /// </summary>
    private static void ValidateMessagePattern(List<MessageParam> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        // First message must be from user
        // Note: Role is ApiEnum<string, Role>, access .Json property for string value
        var firstRoleJson = (messages[0].Role as dynamic)?.Json?.ToString();
        if (firstRoleJson != "user")
        {
            throw new ArgumentException(
                "The first message must be from the user. Anthropic's API requires the conversation to start with a user message.");
        }

        // Validate alternating pattern
        for (int i = 1; i < messages.Count; i++)
        {
            var prevRoleJson = (messages[i - 1].Role as dynamic)?.Json?.ToString();
            var currentRoleJson = (messages[i].Role as dynamic)?.Json?.ToString();

            // Messages should alternate between user and assistant
            if (prevRoleJson == currentRoleJson)
            {
                throw new ArgumentException(
                    $"Messages must alternate between user and assistant. Found consecutive {currentRoleJson} messages at position {i}.");
            }
        }
    }
}
