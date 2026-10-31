using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class LeadInCommandJson
{
    internal static bool IsType(string? type) => type is "orchestration.createCountdown" or "orchestration.createLeadIn"
        or "orchestration.updateLeadIn" or "orchestration.deleteLeadIn";

    internal static ProjectCommandDto Read(JsonElement root, string type, JsonSerializerOptions options)
    {
        StringComparer comparer = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string[] allowed = type switch
        {
            "orchestration.createCountdown" => ["type", "parentId", "name", "count"],
            "orchestration.deleteLeadIn" => ["type", "branchId", "leadInId"],
            _ => ["type", "branchId", "leadInId", "name", "targetLineId", "indicatorContainerId", "durationUs", "enabled"],
        };
        var fields = new Dictionary<string, JsonElement>(comparer);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, comparer) || !fields.TryAdd(property.Name, property.Value))
                throw new JsonException("前奏命令包含重复或未知字段。");
        if (allowed.Any(name => !fields.ContainsKey(name))) throw new JsonException("前奏命令必须显式提供全部字段。");
        foreach (string name in allowed.Where(name => name is not ("count" or "enabled")))
            if (fields[name].ValueKind != JsonValueKind.String && !(name == "name" && fields[name].ValueKind == JsonValueKind.Null))
                throw new JsonException("前奏身份、时间和名称必须是字符串，名称允许null。");
        if (type == "orchestration.createCountdown")
        {
            if (fields["count"].ValueKind != JsonValueKind.Number || !fields["count"].TryGetInt32(out int count) || count is < 1 or > 64) throw new JsonException("count必须是1..64的整数。");
            return new CreateOrchestrationCountdownCommandDto(Text("parentId"), Text("name"), count);
        }
        if (type == "orchestration.deleteLeadIn") return new DeleteOrchestrationLeadInCommandDto(Text("branchId"), Text("leadInId"));
        if (fields["enabled"].ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("enabled必须是bool。");
        return type == "orchestration.createLeadIn"
            ? new CreateOrchestrationLeadInCommandDto(Text("branchId"), Text("leadInId"), Text("name"), Text("targetLineId"), Text("indicatorContainerId"), Text("durationUs"), fields["enabled"].GetBoolean())
            : new UpdateOrchestrationLeadInCommandDto(Text("branchId"), Text("leadInId"), Text("name"), Text("targetLineId"), Text("indicatorContainerId"), Text("durationUs"), fields["enabled"].GetBoolean());
        string? Text(string name) => fields[name].GetString();
    }

    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", command.Type);
        switch (command)
        {
            case CreateOrchestrationCountdownCommandDto value:
                writer.WriteString("parentId", value.ParentId); writer.WriteString("name", value.Name);
                writer.WritePropertyName("count"); JsonSerializer.Serialize(writer, value.Count, options); break;
            case CreateOrchestrationLeadInCommandDto value:
                Cue(value.BranchId, value.LeadInId, value.Name, value.TargetLineId, value.IndicatorContainerId, value.DurationUs, value.Enabled); break;
            case UpdateOrchestrationLeadInCommandDto value:
                Cue(value.BranchId, value.LeadInId, value.Name, value.TargetLineId, value.IndicatorContainerId, value.DurationUs, value.Enabled); break;
            case DeleteOrchestrationLeadInCommandDto value:
                writer.WriteString("branchId", value.BranchId); writer.WriteString("leadInId", value.LeadInId); break;
        }
        // Preserve malformed in-process fields so strict admission rejects them too.
        if (command.Target is not null) { writer.WritePropertyName("target"); JsonSerializer.Serialize(writer, command.Target, options); }
        if (command.StartUs is not null) writer.WriteString("startUs", command.StartUs);
        if (command.EndUs is not null) writer.WriteString("endUs", command.EndUs);
        if (command.AnalysisJobId is not null) writer.WriteString("analysisJobId", command.AnalysisJobId);
        writer.WriteEndObject();
        void Cue(string? branch, string? id, string? name, string? target, string? indicator, string? duration, bool? enabled)
        {
            writer.WriteString("branchId", branch); writer.WriteString("leadInId", id); writer.WriteString("name", name);
            writer.WriteString("targetLineId", target); writer.WriteString("indicatorContainerId", indicator); writer.WriteString("durationUs", duration);
            writer.WritePropertyName("enabled"); JsonSerializer.Serialize(writer, enabled, options);
        }
    }
}
