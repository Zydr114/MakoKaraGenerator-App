using System.Collections.Immutable;
using System.Text.Json;

namespace MakoKaraGenerator.Core.Effects;

// 各语义读取器共享严格字段规则及带文件/字段位置的异常。
internal static class EffectPackageJson
{
    internal static JsonDocument ReadJson(ReadOnlyMemory<byte> value, string path)
    {
        if (value.Length >= 3 && value.Span[0] == 0xef && value.Span[1] == 0xbb && value.Span[2] == 0xbf) value = value[3..];
        try { return JsonDocument.Parse(value); }
        catch (JsonException error) { throw Error("PackageJsonInvalid", path, "包语义文件不是合法 JSON。", error); }
    }
    internal static void Keys(JsonElement value, string path, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Type(path);
        HashSet<string> found = new(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!found.Add(property.Name)) throw Error("PackageJsonDuplicateKey", path + "." + property.Name, "字段重复。");
            if (!required.Contains(property.Name, StringComparer.Ordinal) && !optional.Contains(property.Name, StringComparer.Ordinal))
                throw Error("PackageSemanticUnknownField", path + "." + property.Name, "未知或导出未引用的字段。");
        }
        foreach (string key in required) if (!found.Contains(key)) throw Error("PackageSemanticMissingField", path + "." + key, "缺少必需字段。");
    }
    internal static string RequiredText(JsonElement value, string key, string path)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? String(field, path + "." + key)
            : throw Error("PackageSemanticMissingField", path + "." + key, "缺少必需文本字段。");
    internal static string String(JsonElement value, string path, bool nonempty = true)
    {
        if (value.ValueKind != JsonValueKind.String || nonempty && string.IsNullOrWhiteSpace(value.GetString())) throw Type(path);
        return value.GetString()!;
    }
    internal static double Number(JsonElement value, string path)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : throw Type(path);
    internal static long Whole(JsonElement value, string path)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long whole) ? whole : throw Type(path);
    internal static JsonElement.ArrayEnumerator Array(JsonElement value, string path)
        => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Type(path);
    internal static ImmutableArray<string> Strings(JsonElement value, string path)
    {
        var items = Array(value, path).Select(item => String(item, path)).ToImmutableArray();
        if (items.Distinct(StringComparer.Ordinal).Count() != items.Length) throw Error("PackageReferenceDuplicate", path, "引用身份重复。");
        return items;
    }
    internal static EffectPackageException Type(string path) => Error("PackageParameterTypeMismatch", path, "字段类型、整数表示或数值有限性不符。");
    internal static EffectPackageException Range(string path) => Error("PackageParameterOutOfRange", path, "值不在明确声明的区间／选项中。");
    internal static EffectPackageException Error(string code, string path, string message, Exception? inner = null) => new(code, path, message, inner);
}
