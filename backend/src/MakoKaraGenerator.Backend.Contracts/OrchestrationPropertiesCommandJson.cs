using System.Globalization;
using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class OrchestrationPropertiesCommandJson
{
    internal static bool IsType(string? type) => type is "orchestration.setNodeGeometry"
        or "orchestration.setLayoutSettings" or "orchestration.setSlotAnimation" or "orchestration.setProgramAnimationPolicy";

    internal static ProjectCommandDto Read(JsonElement root, string type)
    {
        switch (type)
        {
            case "orchestration.setNodeGeometry":
                var geometry = Object(root, ["type", "nodeId", "placement", "referenceRect"]);
                var placement = Object(Required(geometry, "placement"), ["x", "y", "anchor", "rotationDegrees", "scale"]);
                var rect = Object(Required(geometry, "referenceRect"), ["widthRatio", "heightRatio"]);
                return new SetOrchestrationNodeGeometryCommandDto(Text(geometry, "nodeId"),
                    new(Number(placement, "x"), Number(placement, "y"), Named(placement, "anchor", ["Center", "TopLeft", "TopCenter", "BottomRight", "LeftCenter", "RightCenter"]),
                        Number(placement, "rotationDegrees"), Positive(placement, "scale")),
                    new(Positive(rect, "widthRatio"), Positive(rect, "heightRatio")));
            case "orchestration.setLayoutSettings":
                var settings = Object(root, ["type", "containerId", "safeArea", "maxWidthRatio"]);
                var area = Object(Required(settings, "safeArea"), ["left", "top", "right", "bottom"]);
                return new SetOrchestrationLayoutSettingsCommandDto(Text(settings, "containerId"),
                    new(Number(area, "left"), Number(area, "top"), Number(area, "right"), Number(area, "bottom")), Number(settings, "maxWidthRatio"));
            case "orchestration.setProgramAnimationPolicy":
                var policy = Object(root, ["type", "branchId", "animationPolicy"]);
                return new SetOrchestrationProgramAnimationPolicyCommandDto(Text(policy, "branchId"),
                    Required(policy, "animationPolicy").ValueKind == JsonValueKind.Null ? null : Named(policy, "animationPolicy", ["Karaoke", "Static", "FollowPrimary"]));
            default:
                var animation = Object(root, ["type", "containerId", "slotAnimation"]);
                var value = Required(animation, "slotAnimation");
                return new SetOrchestrationSlotAnimationCommandDto(Text(animation, "containerId"),
                    value.ValueKind == JsonValueKind.Null ? null : Animation(value));
        }
    }

    private static ProjectOrchestrationSlotAnimationDto Animation(JsonElement value)
    {
        var fields = Object(value, ["revealMode", "revealProgress", "revealDelayUs", "revealLeadUs", "maxVisibleLines", "conflictPolicy", "retentionPolicy"]);
        var count = Required(fields, "maxVisibleLines");
        if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out int max) || max < 1) throw new JsonException("maxVisibleLines 必须为正整数。");
        return new(Named(fields, "revealMode", ["Progress", "TimeAfterPreviousStart", "LeadBeforeNextStart"]), Number(fields, "revealProgress"),
            Time(fields, "revealDelayUs"), Time(fields, "revealLeadUs"), max,
            Named(fields, "conflictPolicy", ["QueueUntilSlotFree", "AllowOverflow", "ReplacePrevious", "Skip"]),
            Named(fields, "retentionPolicy", ["UntilLineEnd", "UntilNextReveal", "Immediate"]));
    }

    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command, JsonSerializerOptions options)
    {
        switch (command)
        {
            case SetOrchestrationNodeGeometryCommandDto value:
                JsonSerializer.Serialize(writer, new { type = value.Type, nodeId = value.NodeId, placement = value.Placement, referenceRect = value.ReferenceRect }, options); break;
            case SetOrchestrationLayoutSettingsCommandDto value:
                JsonSerializer.Serialize(writer, new { type = value.Type, containerId = value.ContainerId, safeArea = value.SafeArea, maxWidthRatio = value.MaxWidthRatio }, options); break;
            case SetOrchestrationSlotAnimationCommandDto value:
                writer.WriteStartObject(); writer.WriteString("type", value.Type); writer.WriteString("containerId", value.ContainerId);
                writer.WritePropertyName("slotAnimation"); JsonSerializer.Serialize(writer, value.SlotAnimation, options); writer.WriteEndObject(); break;
            case SetOrchestrationProgramAnimationPolicyCommandDto value:
                writer.WriteStartObject(); writer.WriteString("type", value.Type); writer.WriteString("branchId", value.BranchId);
                writer.WriteString("animationPolicy", value.AnimationPolicy); writer.WriteEndObject(); break;
        }
    }

    private static Dictionary<string, JsonElement> Object(JsonElement root, string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("编排属性必须为对象。");
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                throw new JsonException("编排属性包含未知或重复字段。");
        return fields;
    }
    private static JsonElement Required(Dictionary<string, JsonElement> fields, string name) => fields.TryGetValue(name, out var value)
        ? value : throw new JsonException(name + " 必须显式提供。");
    private static string Text(Dictionary<string, JsonElement> fields, string name) => Required(fields, name) is { ValueKind: JsonValueKind.String } value
        && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new JsonException(name + " 必须为非空字符串。");
    private static string Named(Dictionary<string, JsonElement> fields, string name, string[] names)
    {
        string text = Text(fields, name);
        return names.FirstOrDefault(value => string.Equals(text, value, StringComparison.OrdinalIgnoreCase)) ?? throw new JsonException(name + " 必须为公开枚举名称。");
    }
    private static double Number(Dictionary<string, JsonElement> fields, string name) => Required(fields, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : throw new JsonException(name + " 必须为有限数值。");
    private static double Positive(Dictionary<string, JsonElement> fields, string name) => Number(fields, name) is > 0 and var number
        ? number : throw new JsonException(name + " 必须为正数。");
    private static string Time(Dictionary<string, JsonElement> fields, string name)
    {
        string text = Text(fields, name);
        return text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            ? value.ToString(CultureInfo.InvariantCulture) : throw new JsonException(name + " 必须为非负十进制Int64微秒字符串。");
    }
}
