using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class LineLocalTransformCommandJson
{
    internal static SetLineLocalTransformCommandDto Read(JsonElement root)
    {
        var fields = Object(root, ["type", "branchId", "lineId", "transform"]);
        string branch = Text(fields, "branchId");
        string line = Text(fields, "lineId");
        if (!fields.TryGetValue("transform", out var value)) throw new JsonException("transform 必须显式提供对象或 null。");
        ProjectLineLocalTransformDto? transform = null;
        if (value.ValueKind != JsonValueKind.Null)
        {
            var parts = Object(value, ["offsetXRatio", "offsetYRatio", "rotationDegrees", "scale"]);
            double x = Number(parts, "offsetXRatio"), y = Number(parts, "offsetYRatio");
            double rotation = Number(parts, "rotationDegrees"), scale = Number(parts, "scale");
            if (scale <= 0) throw new JsonException("scale 必须为有限正数。");
            transform = new(x, y, rotation, scale);
        }
        return new(branch, line, transform);
    }

    internal static void Write(Utf8JsonWriter writer, SetLineLocalTransformCommandDto command)
    {
        writer.WriteStartObject();
        writer.WriteString("type", command.Type);
        writer.WriteString("branchId", command.BranchId);
        writer.WriteString("lineId", command.LineId);
        writer.WritePropertyName("transform");
        if (command.Transform is not { } value) writer.WriteNullValue();
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("offsetXRatio", value.OffsetXRatio);
            writer.WriteNumber("offsetYRatio", value.OffsetYRatio);
            writer.WriteNumber("rotationDegrees", value.RotationDegrees);
            writer.WriteNumber("scale", value.Scale);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static Dictionary<string, JsonElement> Object(JsonElement root, string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("微调数据必须是对象。");
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                throw new JsonException("微调数据包含未知或重复字段。");
        return fields;
    }

    private static string Text(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new JsonException(name + " 必须为非空字符串。");

    private static double Number(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number) && double.IsFinite(number)
                ? number : throw new JsonException(name + " 必须为有限数值。");
}
