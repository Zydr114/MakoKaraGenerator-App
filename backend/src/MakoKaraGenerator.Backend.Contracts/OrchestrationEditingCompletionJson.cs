using System.Globalization;
using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class OrchestrationEditingCompletionJson
{
    internal static bool IsType(string? type) => type is "orchestration.setLayoutKind"
        or "orchestration.setLineEnterAnimation" or "orchestration.setProjectEnterAnimation";

    internal static ProjectCommandDto Read(JsonElement root, string type)
    {
        if (type == "orchestration.setLayoutKind")
        {
            var fields = Object(root, ["type", "containerId", "kind", "slotIds", "newSlotCount"]);
            string id = Id(fields, "containerId"), kind = Text(fields, "kind");
            if (kind is not ("Auto" or "SlotSequence")) throw new JsonException("kind 必须是 Auto 或 SlotSequence。");
            var array = fields["slotIds"];
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 64
                || fields["newSlotCount"].ValueKind != JsonValueKind.Number || !fields["newSlotCount"].TryGetInt32(out int count)
                || count is < 0 or > 64) throw new JsonException("槽位数量无效。");
            string[] ids = [.. array.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString()!.Length <= 128
                ? value.GetString()! : throw new JsonException("槽位身份无效。"))];
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || (kind == "Auto" ? ids.Length != 0 || count != 0 : ids.Length + count is < 1 or > 64))
                throw new JsonException("布局类型与槽位数量不匹配。");
            return new SetOrchestrationLayoutKindCommandDto(id, kind, ids, count);
        }
        var animationFields = Object(root, type == "orchestration.setLineEnterAnimation"
            ? ["type", "branchId", "lineId", "animation"] : ["type", "animation"]);
        ProjectLineEnterAnimationDto? animation = ReadAnimation(animationFields["animation"]);
        return type == "orchestration.setLineEnterAnimation"
            ? new SetLineEnterAnimationCommandDto(Id(animationFields, "branchId"), Id(animationFields, "lineId"), animation)
            : new SetProjectEnterAnimationCommandDto(animation);
    }

    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", command.Type);
        switch (command)
        {
            case SetOrchestrationLayoutKindCommandDto value:
                writer.WriteString("containerId", value.ContainerId); writer.WriteString("kind", value.Kind);
                writer.WritePropertyName("slotIds"); JsonSerializer.Serialize(writer, value.SlotIds, options);
                writer.WritePropertyName("newSlotCount"); JsonSerializer.Serialize(writer, value.NewSlotCount, options); break;
            case SetLineEnterAnimationCommandDto value:
                writer.WriteString("branchId", value.BranchId); writer.WriteString("lineId", value.LineId);
                writer.WritePropertyName("animation"); JsonSerializer.Serialize(writer, value.Animation, options); break;
            case SetProjectEnterAnimationCommandDto value:
                writer.WritePropertyName("animation"); JsonSerializer.Serialize(writer, value.Animation, options); break;
        }
        if (command.Target is not null) { writer.WritePropertyName("target"); JsonSerializer.Serialize(writer, command.Target, options); }
        if (command.StartUs is not null) writer.WriteString("startUs", command.StartUs);
        if (command.EndUs is not null) writer.WriteString("endUs", command.EndUs);
        if (command.AnalysisJobId is not null) writer.WriteString("analysisJobId", command.AnalysisJobId);
        writer.WriteEndObject();
    }

    private static ProjectLineEnterAnimationDto? ReadAnimation(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        var fields = Object(element, ["durationUs", "easing", "startOpacity", "startScale", "startOffsetPx"]);
        string duration = Text(fields, "durationUs"), easing = Text(fields, "easing");
        float opacity = Number(fields, "startOpacity"), scale = Number(fields, "startScale"), offset = Number(fields, "startOffsetPx");
        if (!long.TryParse(duration, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long us)
            || us is < 0 or > 10000000 || easing is not ("Linear" or "EaseIn" or "EaseOut" or "EaseInOut")
            || opacity is < 0 or > 1 || scale is <= 0 or > 4 || Math.Abs(offset) > 10000)
            throw new JsonException("进入动画参数超出领域范围。");
        return new(duration, easing, opacity, scale, offset);
    }

    private static Dictionary<string, JsonElement> Object(JsonElement root, string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("需要完整对象。");
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                throw new JsonException("对象包含未知或重复字段。");
        if (allowed.Any(name => !fields.ContainsKey(name))) throw new JsonException("必须显式提供全部字段。");
        return fields;
    }
    private static string Text(Dictionary<string, JsonElement> fields, string name) => fields[name].ValueKind == JsonValueKind.String
        ? fields[name].GetString()! : throw new JsonException(name + " 必须是字符串。");
    private static string Id(Dictionary<string, JsonElement> fields, string name)
    {
        string value = Text(fields, name);
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 128 ? value : throw new JsonException("稳定身份无效。");
    }
    private static float Number(Dictionary<string, JsonElement> fields, string name) => fields[name].ValueKind == JsonValueKind.Number
        && fields[name].TryGetSingle(out float value) && float.IsFinite(value) ? value : throw new JsonException(name + " 必须是有限数值。");
}
