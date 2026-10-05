using System.Text.Json;
using System.Text.Json.Serialization;

namespace JevDotNet;

/// <summary>A text, object, or array value accepted by the TypeSafe API.</summary>
[JsonConverter(typeof(JevValueConverter))]
public sealed class JevValue
{
    private readonly JsonElement _value;

    private JevValue(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException("A Jev value must be a JSON string, object, or array.", nameof(value));
        _value = value.Clone();
    }

    public static JevValue Text(string value) => new(JsonSerializer.SerializeToElement(
        value ?? throw new ArgumentNullException(nameof(value))));

    public static JevValue FromObject<T>(T value, JsonSerializerOptions? options = null) where T : notnull
        => FromSerialized(value, JsonValueKind.Object, options);

    public static JevValue FromArray<T>(T value, JsonSerializerOptions? options = null) where T : notnull
        => FromSerialized(value, JsonValueKind.Array, options);

    public static JevValue FromJson(JsonElement value) => new(value);

    public static implicit operator JevValue(string value) => Text(value);

    public JsonElement ToJsonElement() => _value.Clone();

    private static JevValue FromSerialized<T>(T value, JsonValueKind expected, JsonSerializerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = JsonSerializer.SerializeToElement(value, options);
        if (element.ValueKind != expected)
            throw new ArgumentException($"The serialized value must be a JSON {expected.ToString().ToLowerInvariant()}.", nameof(value));
        return new JevValue(element);
    }

    private sealed class JevValueConverter : JsonConverter<JevValue>
    {
        public override JevValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(JsonElement.ParseValue(ref reader));

        public override void Write(Utf8JsonWriter writer, JevValue value, JsonSerializerOptions options)
            => value._value.WriteTo(writer);
    }
}
