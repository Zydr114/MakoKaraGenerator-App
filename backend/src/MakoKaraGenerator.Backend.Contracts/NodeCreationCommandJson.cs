using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class NodeCreationCommandJson
{
    internal static bool IsType(string? type) => type is "orchestration.createNode" or "orchestration.copySubtree";

    internal static ProjectCommandDto Read(JsonElement root, string type)
    {
        string[] allowed = type == "orchestration.createNode"
            ? ["type", "kind", "parentId", "name", "slotCount"] : ["type", "nodeId", "name"];
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                throw new JsonException("节点创建命令包含重复或未知字段。");
        if (allowed.Any(name => !fields.ContainsKey(name))) throw new JsonException("节点创建命令必须显式提供全部字段。");
        if (fields["name"].ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            || fields["name"].GetString()?.Length > OrchestrationNodeCreationLimits.MaxNameLength)
            throw new JsonException("name 必须是最多256字符的字符串或null。");
        if (type == "orchestration.copySubtree")
            return new CopyOrchestrationSubtreeCommandDto(Id("nodeId", false), fields["name"].GetString());
        if (fields["kind"].ValueKind != JsonValueKind.String
            || fields["kind"].GetString() is not ("group" or "auto" or "slotSequence"))
            throw new JsonException("kind 必须是 group、auto 或 slotSequence。");
        string kind = fields["kind"].GetString()!;
        int? count = null;
        if (kind == "slotSequence")
        {
            if (fields["slotCount"].ValueKind != JsonValueKind.Number || !fields["slotCount"].TryGetInt32(out int value)
                || value is < 1 or > OrchestrationNodeCreationLimits.MaxSlots)
                throw new JsonException("slotSequence 的 slotCount 必须是1..64整数。");
            count = value;
        }
        else if (fields["slotCount"].ValueKind != JsonValueKind.Null)
            throw new JsonException("group 和 auto 的 slotCount 必须显式为null。");
        return new CreateOrchestrationNodeCommandDto(kind, Id("parentId", true), fields["name"].GetString(), count);

        string? Id(string name, bool nullable)
        {
            var value = fields[name];
            if (nullable && value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 128)
                throw new JsonException("节点身份必须是1..128字符的非空白字符串。");
            return value.GetString();
        }
    }

    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", command.Type);
        if (command is CreateOrchestrationNodeCommandDto create)
        {
            writer.WriteString("kind", create.Kind); writer.WriteString("parentId", create.ParentId);
            writer.WriteString("name", create.Name); writer.WritePropertyName("slotCount"); JsonSerializer.Serialize(writer, create.SlotCount, options);
        }
        else if (command is CopyOrchestrationSubtreeCommandDto copy)
        { writer.WriteString("nodeId", copy.NodeId); writer.WriteString("name", copy.Name); }
        // Keep malformed inherited fields visible to the same strict admission as HTTP.
        if (command.Target is not null) { writer.WritePropertyName("target"); JsonSerializer.Serialize(writer, command.Target, options); }
        if (command.StartUs is not null) writer.WriteString("startUs", command.StartUs);
        if (command.EndUs is not null) writer.WriteString("endUs", command.EndUs);
        if (command.AnalysisJobId is not null) writer.WriteString("analysisJobId", command.AnalysisJobId);
        writer.WriteEndObject();
    }
}
