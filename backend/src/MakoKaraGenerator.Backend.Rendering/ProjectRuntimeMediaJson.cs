using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>
/// Strict JSON shape for immutable Core media values: one object field per public constructor parameter.
/// </summary>
/// <remarks>
/// Core media values expose get-only properties and a single public constructor; structs would otherwise
/// deserialize through their implicit default constructor. Unknown, duplicate or missing fields are rejected,
/// and every received value is still rebuilt and revalidated by the Core factories in <see cref="ProjectRuntimeMediaGrant.ToInput"/>.
/// </remarks>
internal sealed class CoreMediaValueJsonConverterFactory : JsonConverterFactory
{
    private const string MediaNamespace = "MakoKaraGenerator.Core.Media";

    public override bool CanConvert(Type typeToConvert) => typeToConvert.Namespace == MediaNamespace
        && !typeToConvert.IsEnum && !typeToConvert.IsAbstract && !typeToConvert.IsGenericType
        && typeToConvert.GetConstructors(BindingFlags.Public | BindingFlags.Instance) is [{ } constructor]
        && constructor.GetParameters().Length > 0;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(ValueConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class ValueConverter<T> : JsonConverter<T>
    {
        private readonly ConstructorInfo _constructor;
        private readonly ParameterInfo[] _parameters;
        private readonly PropertyInfo[] _properties;

        public ValueConverter()
        {
            _constructor = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();
            _parameters = _constructor.GetParameters();
            _properties = new PropertyInfo[_parameters.Length];
            for (int index = 0; index < _parameters.Length; index++)
            {
                string name = _parameters[index].Name ?? throw new InvalidOperationException("Media value parameter has no name.");
                PropertyInfo? property = typeof(T).GetProperty(char.ToUpperInvariant(name[0]) + name[1..], BindingFlags.Public | BindingFlags.Instance);
                if (property is null || property.PropertyType != _parameters[index].ParameterType)
                    throw new InvalidOperationException("Media value " + typeof(T).Name + " does not expose constructor field " + name + ".");
                _properties[index] = property;
            }
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Media value must be an object.");
            object?[] values = new object?[_parameters.Length];
            bool[] seen = new bool[_parameters.Length];
            while (true)
            {
                if (!reader.Read()) throw new JsonException("Media value is truncated.");
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Media value field is malformed.");
                string name = reader.GetString()!;
                int index = Array.FindIndex(_parameters, parameter => parameter.Name == name);
                if (index < 0 || seen[index]) throw new JsonException("Media value field is unknown or repeated.");
                if (!reader.Read()) throw new JsonException("Media value is truncated.");
                values[index] = JsonSerializer.Deserialize(ref reader, _parameters[index].ParameterType, options);
                seen[index] = true;
            }
            if (Array.IndexOf(seen, false) >= 0) throw new JsonException("Media value is missing a field.");
            try { return (T)_constructor.Invoke(values); }
            catch (TargetInvocationException error) { throw new JsonException("Media value is invalid.", error.InnerException ?? error); }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            for (int index = 0; index < _parameters.Length; index++)
            {
                writer.WritePropertyName(_parameters[index].Name!);
                JsonSerializer.Serialize(writer, _properties[index].GetValue(value), _parameters[index].ParameterType, options);
            }
            writer.WriteEndObject();
        }
    }
}
