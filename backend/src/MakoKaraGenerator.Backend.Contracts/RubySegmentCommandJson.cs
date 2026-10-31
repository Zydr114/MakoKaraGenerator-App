using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class RubySegmentCommandJson
{
    internal static bool IsType(string? type) => type is "lyrics.setRubySegmentTime" or "lyrics.clearRubySegmentTime"
        or "lyrics.setRubySegmentText" or "lyrics.addRubySegment" or "lyrics.removeRubySegment"
        or "lyrics.reorderRubySegments" or "lyrics.setRubySegmentTimes";
    internal static ProjectCommandDto Read(JsonElement root, string type)
    {
        string[] names = type switch
        {
            "lyrics.setRubySegmentTime" => ["type", "target", "startUs", "endUs"],
            "lyrics.setRubySegmentText" => ["type", "target", "text"],
            "lyrics.addRubySegment" => ["type", "target", "text", "afterSegmentId"],
            "lyrics.reorderRubySegments" => ["type", "target", "orderedSegmentIds"],
            "lyrics.setRubySegmentTimes" => ["type", "changes"],
            _ => ["type", "target"],
        };
        var fields = Fields(root, names);
        RubySegmentTargetDto Segment(JsonElement target)
        {
            var ids = Fields(target, ["branchId", "lineId", "tokenId", "rubyId", "segmentId"]);
            return new(Id(ids["branchId"]), Id(ids["lineId"]), Id(ids["tokenId"]), Id(ids["rubyId"]), Id(ids["segmentId"]));
        }
        RubyGroupTargetDto Group()
        {
            var ids = Fields(fields["target"], ["branchId", "lineId", "tokenId", "rubyId"]);
            return new(Id(ids["branchId"]), Id(ids["lineId"]), Id(ids["tokenId"]), Id(ids["rubyId"]));
        }
        string Text(string name) => fields[name].ValueKind == JsonValueKind.String ? fields[name].GetString()! : throw new JsonException("正文必须是字符串。");
        RubySegmentTimeChangeDto[] Changes()
        {
            var value = fields["changes"];
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > RubySegmentEditingLimits.MaxChanges) throw new JsonException("时间批次必须包含1至1000项。");
            return [.. value.EnumerateArray().Select(item =>
            {
                var change = Fields(item, ["target", "startUs", "endUs"]);
                return new RubySegmentTimeChangeDto(Segment(change["target"]), Time(change["startUs"]), Time(change["endUs"]));
            })];
        }
        string[] Order()
        {
            var value = fields["orderedSegmentIds"];
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > ProjectReadModelLimits.MaxItems) throw new JsonException("片段顺序必须为有界非空数组。");
            return [.. value.EnumerateArray().Select(Id)];
        }
        return type switch
        {
            "lyrics.setRubySegmentTime" => new SetRubySegmentTimeCommandDto(Segment(fields["target"]), Time(fields["startUs"]), Time(fields["endUs"])),
            "lyrics.clearRubySegmentTime" => new ClearRubySegmentTimeCommandDto(Segment(fields["target"])),
            "lyrics.setRubySegmentText" => new SetRubySegmentTextCommandDto(Segment(fields["target"]), Text("text")),
            "lyrics.addRubySegment" => new AddRubySegmentCommandDto(Group(), Text("text"), fields["afterSegmentId"].ValueKind == JsonValueKind.Null ? null : Id(fields["afterSegmentId"])),
            "lyrics.removeRubySegment" => new RemoveRubySegmentCommandDto(Segment(fields["target"])),
            "lyrics.reorderRubySegments" => new ReorderRubySegmentsCommandDto(Group(), Order()),
            _ => new SetRubySegmentTimesCommandDto(Changes()),
        };
    }
    private static Dictionary<string, JsonElement> Fields(JsonElement root, string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("命令与目标必须为对象。");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in root.EnumerateObject())
            if (!names.Contains(item.Name, StringComparer.Ordinal) || !fields.TryAdd(item.Name, item.Value)) throw new JsonException("注音片段命令含未知或重复字段。");
        if (fields.Count != names.Length) throw new JsonException("注音片段命令必须完整提供必填字段。");
        return fields;
    }
    private static string Id(JsonElement value) => value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 and <= 128 } id && !string.IsNullOrWhiteSpace(id) ? id : throw new JsonException("目标身份必须非空且不超过128字符。");
    private static string Time(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("时间必须是十进制字符串。");
    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", command.Type);
        void Field<T>(string name, T value) { writer.WritePropertyName(name); JsonSerializer.Serialize(writer, value, options); }
        switch (command)
        {
            case SetRubySegmentTimeCommandDto value: Field("target", value.RubyTarget); Field("startUs", value.SegmentStartUs); Field("endUs", value.SegmentEndUs); break;
            case ClearRubySegmentTimeCommandDto value: Field("target", value.RubyTarget); break;
            case SetRubySegmentTextCommandDto value: Field("target", value.RubyTarget); Field("text", value.Text); break;
            case AddRubySegmentCommandDto value: Field("target", value.RubyTarget); Field("text", value.Text); Field("afterSegmentId", value.AfterSegmentId); break;
            case RemoveRubySegmentCommandDto value: Field("target", value.RubyTarget); break;
            case ReorderRubySegmentsCommandDto value: Field("target", value.RubyTarget); Field("orderedSegmentIds", value.OrderedSegmentIds); break;
            case SetRubySegmentTimesCommandDto value: Field("changes", value.Changes); break;
        }
        // Malformed trusted callers must not share a fingerprint with valid wire requests.
        if (command.Target is not null) Field("inheritedTarget", command.Target);
        if (command.StartUs is not null) Field("inheritedStartUs", command.StartUs);
        if (command.EndUs is not null) Field("inheritedEndUs", command.EndUs);
        if (command.AnalysisJobId is not null) Field("analysisJobId", command.AnalysisJobId);
        writer.WriteEndObject();
    }
}
