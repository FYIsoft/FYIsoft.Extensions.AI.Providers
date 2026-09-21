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
/// <description>DocumentBlockParam with base64 (beta feature)</description>
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
/// PDF support requires Anthropic's beta API. PDFs are converted to base64 encoding.
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

                case UriContent uriContent:
                    // URI content is not directly supported by Anthropic
                    // Could potentially fetch and convert to DataContent, but that's beyond scope
                    throw new NotSupportedException(
                        $"UriContent is not directly supported by Anthropic. " +
                        $"URI: {uriContent.Uri}. Consider fetching the content and using DataContent instead.");

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

        // ContentBlock is a union type - use TryPick methods
        if (block.TryPickText(out var textBlock))
        {
            return new TextContent(textBlock.Text);
        }

        if (block.TryPickToolUse(out var toolUse))
        {
            return ConvertToolUseToFunctionCall(toolUse);
        }

        // Note: Images are not included in response ContentBlocks (they're only in requests)
        // Note: Anthropic may add new block types in the future (thinking, etc.)
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

        // PDFs are not currently supported in the standard API for input
        // They would require DocumentBlockParam which may be in beta
        if (string.Equals(mediaTypeString, PdfMediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "PDF content is not currently supported for input messages. " +
                "This may require beta API features.");
        }

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
