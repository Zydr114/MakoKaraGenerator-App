using System.Text;
using System.Text.Json;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>排序后的不可变参数对象；语义仅由包元数据解析器判断。</summary>
public sealed class EffectParameterOverrides : IEquatable<EffectParameterOverrides>
{
    private EffectParameterOverrides(string json) { Json = json; }
    /// <summary>不可变规范 JSON；不暴露可写节点或原始缓冲区。</summary>
    public string Json { get; }
    /// <summary>未覆盖任何默认参数。</summary>
    public static EffectParameterOverrides Empty { get; } = new("{}");
    /// <summary>固定 JSON 对象；重复字段或非法 JSON 拒绝，保留数值精度。</summary>
    public static EffectParameterOverrides Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new EffectPackageException("EffectParametersInvalid", "parameters", "参数必须为 JSON 对象。");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return new(Encoding.UTF8.GetString(stream.ToArray()));
    }
    internal static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject(); HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!names.Add(property.Name)) throw new EffectPackageException("EffectFieldDuplicate", property.Name, "JSON 字段重复。");
                writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    /// <summary>返回独立编码，供唯一包参数解析器读取。</summary>
    public ReadOnlyMemory<byte> Encode() => Encoding.UTF8.GetBytes(Json);
    /// <summary>内容相等。</summary>
    public bool Equals(EffectParameterOverrides? other) => other is not null && Json == other.Json;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EffectParameterOverrides other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Json);
}
