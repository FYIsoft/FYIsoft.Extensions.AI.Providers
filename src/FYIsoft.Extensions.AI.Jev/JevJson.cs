using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Extensions.AI.Jev;

/// <summary>
/// JSON settings for the TypeSafe wire format (snake_case, polymorphic questions and answers).
/// </summary>
public static class JevJson
{
    /// <summary>Serializer options used for every request and response.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            Converters = { new JevAnswerConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
