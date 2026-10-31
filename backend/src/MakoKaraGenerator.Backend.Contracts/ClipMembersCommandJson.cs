using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class ClipMembersCommandJson
{
    internal static bool IsType(string? type) => type is "orchestration.setClipMembers" or "orchestration.splitClip" or "orchestration.mergeClips";
    internal static ProjectCommandDto Read(JsonElement root, string type)
    {
        string[] names = type switch
        {
            "orchestration.setClipMembers" => ["type", "branchId", "clipId", "lineIds"],
            "orchestration.splitClip" => ["type", "branchId", "clipId", "splitBeforeLineId", "newClipId"],
            _ => ["type", "branchId", "clipIds", "targetClipId"],
        };
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!names.Contains(field.Name, StringComparer.Ordinal) || !fields.TryAdd(field.Name, field.Value)) throw new JsonException("片段成员命令含未知或重复字段。");
        if (fields.Count != names.Length) throw new JsonException("片段成员命令必须完整提供字段。");
        string Id(string name) => fields[name].ValueKind == JsonValueKind.String
            && fields[name].GetString() is { Length: > 0 and <= 128 } id && !string.IsNullOrWhiteSpace(id)
            ? id : throw new JsonException("片段成员身份必须是非空且不超过128字符的字符串。");
        string[] Ids(string name, int minimum)
        {
            var value = fields[name];
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum || value.GetArrayLength() > 1000) throw new JsonException("成员数组数量非法。");
            string[] ids = [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                && item.GetString() is { Length: > 0 and <= 128 } id && !string.IsNullOrWhiteSpace(id) ? id : throw new JsonException("成员身份非法。"))];
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new JsonException("成员身份不能重复。");
            return ids;
        }
        return type switch
        {
            "orchestration.setClipMembers" => new SetOrchestrationClipMembersCommandDto(Id("branchId"), Id("clipId"), Ids("lineIds", 1)),
            "orchestration.splitClip" => new SplitOrchestrationClipCommandDto(Id("branchId"), Id("clipId"), Id("splitBeforeLineId"), Id("newClipId")),
            _ => new MergeOrchestrationClipsCommandDto(Id("branchId"), Ids("clipIds", 2), Id("targetClipId")),
        };
    }
    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", command.Type);
        switch (command)
        {
            case SetOrchestrationClipMembersCommandDto value:
                writer.WriteString("branchId", value.BranchId); writer.WriteString("clipId", value.ClipId);
                writer.WritePropertyName("lineIds"); JsonSerializer.Serialize(writer, value.LineIds, options); break;
            case SplitOrchestrationClipCommandDto value:
                writer.WriteString("branchId", value.BranchId); writer.WriteString("clipId", value.ClipId);
                writer.WriteString("splitBeforeLineId", value.SplitBeforeLineId); writer.WriteString("newClipId", value.NewClipId); break;
            case MergeOrchestrationClipsCommandDto value:
                writer.WriteString("branchId", value.BranchId); writer.WriteString("targetClipId", value.TargetClipId);
                writer.WritePropertyName("clipIds"); JsonSerializer.Serialize(writer, value.ClipIds, options); break;
        }
        // Preserve inherited fields in fingerprints so malformed in-memory requests cannot alias valid ones.
        if (command.Target is not null) { writer.WritePropertyName("target"); JsonSerializer.Serialize(writer, command.Target, options); }
        if (command.StartUs is not null) writer.WriteString("startUs", command.StartUs);
        if (command.EndUs is not null) writer.WriteString("endUs", command.EndUs);
        if (command.AnalysisJobId is not null) writer.WriteString("analysisJobId", command.AnalysisJobId);
        writer.WriteEndObject();
    }
}
