using System.Text.Json;
using Anthropic.Models.Messages;

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>
/// Converts between Microsoft.Extensions.AI content types and Anthropic SDK content blocks.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Content Type Mapping</strong>:
/// <list type="table">
/// <listheader>
/// <term>M.E.AI Type</term>
/// <description>Anthropic Type</description>
/// </listheader>
/// <item>
/// <term><see cref="TextContent"/></term>
/// <description><see cref="TextBlock"/></description>
/// </item>
/// <item>
/// <term><see cref="DataContent"/> (image)</term>
/// <description>ImageBlockParam with base64</description>
/// </item>
/// <item>
/// <term><see cref="DataContent"/> (PDF)</term>
/// <description>DocumentBlockParam with base64</description>
/// </item>
/// <item>
/// <term><see cref="FunctionCallContent"/></term>
/// <description>ToolUseBlockParam</description>
/// </item>
/// <item>
/// <term><see cref="FunctionResultContent"/></term>
/// <description>ToolResultBlockParam</description>
/// </item>
/// </list>
/// </para>
///
/// <para>
/// <strong>Image Support</strong>:
/// Supported formats: image/jpeg, image/png, image/gif, image/webp.
/// Images are automatically converted to base64 encoding for transmission.
/// </para>
///
/// <para>
/// <strong>PDF Support</strong>:
/// PDFs are accepted as base64 document blocks or HTTPS document URLs, with optional citations.
/// </para>
/// </remarks>
internal static class AnthropicContentConverter
{
    private static readonly HashSet<string> SupportedImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp"
    };

    private const string PdfMediaType = "application/pdf";

    /// <summary>
    /// Converts Microsoft.Extensions.AI content items to Anthropic content blocks.
    /// </summary>
    /// <param name="contents">The content items to convert.</param>
    /// <returns>A list of Anthropic content blocks.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="contents"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when a content type cannot be converted to an Anthropic content block.
    /// </exception>
    public static List<ContentBlockParam> ToAnthropicContent(IEnumerable<AIContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var blocks = new List<ContentBlockParam>();

        foreach (var content in contents)
        {
            switch (content)
            {
                case TextContent textContent:
                    if (!string.IsNullOrEmpty(textContent.Text))
                    {
                        blocks.Add(new TextBlockParam { Text = textContent.Text });
                    }
                    break;

                case DataContent dataContent:
                    var dataBlock = ConvertDataContent(dataContent);
                    if (dataBlock is not null)
                    {
                        blocks.Add(dataBlock);
                    }
                    break;

                case FunctionCallContent functionCall:
                    blocks.Add(ConvertFunctionCall(functionCall));
                    break;

                case FunctionResultContent functionResult:
                    blocks.Add(ConvertFunctionResult(functionResult));
                    break;

                case UriContent uriContent when uriContent.MediaType == PdfMediaType && uriContent.Uri.Scheme == "https":
                    blocks.Add(AnthropicProviderFeatures.Document(content, new System.Text.Json.Nodes.JsonObject
                        { ["type"] = "url", ["url"] = uriContent.Uri.ToString() }));
                    break;
                case UriContent:
                    throw new NotSupportedException("Only HTTPS PDF UriContent is supported. Use DataContent for other inputs.");
                case TextReasoningContent or WebSearchToolCallContent or WebSearchToolResultContent or McpServerToolCallContent or McpServerToolResultContent:
                    throw new ArgumentException("Provider content requires its original replay envelope. Preserve response message metadata when saving history.");

                case UsageContent:
                    // Usage content is metadata, not part of the message content
                    // Skip it - it will be handled separately
                    break;

                default:
                    // Unknown content type - log warning but don't fail
                    // This allows for forward compatibility with new content types
                    System.Diagnostics.Debug.WriteLine(
                        $"Warning: Unsupported content type {content.GetType().Name} will be skipped.");
                    break;
            }
        }

        return blocks;
    }

    /// <summary>
    /// Converts an Anthropic content block to a Microsoft.Extensions.AI content item.
    /// </summary>
    /// <param name="block">The Anthropic content block to convert.</param>
    /// <returns>The converted content item, or null if the block type is not recognized.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="block"/> is null.</exception>
    public static AIContent? FromAnthropicContent(ContentBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        // Raw union constructors retain JSON without selecting a typed variant. Read the wire
        // discriminator so both SDK responses and assembled streaming blocks project identically.
        var json = block.Json;
        if (AnthropicProviderFeatures.Project(json) is { } projected) return projected;

        if (AnthropicContentEnvelope.String(json, "type") == "tool_use")
        {
            return ConvertToolUseToFunctionCall(JsonSerializer.Deserialize<ToolUseBlock>(json)!);
        }

        // Note: Images are not included in response ContentBlocks (they're only in requests)
        // Unknown response blocks are preserved by the replay-aware projection.
        return null;
    }

    /// <summary>
    /// Converts a DataContent item to an Anthropic content block (ImageBlockParam or DocumentBlockParam).
    /// </summary>
    private static ContentBlockParam? ConvertDataContent(DataContent dataContent)
    {
        var mediaTypeString = dataContent.MediaType ?? string.Empty;

        // Handle images
        if (SupportedImageMediaTypes.Contains(mediaTypeString))
        {
            // Convert data to base64
            // DataContent.Data is ReadOnlyMemory<byte>, convert to byte array
            var bytes = dataContent.Data.ToArray();

            var base64Data = Convert.ToBase64String(bytes);

            // Map media type string to MediaType enum
            var mediaType = mediaTypeString.ToLowerInvariant() switch
            {
                "image/jpeg" => MediaType.ImageJpeg,
                "image/png" => MediaType.ImagePng,
                "image/gif" => MediaType.ImageGif,
                "image/webp" => MediaType.ImageWebP,
                _ => MediaType.ImageJpeg // Default to JPEG
            };

            var base64Source = new Base64ImageSource
            {
                Data = base64Data,
                MediaType = mediaType
            };

            return new ImageBlockParam(new ImageBlockParamSource(base64Source));
        }

        if (string.Equals(mediaTypeString, PdfMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return AnthropicProviderFeatures.Document(dataContent, new System.Text.Json.Nodes.JsonObject
            { ["type"] = "base64", ["media_type"] = PdfMediaType, ["data"] = Convert.ToBase64String(dataContent.Data.Span) });
        }
        if (string.Equals(mediaTypeString, "text/plain", StringComparison.OrdinalIgnoreCase))
            return AnthropicProviderFeatures.Document(dataContent, new System.Text.Json.Nodes.JsonObject
            { ["type"] = "text", ["media_type"] = "text/plain", ["data"] = System.Text.Encoding.UTF8.GetString(dataContent.Data.Span) });

        throw new NotSupportedException(
            $"Media type '{mediaTypeString}' is not supported. " +
            $"Supported types: {string.Join(", ", SupportedImageMediaTypes)}");
    }

    /// <summary>
    /// Converts a FunctionCallContent to an Anthropic ToolUseBlockParam.
    /// </summary>
    private static ToolUseBlockParam ConvertFunctionCall(FunctionCallContent functionCall)
    {
        return new ToolUseBlockParam
        {
            ID = functionCall.CallId ?? Guid.NewGuid().ToString(),
            Name = functionCall.Name,
            Input = ParseFunctionArguments(functionCall.Arguments) ?? new Dictionary<string, JsonElement>()
        };
    }

    /// <summary>
    /// Converts a FunctionResultContent to an Anthropic ToolResultBlockParam.
    /// </summary>
    private static ToolResultBlockParam ConvertFunctionResult(FunctionResultContent functionResult)
    {
        // Convert result to content blocks
        var resultText = functionResult.Result?.ToString();
        ToolResultBlockParamContent? content = null;
        if (!string.IsNullOrEmpty(resultText))
        {
            content = resultText; // Implicit conversion from string to ToolResultBlockParamContent
        }

        // Create ToolResultBlockParam
        return new ToolResultBlockParam
        {
            ToolUseID = functionResult.CallId ?? throw new ArgumentException(
                "FunctionResultContent must have a CallId", nameof(functionResult)),
            Content = content,
            IsError = false // Could be enhanced to detect error results
        };
    }

    /// <summary>
    /// Converts an Anthropic ToolUseBlock to a FunctionCallContent.
    /// </summary>
    private static FunctionCallContent ConvertToolUseToFunctionCall(ToolUseBlock toolUse)
    {
        // Convert Dictionary<string, JsonElement> to IDictionary<string, object?>
        var arguments = new Dictionary<string, object?>();
        foreach (var kvp in toolUse.Input)
        {
            arguments[kvp.Key] = JsonSerializer.Deserialize<object>(kvp.Value);
        }

        return new FunctionCallContent(
            callId: toolUse.ID,
            name: toolUse.Name,
            arguments: arguments);
    }

    // Note: ConvertImageBlockToDataContent removed - Images are not part of response ContentBlocks
    // Images only appear in request ContentBlockParams, not in response ContentBlocks

    /// <summary>
    /// Parses function arguments from various formats into a dictionary.
    /// </summary>
    private static Dictionary<string, JsonElement>? ParseFunctionArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments == null)
        {
            return null;
        }

        // Convert IDictionary<string, object?> to Dictionary<string, JsonElement>
        var result = new Dictionary<string, JsonElement>();
        foreach (var kvp in arguments)
        {
            var element = JsonSerializer.SerializeToElement(kvp.Value);
            result[kvp.Key] = element;
        }
        return result;
    }
}
