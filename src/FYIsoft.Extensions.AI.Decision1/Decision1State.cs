using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Extensions.AI.Decision1;

/// <summary>
/// The state Microsoft-Decision-1 judges: text or a JSON object.
/// </summary>
/// <remarks>
/// <para>
/// Strings, <see cref="JsonElement"/> and <see cref="JsonNode"/> convert implicitly, so callers can pass
/// them directly. Use <see cref="FromObject{T}(T)"/> for any other object; it is serialized with the
/// client's <see cref="Decision1ClientOptions.JsonSerializerOptions"/> when the request is sent.
/// </para>
/// <para>
/// The service accepts a string or a JSON object. JSON arrays, numbers, booleans and null are rejected
/// on the client with <see cref="ArgumentException"/>; a JSON string value is sent as text.
/// </para>
/// </remarks>
public readonly struct Decision1State
{
    private enum StateKind { None, Text, Element, Node, Object }

    private readonly StateKind _kind;
    private readonly object? _value;
    private readonly Type? _type;
    private readonly JsonElement _element;

    private Decision1State(StateKind kind, object? value, Type? type, JsonElement element = default)
    {
        _kind = kind;
        _value = value;
        _type = type;
        _element = element;
    }

    /// <summary>True when no state was supplied (the default value, or a null conversion).</summary>
    public bool IsEmpty => _kind == StateKind.None;

    /// <summary>Creates text state.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static Decision1State FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(StateKind.Text, text, null);
    }

    /// <summary>Creates JSON state from an element. The element is cloned.</summary>
    public static Decision1State FromJson(JsonElement json) => new(StateKind.Element, null, null, json.ValueKind == JsonValueKind.Undefined ? default : json.Clone());

    /// <summary>Creates JSON state from a node. The node is written when the request is sent.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static Decision1State FromJson(JsonNode json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new(StateKind.Node, json, null);
    }

    /// <summary>
    /// Creates state from any object. It is serialized with the client's JSON serializer options when
    /// the request is sent and must serialize to a JSON object or string.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static Decision1State FromObject<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(StateKind.Object, value, typeof(T));
    }

    /// <summary>Converts text to state. A null string produces empty state, which the client rejects.</summary>
    public static implicit operator Decision1State(string? text) => text is null ? default : FromText(text);

    /// <summary>Converts a JSON element to state.</summary>
    public static implicit operator Decision1State(JsonElement json) => FromJson(json);

    /// <summary>Converts a JSON node to state. A null node produces empty state, which the client rejects.</summary>
    public static implicit operator Decision1State(JsonNode? json) => json is null ? default : FromJson(json);

    /// <summary>Writes the state value. Throws <see cref="ArgumentException"/> for unsupported shapes.</summary>
    internal void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions serializerOptions)
    {
        switch (_kind)
        {
            case StateKind.Text:
                writer.WriteStringValue((string)_value!);
                return;
            case StateKind.Element:
                Validate(_element.ValueKind);
                _element.WriteTo(writer);
                return;
            case StateKind.Node:
                var node = (JsonNode)_value!;
                Validate(node.GetValueKind());
                node.WriteTo(writer, serializerOptions);
                return;
            case StateKind.Object:
                JsonElement element;
                try
                {
                    element = JsonSerializer.SerializeToElement(_value, _type!, serializerOptions);
                }
                catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
                {
                    throw new ArgumentException($"The state object of type {_type} could not be serialized to JSON.", "state", ex);
                }
                Validate(element.ValueKind);
                element.WriteTo(writer);
                return;
            default:
                throw new ArgumentException("State is required. Supply text, a JSON object, or Decision1State.FromObject(value).", "state");
        }
    }

    private static void Validate(JsonValueKind kind)
    {
        if (kind is not (JsonValueKind.Object or JsonValueKind.String))
            throw new ArgumentException(
                $"State must be text or a JSON object; a JSON {kind} value is not supported.", "state");
    }
}
