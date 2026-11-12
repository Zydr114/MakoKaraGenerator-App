using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class LeadInEditingCommandJson
{
    internal static bool IsType(string? type) => type is "orchestration.setLeadInGeneration" or "orchestration.previewLeadInGeneration"
        or "orchestration.applyLeadInGeneration" or "orchestration.deleteLeadInAndExclude" or "orchestration.addMark"
        or "orchestration.setMarkText" or "orchestration.reorderMarks" or "orchestration.spaceMarks"
        or "orchestration.setCountdownAppearance" or "orchestration.setMarkAppearance" or "orchestration.setLeadInAttachment";

    internal static ProjectCommandDto Read(JsonElement root, string type, JsonSerializerOptions options)
    {
        string[] names = type switch
        {
            "orchestration.setLeadInGeneration" or "orchestration.previewLeadInGeneration" => ["type", "branchId", "settings"],
            "orchestration.applyLeadInGeneration" => ["type", "branchId", "settings", "selectedLineIds"],
            "orchestration.deleteLeadInAndExclude" => ["type", "branchId", "leadInId"],
            "orchestration.setLeadInAttachment" => ["type", "branchId", "leadInId", "containerId", "mode"],
            "orchestration.addMark" => ["type", "containerId", "text"],
            "orchestration.setMarkText" => ["type", "nodeId", "text"],
            "orchestration.reorderMarks" => ["type", "containerId", "orderedIds"],
            "orchestration.spaceMarks" => ["type", "containerId"],
            "orchestration.setCountdownAppearance" => ["type", "containerId", "appearance"],
            _ => ["type", "nodeId", "changes"],
        };
        Fields(root, names);
        LeadInGenerationSettingsDto? settings = null;
        if (root.TryGetProperty("settings", out var configuration))
        {
            if (configuration.ValueKind == JsonValueKind.Null)
            { if (type != "orchestration.setLeadInGeneration") throw new JsonException("生成分析和应用需要完整settings。"); }
            else
            {
                Fields(configuration, ["includeOpening", "includeLongGaps", "gapThresholdUs", "durationUs", "indicatorContainerId", "excludedLineIds"]);
                if (configuration.GetProperty("includeOpening").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || configuration.GetProperty("includeLongGaps").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("生成开关必须是bool。");
                settings = new(configuration.GetProperty("includeOpening").GetBoolean(), configuration.GetProperty("includeLongGaps").GetBoolean(),
                    Text(configuration, "gapThresholdUs")!, Text(configuration, "durationUs")!, Text(configuration, "indicatorContainerId", true),
                    Strings(configuration.GetProperty("excludedLineIds")));
            }
        }
        return type switch
        {
            "orchestration.setLeadInGeneration" => new SetLeadInGenerationCommandDto(Text(root, "branchId"), settings),
            "orchestration.previewLeadInGeneration" => new PreviewLeadInGenerationCommandDto(Text(root, "branchId"), settings),
            "orchestration.applyLeadInGeneration" => new ApplyLeadInGenerationCommandDto(Text(root, "branchId"), settings, Strings(root.GetProperty("selectedLineIds"))),
            "orchestration.setLeadInAttachment" => new SetLeadInAttachmentCommandDto(Text(root, "branchId"), Text(root, "leadInId"), Text(root, "containerId"), Mode(root)),
            "orchestration.deleteLeadInAndExclude" => new DeleteLeadInAndExcludeCommandDto(Text(root, "branchId"), Text(root, "leadInId")),
            "orchestration.addMark" => new AddOrchestrationMarkCommandDto(Text(root, "containerId"), Text(root, "text")),
            "orchestration.setMarkText" => new SetOrchestrationMarkTextCommandDto(Text(root, "nodeId"), Text(root, "text")),
            "orchestration.reorderMarks" => new ReorderOrchestrationMarksCommandDto(Text(root, "containerId"), Strings(root.GetProperty("orderedIds"))),
            "orchestration.spaceMarks" => new SpaceOrchestrationMarksCommandDto(Text(root, "containerId")),
            "orchestration.setCountdownAppearance" => new SetCountdownAppearanceCommandDto(Text(root, "containerId"), Appearance(root.GetProperty("appearance"), options)),
            _ => new SetMarkAppearanceCommandDto(Text(root, "nodeId"), Changes(root.GetProperty("changes"), options)),
        };
    }

    private static string Mode(JsonElement root)
    {
        string value = Text(root, "mode")!;
        return value is "FixedParent" or "CueTargetText" ? value : throw new JsonException("mode必须是FixedParent或CueTargetText。");
    }
    private static SceneTextAppearanceDto Appearance(JsonElement value, JsonSerializerOptions options)
    {
        Fields(value, ["typography", "pending", "elapsed", "opacity"]);
        return new(StyleCommandJson.Typography(value.GetProperty("typography")), StyleCommandJson.Appearance(value.GetProperty("pending")),
            StyleCommandJson.SungAppearance(value.GetProperty("elapsed")), StyleCommandJson.Float(value.GetProperty("opacity")));
    }
    // 文字标记外观可写的属性：除扫字样式与 verticalCenterRatio、horizontalAlignment、scale 之外的 34 条路径。
    internal static readonly int MarkPropertyCount = StyleCommandJson.Properties.Count(IsMarkProperty);
    internal static bool IsMarkProperty(string property) => !property.StartsWith("wipe.", StringComparison.Ordinal)
        && property is not ("verticalCenterRatio" or "horizontalAlignment" or "scale");
    private static StyleChangeDto[] Changes(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MarkPropertyCount) throw new JsonException("changes必须是最多34项的完整覆盖列表。");
        return value.Deserialize<StyleChangeDto[]>(options) ?? throw new JsonException();
    }
    private static string[] Strings(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1000) throw new JsonException("身份列表最多1000项。");
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new JsonException("身份必须是字符串。")).ToArray();
    }
    private static string? Text(JsonElement root, string name, bool nullable = false)
    {
        var value = root.GetProperty(name);
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("字段必须是字符串。");
        return value.GetString();
    }
    private static void Fields(JsonElement root, string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("字段必须是对象。");
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name)) throw new JsonException("重复或未知字段。");
        if (names.Count != allowed.Length) throw new JsonException("必须显式提供全部字段。");
    }

    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", command.Type);
        switch (command)
        {
            case SetLeadInGenerationCommandDto v: Settings(v.BranchId, v.Settings); break;
            case PreviewLeadInGenerationCommandDto v: Settings(v.BranchId, v.Settings); break;
            case ApplyLeadInGenerationCommandDto v: Settings(v.BranchId, v.Settings); Value("selectedLineIds", v.SelectedLineIds); break;
            case SetLeadInAttachmentCommandDto v: writer.WriteString("branchId", v.BranchId); writer.WriteString("leadInId", v.LeadInId); writer.WriteString("containerId", v.ContainerId); writer.WriteString("mode", v.Mode); break;
            case DeleteLeadInAndExcludeCommandDto v: writer.WriteString("branchId", v.BranchId); writer.WriteString("leadInId", v.LeadInId); break;
            case AddOrchestrationMarkCommandDto v: writer.WriteString("containerId", v.ContainerId); writer.WriteString("text", v.Text); break;
            case SetOrchestrationMarkTextCommandDto v: writer.WriteString("nodeId", v.NodeId); writer.WriteString("text", v.Text); break;
            case ReorderOrchestrationMarksCommandDto v: writer.WriteString("containerId", v.ContainerId); Value("orderedIds", v.OrderedIds); break;
            case SpaceOrchestrationMarksCommandDto v: writer.WriteString("containerId", v.ContainerId); break;
            case SetCountdownAppearanceCommandDto v: writer.WriteString("containerId", v.ContainerId); Value("appearance", v.Appearance); break;
            case SetMarkAppearanceCommandDto v: writer.WriteString("nodeId", v.NodeId); Value("changes", v.Changes); break;
        }
        if (command.Target is not null) Value("target", command.Target);
        if (command.StartUs is not null) writer.WriteString("startUs", command.StartUs);
        if (command.EndUs is not null) writer.WriteString("endUs", command.EndUs);
        if (command.AnalysisJobId is not null) writer.WriteString("analysisJobId", command.AnalysisJobId);
        writer.WriteEndObject();
        void Value<T>(string name, T value) { writer.WritePropertyName(name); JsonSerializer.Serialize(writer, value, options); }
        void Settings(string? branch, LeadInGenerationSettingsDto? value)
        {
            writer.WriteString("branchId", branch); writer.WritePropertyName("settings");
            if (value is null) { writer.WriteNullValue(); return; }
            writer.WriteStartObject(); writer.WriteBoolean("includeOpening", value.IncludeOpening); writer.WriteBoolean("includeLongGaps", value.IncludeLongGaps);
            writer.WriteString("gapThresholdUs", value.GapThresholdUs); writer.WriteString("durationUs", value.DurationUs);
            writer.WriteString("indicatorContainerId", value.IndicatorContainerId); Value("excludedLineIds", value.ExcludedLineIds); writer.WriteEndObject();
        }
    }
}
