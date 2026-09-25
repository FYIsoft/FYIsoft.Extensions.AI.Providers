using System.Text.Json;
using Anthropic.Models.Messages;

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>
/// Converts between Microsoft.Extensions.AI tool/function declarations and Anthropic tool definitions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Tool Definition Mapping</strong>:
/// Anthropic tools consist of:
/// <list type="bullet">
/// <item><c>name</c>: The function name</item>
/// <item><c>description</c>: The function description</item>
/// <item><c>input_schema</c>: JSON Schema describing the function parameters</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>JSON Schema Generation</strong>:
/// Parameter schemas are automatically generated from the function declaration parameters.
/// The schema follows JSON Schema Draft 2020-12 specification.
/// </para>
/// </remarks>
internal static class AnthropicToolConverter
{
    /// <summary>
    /// Converts Microsoft.Extensions.AI function declarations to Anthropic tool definitions.
    /// </summary>
    /// <param name="tools">The tools to convert.</param>
    /// <returns>A list of Anthropic tool unions.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tools"/> is null.</exception>
    public static List<ToolUnion> ToAnthropicTools(IList<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var anthropicTools = new List<ToolUnion>();

        foreach (var tool in tools)
        {
            if (tool is AIFunction aiFunction)
            {
                var toolDef = ConvertFunction(aiFunction);
                anthropicTools.Add(new ToolUnion(toolDef));
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Warning: Unsupported tool type {tool.GetType().Name} will be skipped.");
            }
        }

        return anthropicTools;
    }

    /// <summary>
    /// Converts an AIFunction to an Anthropic Tool.
    /// </summary>
    private static Tool ConvertFunction(AIFunction function)
    {
        // AIFunctionDeclaration.JsonSchema already contains the input schema
        // AITool.Name and AITool.Description provide the metadata
        var inputSchema = ConvertJsonSchemaToInputSchema(function);

        var toolDef = new Tool
        {
            Name = string.IsNullOrEmpty(function.Name)
                ? throw new ArgumentException("Function name is required", nameof(function))
                : function.Name,
            Description = function.Description ?? string.Empty,
            InputSchema = inputSchema
        };

        return toolDef;
    }

    /// <summary>
    /// Converts the AIFunction's JsonSchema to an InputSchema object compatible with Anthropic's API.
    /// </summary>
    private static InputSchema ConvertJsonSchemaToInputSchema(AIFunctionDeclaration function)
    {
        // The JsonSchema property contains the entire schema as a JsonElement
        var schema = function.JsonSchema;

        // InputSchema expects a dictionary of JsonElements for each property
        // Extract the properties from the schema
        var propertiesDict = new Dictionary<string, JsonElement>();

        if (schema.ValueKind == JsonValueKind.Object)
        {
            // Enumerate all properties from the schema JsonElement
            foreach (var prop in schema.EnumerateObject())
            {
                propertiesDict[prop.Name] = prop.Value;
            }
        }
        else
        {
            // If it's not an object, create a default object schema
            propertiesDict["type"] = JsonSerializer.SerializeToElement("object");
            propertiesDict["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>());
        }

        // Create InputSchema from the properties dictionary
        return new InputSchema(propertiesDict);
    }
}
