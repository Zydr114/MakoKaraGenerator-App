using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>按 type 解析具名命令，保留五个既有命令的 JSON 与显式空字段兼容性。</summary>
public sealed class ProjectCommandJsonConverter : JsonConverter<ProjectCommandDto>
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeof(ProjectCommandDto).IsAssignableFrom(typeToConvert);

    /// <inheritdoc />
    public override ProjectCommandDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("command 必须是对象。");
        if (root.TryGetProperty("type", out var editType) && editType.ValueKind == JsonValueKind.String && editType.GetString() == "project.applyEdits")
        {
            var editFields = root.EnumerateObject().ToArray();
            if (editFields.Length != 2 || editFields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 2
                || editFields.Any(field => field.Name is not ("type" or "edits"))
                || !root.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array)
                throw new JsonException("project.applyEdits 必须恰好包含 type 和 edits 数组。");
            var batch = new ApplyProjectEditsCommandDto(edits.Deserialize<ProjectCommandDto[]>(options));
            if (!typeToConvert.IsInstanceOfType(batch)) throw new JsonException("命令type与DTO不一致。");
            return batch;
        }
        if (root.TryGetProperty("type", out var beatType) && beatType.ValueKind == JsonValueKind.String && LyricBeatCommandJson.IsType(beatType.GetString()))
        {
            var beat = LyricBeatCommandJson.Read(root, beatType.GetString()!);
            if (!typeToConvert.IsInstanceOfType(beat)) throw new JsonException("命令type与指定DTO类型不一致。");
            return beat;
        }
        if (root.TryGetProperty("type", out var rubyType) && rubyType.ValueKind == JsonValueKind.String && RubySegmentCommandJson.IsType(rubyType.GetString()))
        {
            var ruby = RubySegmentCommandJson.Read(root, rubyType.GetString()!);
            if (!typeToConvert.IsInstanceOfType(ruby)) throw new JsonException("命令type与指定DTO类型不一致。");
            return ruby;
        }
        if (root.TryGetProperty("type", out var clipMembersType) && clipMembersType.ValueKind == JsonValueKind.String && ClipMembersCommandJson.IsType(clipMembersType.GetString()))
        {
            var members = ClipMembersCommandJson.Read(root, clipMembersType.GetString()!);
            if (!typeToConvert.IsInstanceOfType(members)) throw new JsonException("命令type与指定DTO类型不一致。");
            return members;
        }
        if (root.TryGetProperty("type", out var backgroundType) && backgroundType.ValueKind == JsonValueKind.String && BackgroundCommandJson.IsType(backgroundType.GetString()))
        {
            var background = BackgroundCommandJson.Read(root, backgroundType.GetString()!);
            if (!typeToConvert.IsInstanceOfType(background)) throw new JsonException("命令type与指定DTO类型不一致。");
            return background;
        }
        if (root.TryGetProperty("type", out var basicMediaType) && basicMediaType.ValueKind == JsonValueKind.String && BasicMediaCommandJson.IsType(basicMediaType.GetString()))
        {
            var media = BasicMediaCommandJson.Read(root, basicMediaType.GetString()!);
            if (!typeToConvert.IsInstanceOfType(media)) throw new JsonException("命令type与指定DTO类型不一致。");
            return media;
        }
        if (root.TryGetProperty("type", out var leadInEditingType) && leadInEditingType.ValueKind == JsonValueKind.String && LeadInEditingCommandJson.IsType(leadInEditingType.GetString()))
        {
            var leadInEditing = LeadInEditingCommandJson.Read(root, leadInEditingType.GetString()!, options);
            if (!typeToConvert.IsInstanceOfType(leadInEditing)) throw new JsonException("命令type与指定DTO类型不一致。");
            return leadInEditing;
        }
        if (root.TryGetProperty("type", out var completionType) && completionType.ValueKind == JsonValueKind.String && OrchestrationEditingCompletionJson.IsType(completionType.GetString()))
        {
            var completion = OrchestrationEditingCompletionJson.Read(root, completionType.GetString()!);
            if (!typeToConvert.IsInstanceOfType(completion)) throw new JsonException("命令type与指定DTO类型不一致。");
            return completion;
        }
        if (root.TryGetProperty("type", out var nodeType) && nodeType.ValueKind == JsonValueKind.String && NodeCreationCommandJson.IsType(nodeType.GetString()))
        {
            var node = NodeCreationCommandJson.Read(root, nodeType.GetString()!);
            if (!typeToConvert.IsInstanceOfType(node)) throw new JsonException("命令 type 与指定 DTO 类型不一致。");
            return node;
        }
        if (root.TryGetProperty("type", out var importType) && importType.ValueKind == JsonValueKind.String && importType.GetString() is "lyrics.importAsset" or "lyrics.appendText")
        {
            var imported = ImportCommandJson.Read(root, importType.GetString()!, options);
            if (!typeToConvert.IsInstanceOfType(imported)) throw new JsonException("命令 type 与指定 DTO 类型不一致。");
            return imported;
        }
        if (root.TryGetProperty("type", out var styleType) && styleType.ValueKind == JsonValueKind.String && styleType.GetString() is "styles.patch" or "styles.restore")
        {
            var styled = StyleCommandJson.Read(root, styleType.GetString()!, options);
            if (!typeToConvert.IsInstanceOfType(styled)) throw new JsonException("命令 type 与指定 DTO 类型不一致。");
            return styled;
        }
        if (root.TryGetProperty("type", out var leadInType) && leadInType.ValueKind == JsonValueKind.String && LeadInCommandJson.IsType(leadInType.GetString()))
        {
            var leadIn = LeadInCommandJson.Read(root, leadInType.GetString()!, options);
            if (!typeToConvert.IsInstanceOfType(leadIn)) throw new JsonException("命令 type 与指定 DTO 类型不一致。");
            return leadIn;
        }
        if (root.TryGetProperty("type", out var localType) && localType.ValueKind == JsonValueKind.String
            && localType.GetString() == "orchestration.setLineLocalTransform")
        {
            var local = LineLocalTransformCommandJson.Read(root);
            if (!typeToConvert.IsInstanceOfType(local)) throw new JsonException("命令 type 与指定 DTO 类型不一致。");
            return local;
        }
        if (root.TryGetProperty("type", out var propertyType) && propertyType.ValueKind == JsonValueKind.String
            && OrchestrationPropertiesCommandJson.IsType(propertyType.GetString()))
        {
            var property = OrchestrationPropertiesCommandJson.Read(root, propertyType.GetString()!);
            if (!typeToConvert.IsInstanceOfType(property)) throw new JsonException("命令 type 与指定 DTO 类型不一致。");
            return property;
        }
        StringComparer comparer = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var fields = new Dictionary<string, JsonElement>(comparer);
        bool duplicate = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value)) duplicate = true;
            fields[property.Name] = property.Value;
        }
        bool typed = false;
        string? type = null;
        type = ReadField<string>("type");
        if (type == "lyrics.resplitLine" && fields.TryGetValue("segments", out JsonElement segments)
            && segments.ValueKind == JsonValueKind.Array && segments.GetArrayLength() > TokenEditingLimits.MaxSegments)
            throw new ProjectCommandBudgetExceededException();
        string[] allowed = type switch
        {
            "lyrics.stampTokenStart" or "lyrics.stampTokenEnd" => ["type", "target", "playheadUs"],
            "lyrics.clearTokenTime" => ["type", "target"],
            "lyrics.nudgeTokenTime" => ["type", "target", "frameDelta"],
            "lyrics.shiftLine" => ["type", "target", "deltaUs"],
            "lyrics.setTokenTimes" => ["type", "changes"],
            "lyrics.splitToken" or "lyrics.mergeToken" => ["type", "target"],
            "lyrics.reorderToken" => ["type", "target", "destinationIndex", "reassignTimeSlots"],
            "lyrics.resplitLine" => ["type", "target", "segments", "estimateTimes"],
            "lyrics.resplitBranch" => ["type", "branchId", "rule", "estimateTimes"],
            "lyrics.setLineText" or "lyrics.setRubyText" => ["type", "target", "text"],
            "lyrics.addLine" => ["type", "branchId", "afterLineId"],
            "lyrics.duplicateLine" or "lyrics.removeLine" => ["type", "target"],
            "composition.setCanvasSize" => ["type", "width", "height"],
            "composition.setFrameRate" => ["type", "numerator", "denominator"],
            "composition.setBackgroundColor" => ["type", "red", "green", "blue", "alpha"],
            "composition.setOutputDuration" => ["type", "durationUs"],
            "composition.useLyricsEnd" or "composition.useAudioDuration" => ["type"],
            "lyrics.addTranslationBranch" => ["type", "name", "language"],
            "lyrics.configureBranch" => ["type", "branchId", "name", "language", "role", "timingMode"],
            "lyrics.removeBranch" => ["type", "branchId"],
            "lyrics.linkPrimaryLine" => ["type", "branchId", "lineId", "primaryLineId"],
            "presetApplications.apply" => ["type", "templateLibraryId", "templateId", "templateRevision", "targets", "branchInputs"],
            "orchestration.setDefaultContainer" => ["type", "branchId", "containerId"],
            "orchestration.setLayoutDefaultPlacement" => ["type", "containerId", "placement"],
            "orchestration.setNode" => ["type", "nodeId", "name"],
            "orchestration.reparent" => ["type", "nodeId", "parentId"],
            "orchestration.reorderChildren" => ["type", "parentId", "orderedIds"],
            "orchestration.addSlot" => ["type", "containerId", "slotId", "name"],
            "orchestration.removeNode" => ["type", "nodeId"],
            "orchestration.reorderSlots" => ["type", "containerId", "orderedIds"],
            "orchestration.createClip" => ["type", "branchId", "clipId", "name", "lineIds", "containerId"],
            "orchestration.renameClip" => ["type", "branchId", "clipId", "name"],
            "orchestration.setClipContainer" => ["type", "branchId", "clipId", "containerId"],
            "orchestration.deleteClip" => ["type", "branchId", "clipId"],
            "orchestration.previewBasicLayout" or "orchestration.applyBasicLayout" => ["type", "branchId", "preset", "selectedLineIds", "layoutName", "slotNames", "selectedSegmentName", "retainedSegmentName"],
            "presetApplications.update" => ["type", "applicationId", "templateLibraryId", "templateId", "templateRevision", "branchInputs"],
            "presetApplications.setEnabled" => ["type", "applicationId", "enabled"],
            "presetApplications.remove" => ["type", "applicationIds"],
            _ => ["type", "target", "startUs", "endUs", "analysisJobId"],
        };
        typed = type is "lyrics.stampTokenStart" or "lyrics.stampTokenEnd" or "lyrics.clearTokenTime"
            or "lyrics.nudgeTokenTime" or "lyrics.shiftLine" or "lyrics.setTokenTimes"
            or "lyrics.setLineText" or "lyrics.setRubyText" or "lyrics.addLine" or "lyrics.duplicateLine" or "lyrics.removeLine"
            or "composition.setCanvasSize" or "composition.setFrameRate" or "composition.setBackgroundColor"
            or "composition.setOutputDuration" or "composition.useLyricsEnd" or "composition.useAudioDuration"
            or "lyrics.addTranslationBranch" or "lyrics.configureBranch" or "lyrics.removeBranch" or "lyrics.linkPrimaryLine"
            or "orchestration.setDefaultContainer" or "orchestration.setLayoutDefaultPlacement" or "presetApplications.apply" or "presetApplications.update" or "presetApplications.setEnabled" or "presetApplications.remove"
            or "orchestration.setNode" or "orchestration.reparent" or "orchestration.reorderChildren" or "orchestration.addSlot" or "orchestration.removeNode" or "orchestration.reorderSlots"
            or "orchestration.createClip" or "orchestration.renameClip" or "orchestration.setClipContainer" or "orchestration.deleteClip"
            or "orchestration.previewBasicLayout" or "orchestration.applyBasicLayout"
            or "lyrics.splitToken" or "lyrics.mergeToken" or "lyrics.reorderToken" or "lyrics.resplitLine" or "lyrics.resplitBranch";
        if ((typed && duplicate) || fields.Keys.Any(key => !allowed.Contains(key, comparer)))
            throw new JsonException("命令包含重复或不属于其 type 的字段。");
        // Shape validation precedes domain lookup, so stale targets cannot mask missing required fields.
        bool requiresTarget = typed && allowed.Contains("target", comparer);
        if (requiresTarget && (!fields.TryGetValue("target", out JsonElement target) || target.ValueKind != JsonValueKind.Object))
            throw new JsonException("此命令必须提供对象 target。");
        switch (type)
        {
            case "lyrics.reorderToken":
                RequireKind("destinationIndex", JsonValueKind.Number);
                RequireBoolean("reassignTimeSlots");
                break;
            case "lyrics.resplitLine":
                RequireKind("segments", JsonValueKind.Array);
                if (fields["segments"].EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                    throw new JsonException("segments 的每个项必须是非空值字符串。");
                RequireBoolean("estimateTimes");
                break;
            case "lyrics.resplitBranch":
                RequireKind("branchId", JsonValueKind.String);
                RequireKind("rule", JsonValueKind.String);
                RequireBoolean("estimateTimes");
                break;
        }
        if (type == "composition.setOutputDuration" && !fields.ContainsKey("durationUs"))
            throw new JsonException("durationUs 必须显式提供，空值表示尚未确定。");
        string[] required = type switch
        {
            "lyrics.addTranslationBranch" => ["name"],
            "lyrics.configureBranch" => ["branchId", "name", "language", "role", "timingMode"],
            "lyrics.removeBranch" => ["branchId"],
            "lyrics.linkPrimaryLine" => ["branchId", "lineId", "primaryLineId"],
            "presetApplications.apply" => ["templateLibraryId", "templateId", "templateRevision", "targets"],
            "presetApplications.update" => ["applicationId", "templateLibraryId", "templateId", "templateRevision"],
            "presetApplications.setEnabled" => ["applicationId", "enabled"],
            "presetApplications.remove" => ["applicationIds"],
            "orchestration.setDefaultContainer" => ["branchId", "containerId"],
            "orchestration.setLayoutDefaultPlacement" => ["containerId", "placement"],
            "orchestration.setNode" => ["nodeId", "name"],
            "orchestration.reparent" => ["nodeId", "parentId"],
            "orchestration.reorderChildren" => ["parentId", "orderedIds"],
            "orchestration.addSlot" => ["containerId", "slotId", "name"],
            "orchestration.removeNode" => ["nodeId"],
            "orchestration.reorderSlots" => ["containerId", "orderedIds"],
            "orchestration.createClip" => ["branchId", "clipId", "lineIds", "containerId"],
            "orchestration.renameClip" => ["branchId", "clipId", "name"],
            "orchestration.setClipContainer" => ["branchId", "clipId", "containerId"],
            "orchestration.deleteClip" => ["branchId", "clipId"],
            "orchestration.previewBasicLayout" or "orchestration.applyBasicLayout" => ["branchId", "preset", "layoutName", "slotNames", "selectedSegmentName", "retainedSegmentName"],
            _ => [],
        };
        if (required.Any(key => !fields.ContainsKey(key))) throw new JsonException("来源命令缺少必填字段。");
        if (type == "orchestration.setDefaultContainer") { RequireString("branchId"); RequireString("containerId"); }
        if (type == "orchestration.setLayoutDefaultPlacement")
        {
            RequireString("containerId");
            if (!fields.TryGetValue("placement", out JsonElement placement)
                || placement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                throw new JsonException("placement 必须显式提供对象或 null。");
            if (placement.ValueKind == JsonValueKind.Object)
            {
                string[] placementFields = ["x", "y", "anchor", "rotationDegrees", "scale"];
                var names = placement.EnumerateObject().Select(property => property.Name).ToArray();
                if (names.Any(name => !placementFields.Contains(name, comparer))
                    || names.Length != placementFields.Length
                    || placementFields.Any(name => !names.Contains(name, comparer)))
                    throw new JsonException("placement 必须恰好包含 x、y、anchor、rotationDegrees、scale。");
                JsonElement Field(string name) => placement.EnumerateObject().First(property => comparer.Equals(property.Name, name)).Value;
                foreach (string name in new[] { "x", "y", "rotationDegrees", "scale" })
                    if (Field(name).ValueKind != JsonValueKind.Number)
                        throw new JsonException($"placement.{name} 必须是 JSON 数值。");
                if (!double.IsFinite(Field("x").GetDouble())
                    || !double.IsFinite(Field("y").GetDouble())
                    || !double.IsFinite(Field("rotationDegrees").GetDouble())
                    || !double.IsFinite(Field("scale").GetDouble())
                    || Field("scale").GetDouble() <= 0d)
                    throw new JsonException("placement 数值必须为有限值且 scale 必须大于0。");
                JsonElement anchorValue = Field("anchor");
                string? anchor = anchorValue.ValueKind == JsonValueKind.String ? anchorValue.GetString() : null;
                if (anchor is null || !new[] { "Center", "TopLeft", "TopCenter", "BottomRight", "LeftCenter", "RightCenter" }
                        .Contains(anchor, StringComparer.OrdinalIgnoreCase))
                    throw new JsonException("placement.anchor 必须是公开的锚点名称。");
            }
        }
        if (type is "orchestration.setNode" or "orchestration.removeNode") RequireString("nodeId");
        if (type == "orchestration.setNode")
        {
            if (!fields.TryGetValue("name", out JsonElement name) || name.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new JsonException("name 必须显式提供字符串或 null。");
        }
        if (type == "orchestration.reparent")
        {
            RequireString("nodeId"); RequireStringOrNull("parentId");
        }
        if (type is "orchestration.reorderChildren" or "orchestration.reorderSlots")
        {
            if (type == "orchestration.reorderChildren") RequireStringOrNull("parentId");
            else RequireString("containerId");
            RequireStringArray("orderedIds", 1, 2000);
        }
        if (type == "orchestration.addSlot")
        {
            RequireString("containerId"); RequireString("slotId");
            if (!fields.TryGetValue("name", out JsonElement slotName) || slotName.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new JsonException("name 必须显式提供字符串或 null。");
        }
        if (type is "orchestration.createClip" or "orchestration.renameClip" or "orchestration.setClipContainer" or "orchestration.deleteClip")
        {
            RequireString("branchId"); RequireString("clipId");
            if (type == "orchestration.createClip")
            {
                RequireKind("lineIds", JsonValueKind.Array); RequireString("containerId");
                if (fields["lineIds"].GetArrayLength() is < 1 or > 1000
                    || fields["lineIds"].EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
                    throw new JsonException("lineIds 必须包含1至1000个非空字符串身份。");
            }
            else if (type == "orchestration.setClipContainer") RequireString("containerId");
            else if (type == "orchestration.renameClip")
            {
                if (!fields.TryGetValue("name", out JsonElement name) || name.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new JsonException("name 必须显式提供字符串或 null。");
            }
        }
        if (type is "orchestration.previewBasicLayout" or "orchestration.applyBasicLayout")
        {
            RequireString("branchId"); RequireString("preset"); RequireString("layoutName");
            RequireKind("slotNames", JsonValueKind.Array); RequireKind("selectedSegmentName", JsonValueKind.String);
            RequireKind("retainedSegmentName", JsonValueKind.String);
            if (fields["slotNames"].GetArrayLength() is < 1 or > 3 || fields["slotNames"].EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new JsonException("slotNames 必须包含1至3个字符串。");
            if (fields.TryGetValue("selectedLineIds", out JsonElement selected) && selected.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                throw new JsonException("selectedLineIds 必须是数组或显式null。");
        }
        if (type is "presetApplications.apply" or "presetApplications.update")
        {
            RequireString("templateLibraryId"); RequireString("templateId"); RequireString("templateRevision");
            if (fields["templateLibraryId"].GetString() is not ("builtin" or "personal"))
                throw new JsonException("templateLibraryId 必须是 builtin 或 personal。");
            if (type == "presetApplications.apply")
            {
                RequireKind("targets", JsonValueKind.Array);
                if (fields["targets"].GetArrayLength() is < 1 or > 1000) throw new JsonException("targets 必须包含1至1000个目标。");
            }
            if (fields.TryGetValue("branchInputs", out JsonElement inputs) && inputs.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                throw new JsonException("branchInputs 必须是对象或显式null。");
        }
        else if (type == "presetApplications.setEnabled")
        {
            RequireString("applicationId"); RequireBoolean("enabled");
        }
        else if (type == "presetApplications.remove")
        {
            RequireKind("applicationIds", JsonValueKind.Array);
            if (fields["applicationIds"].GetArrayLength() is < 1 or > 1000
                || fields["applicationIds"].EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new JsonException("applicationIds 必须包含1至1000个字符串身份。");
        }
        ProjectCommandDto command = type switch
        {
            "lyrics.stampTokenStart" => new StampTokenStartCommandDto(ReadField<LyricTimingTargetDto>("target"), ReadField<string>("playheadUs")),
            "lyrics.stampTokenEnd" => new StampTokenEndCommandDto(ReadField<LyricTimingTargetDto>("target"), ReadField<string>("playheadUs")),
            "lyrics.clearTokenTime" => new ClearTokenTimeCommandDto(ReadField<LyricTimingTargetDto>("target")),
            "lyrics.nudgeTokenTime" => new NudgeTokenTimeCommandDto(ReadField<LyricTimingTargetDto>("target"), ReadField<int?>("frameDelta")),
            "lyrics.shiftLine" => new ShiftLineCommandDto(ReadField<LyricTimingTargetDto>("target"), ReadField<string>("deltaUs")),
            "lyrics.setTokenTimes" => new SetTokenTimesCommandDto(ReadField<TokenTimeChangeDto[]>("changes")),
            "lyrics.splitToken" => new SplitTokenCommandDto(ReadField<LyricTimingTargetDto>("target")),
            "lyrics.mergeToken" => new MergeTokenCommandDto(ReadField<LyricTimingTargetDto>("target")),
            "lyrics.reorderToken" => new ReorderTokenCommandDto(ReadField<LyricTimingTargetDto>("target"), ReadField<int?>("destinationIndex"), ReadField<bool?>("reassignTimeSlots")),
            "lyrics.resplitLine" => new ResplitLineCommandDto(ReadField<LyricTimingTargetDto>("target"), ReadField<string[]>("segments"), ReadField<bool?>("estimateTimes")),
            "lyrics.resplitBranch" => new ResplitBranchCommandDto(ReadField<string>("branchId"), ReadRule(), ReadField<bool?>("estimateTimes")),
            "lyrics.setLineText" => new SetLineTextCommandDto(ReadField<LyricTimingTargetDto>("target"), ReadField<string>("text")),
            "lyrics.setRubyText" => new SetRubyTextCommandDto(ReadField<LyricTimingTargetDto>("target"), ReadField<string>("text")),
            "lyrics.addLine" => new AddLineCommandDto(ReadField<string>("branchId"), ReadField<string>("afterLineId")),
            "lyrics.duplicateLine" => new DuplicateLineCommandDto(ReadField<LyricTimingTargetDto>("target")),
            "lyrics.removeLine" => new RemoveLineCommandDto(ReadField<LyricTimingTargetDto>("target")),
            "composition.setCanvasSize" => new SetCanvasSizeCommandDto(ReadField<int?>("width"), ReadField<int?>("height")),
            "composition.setFrameRate" => new SetFrameRateCommandDto(ReadField<int?>("numerator"), ReadField<int?>("denominator")),
            "composition.setBackgroundColor" => new SetBackgroundColorCommandDto(ReadField<byte?>("red"), ReadField<byte?>("green"), ReadField<byte?>("blue"), ReadField<byte?>("alpha")),
            "composition.setOutputDuration" => new SetOutputDurationCommandDto(ReadField<string>("durationUs")),
            "composition.useLyricsEnd" => new UseLyricsEndCommandDto(),
            "composition.useAudioDuration" => new UseAudioDurationCommandDto(),
            "lyrics.addTranslationBranch" => new AddTranslationBranchCommandDto(ReadField<string>("name"), ReadField<string>("language")),
            "lyrics.configureBranch" => new ConfigureBranchCommandDto(ReadField<string>("branchId"), ReadField<string>("name"), ReadField<string>("language"),
                ReadField<ProjectLyricBranchRole?>("role"), ReadField<ProjectLyricTimingMode?>("timingMode")),
            "lyrics.removeBranch" => new RemoveBranchCommandDto(ReadField<string>("branchId")),
            "lyrics.linkPrimaryLine" => new LinkPrimaryLineCommandDto(ReadField<string>("branchId"), ReadField<string>("lineId"), ReadField<string>("primaryLineId")),
            "orchestration.setDefaultContainer" => new SetOrchestrationDefaultContainerCommandDto(ReadField<string>("branchId"), ReadField<string>("containerId")),
            "orchestration.setLayoutDefaultPlacement" => new SetOrchestrationLayoutDefaultPlacementCommandDto(ReadField<string>("containerId"), ReadField<ProjectOrchestrationPlacementDto>("placement")),
            "orchestration.setNode" => new SetOrchestrationNodeCommandDto(ReadField<string>("nodeId"), ReadField<string>("name")),
            "orchestration.reparent" => new ReparentOrchestrationNodeCommandDto(ReadField<string>("nodeId"), ReadField<string>("parentId")),
            "orchestration.reorderChildren" => new ReorderOrchestrationChildrenCommandDto(ReadField<string>("parentId"), ReadField<string[]>("orderedIds")),
            "orchestration.addSlot" => new AddOrchestrationSlotCommandDto(ReadField<string>("containerId"), ReadField<string>("slotId"), ReadField<string>("name")),
            "orchestration.removeNode" => new RemoveOrchestrationNodeCommandDto(ReadField<string>("nodeId")),
            "orchestration.reorderSlots" => new ReorderOrchestrationSlotsCommandDto(ReadField<string>("containerId"), ReadField<string[]>("orderedIds")),
            "orchestration.createClip" => new CreateOrchestrationClipCommandDto(ReadField<string>("branchId"), ReadField<string>("clipId"), ReadField<string>("name"), ReadField<string[]>("lineIds"), ReadField<string>("containerId")),
            "orchestration.renameClip" => new RenameOrchestrationClipCommandDto(ReadField<string>("branchId"), ReadField<string>("clipId"), ReadField<string>("name")),
            "orchestration.setClipContainer" => new SetOrchestrationClipContainerCommandDto(ReadField<string>("branchId"), ReadField<string>("clipId"), ReadField<string>("containerId")),
            "orchestration.deleteClip" => new DeleteOrchestrationClipCommandDto(ReadField<string>("branchId"), ReadField<string>("clipId")),
            "orchestration.previewBasicLayout" => new BasicLayoutCommandDto(ReadField<string>("branchId"), ReadField<string>("preset"), ReadField<string[]>("selectedLineIds"), ReadField<string>("layoutName"), ReadField<string[]>("slotNames"), ReadField<string>("selectedSegmentName"), ReadField<string>("retainedSegmentName"), true),
            "orchestration.applyBasicLayout" => new BasicLayoutCommandDto(ReadField<string>("branchId"), ReadField<string>("preset"), ReadField<string[]>("selectedLineIds"), ReadField<string>("layoutName"), ReadField<string[]>("slotNames"), ReadField<string>("selectedSegmentName"), ReadField<string>("retainedSegmentName"), false),
            "presetApplications.apply" => new ApplyPresetApplicationCommandDto(ReadField<string>("templateLibraryId"), ReadField<string>("templateId"), ReadField<string>("templateRevision"), ReadField<PresetApplicationTargetDto[]>("targets"), ReadField<Dictionary<string,string>>("branchInputs")),
            "presetApplications.update" => new UpdatePresetApplicationCommandDto(ReadField<string>("applicationId"), ReadField<string>("templateLibraryId"), ReadField<string>("templateId"), ReadField<string>("templateRevision"), ReadField<Dictionary<string,string>>("branchInputs")),
            "presetApplications.setEnabled" => new SetPresetApplicationEnabledCommandDto(ReadField<string>("applicationId"), ReadField<bool?>("enabled")),
            "presetApplications.remove" => new RemovePresetApplicationCommandDto(ReadField<string[]>("applicationIds")),
            _ => new ProjectCommandDto(type!, ReadField<LyricTimingTargetDto>("target"), ReadField<string>("startUs"),
                ReadField<string>("endUs"), ReadField<string>("analysisJobId")),
        };
        if (!typeToConvert.IsInstanceOfType(command)) throw new JsonException("命令 type 与指定 DTO 类型不一致。");
        return command;


        void RequireKind(string name, JsonValueKind kind)
        {
            if (!fields.TryGetValue(name, out JsonElement value) || value.ValueKind != kind)
                throw new JsonException($"{name} 必须显式提供且符合命令字段类型。");
        }

        void RequireBoolean(string name)
        {
            if (!fields.TryGetValue(name, out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException($"{name} 必须显式提供非空值 bool。");
        }
        void RequireString(string name)
        {
            if (!fields.TryGetValue(name, out JsonElement value) || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
                throw new JsonException($"{name} 必须显式提供非空字符串。");
        }
        void RequireStringOrNull(string name)
        {
            if (!fields.TryGetValue(name, out JsonElement value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
                throw new JsonException($"{name} 必须显式提供非空字符串或 null。");
        }
        void RequireStringArray(string name, int min, int max)
        {
            if (!fields.TryGetValue(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array
                || value.GetArrayLength() < min || value.GetArrayLength() > max
                || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
                throw new JsonException($"{name} 必须包含 {min} 至 {max} 个非空字符串。");
        }
        ProjectTokenSplitRule? ReadRule()
        {
            if (!fields.TryGetValue("rule", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String) throw new JsonException("rule 必须是公开的规则字符串。");
            return value.GetString() switch
            {
                "none" => ProjectTokenSplitRule.None,
                "mixed" => ProjectTokenSplitRule.Mixed,
                "perTextElement" => ProjectTokenSplitRule.PerTextElement,
                "latinWords" => ProjectTokenSplitRule.LatinWords,
                "byPunctuation" => ProjectTokenSplitRule.ByPunctuation,
                "byWhitespace" => ProjectTokenSplitRule.ByWhitespace,
                _ => throw new JsonException("rule 不是公开的规则名称。"),
            };
        }

        T? ReadField<T>(string name)
        {
            if (!fields.TryGetValue(name, out JsonElement value)) return default;
            if (type == "lyrics.configureBranch" && name is "role" or "timingMode"
                && (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } enumName || !char.IsAsciiLetter(enumName[0])))
                throw new JsonException("来源枚举必须是命名字符串。");
            if (typed && name is "frameDelta" or "width" or "height" or "numerator" or "denominator" or "red" or "green" or "blue" or "alpha" or "destinationIndex"
                && value.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                throw new JsonException("数值字段必须是 JSON 整数。");
            return value.Deserialize<T>(options);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ProjectCommandDto value, JsonSerializerOptions options)
    {
        if (value is ApplyProjectEditsCommandDto batchEdits)
        {
            writer.WriteStartObject(); writer.WriteString("type", batchEdits.Type);
            writer.WritePropertyName("edits"); JsonSerializer.Serialize(writer, batchEdits.Edits, options);
            writer.WriteEndObject(); return;
        }
        if (BackgroundCommandJson.IsType(value.Type)) { BackgroundCommandJson.Write(writer, value); return; }
        if (LyricBeatCommandJson.IsType(value.Type)) { LyricBeatCommandJson.Write(writer, value, options); return; }
        if (RubySegmentCommandJson.IsType(value.Type)) { RubySegmentCommandJson.Write(writer, value, options); return; }
        if (value is SetOrchestrationClipMembersCommandDto or SplitOrchestrationClipCommandDto or MergeOrchestrationClipsCommandDto)
        { ClipMembersCommandJson.Write(writer, value, options); return; }
        if (value is ReplaceProjectAudioAssetCommandDto or ClearProjectAudioCommandDto or RenameProjectCommandDto)
        { BasicMediaCommandJson.Write(writer, value, options); return; }
        if (value is SetOrchestrationLayoutKindCommandDto or SetLineEnterAnimationCommandDto or SetProjectEnterAnimationCommandDto)
        { OrchestrationEditingCompletionJson.Write(writer, value, options); return; }
        if (LeadInEditingCommandJson.IsType(value.Type))
        { LeadInEditingCommandJson.Write(writer, value, options); return; }
        if (value is CreateOrchestrationNodeCommandDto or CopyOrchestrationSubtreeCommandDto)
        { NodeCreationCommandJson.Write(writer, value, options); return; }
        if (value is SetOrchestrationNodeGeometryCommandDto or SetOrchestrationLayoutSettingsCommandDto
            or SetOrchestrationSlotAnimationCommandDto or SetOrchestrationProgramAnimationPolicyCommandDto)
        { OrchestrationPropertiesCommandJson.Write(writer, value, options); return; }
        if (value is SetLineLocalTransformCommandDto local)
        { LineLocalTransformCommandJson.Write(writer, local); return; }
        if (value is CreateOrchestrationCountdownCommandDto or CreateOrchestrationLeadInCommandDto or UpdateOrchestrationLeadInCommandDto or DeleteOrchestrationLeadInCommandDto)
        { LeadInCommandJson.Write(writer, value, options); return; }
        if (value is PatchStylesCommandDto or RestoreStylesCommandDto)
        { StyleCommandJson.Write(writer, value, options); return; }
        if (value is ImportAssetCommandDto or AppendTextCommandDto)
        { ImportCommandJson.Write(writer, value, options); return; }
        writer.WriteStartObject();
        WriteField("type", value.Type);
        bool ownTarget = value is StampTokenStartCommandDto or StampTokenEndCommandDto or ClearTokenTimeCommandDto
            or NudgeTokenTimeCommandDto or ShiftLineCommandDto or SetLineTextCommandDto or SetRubyTextCommandDto
            or DuplicateLineCommandDto or RemoveLineCommandDto or SplitTokenCommandDto or MergeTokenCommandDto
            or ReorderTokenCommandDto or ResplitLineCommandDto || value.GetType() == typeof(ProjectCommandDto);
        if (value is SetTokenTimesCommandDto batch)
            WriteField("changes", batch.Changes);
        else
        {
            if (ownTarget) WriteField("target", value.Target);
            switch (value)
            {
                case StampTokenStartCommandDto start: WriteField("playheadUs", start.PlayheadUs); break;
                case StampTokenEndCommandDto end: WriteField("playheadUs", end.PlayheadUs); break;
                case NudgeTokenTimeCommandDto nudge: WriteField("frameDelta", nudge.FrameDelta); break;
                case ShiftLineCommandDto shift: WriteField("deltaUs", shift.DeltaUs); break;
                case ClearTokenTimeCommandDto or DuplicateLineCommandDto or RemoveLineCommandDto or SplitTokenCommandDto or MergeTokenCommandDto: break;
                case ReorderTokenCommandDto reorder:
                    WriteField("destinationIndex", reorder.DestinationIndex); WriteField("reassignTimeSlots", reorder.ReassignTimeSlots); break;
                case ResplitLineCommandDto split:
                    WriteField("segments", split.Segments); WriteField("estimateTimes", split.EstimateTimes); break;
                case ResplitBranchCommandDto resplit:
                    WriteField("branchId", resplit.BranchId);
                    WriteField("rule", resplit.Rule is { } rule ? JsonNamingPolicy.CamelCase.ConvertName(rule.ToString()) : null);
                    WriteField("estimateTimes", resplit.EstimateTimes); break;
                case SetLineTextCommandDto text: WriteField("text", text.Text); break;
                case SetRubyTextCommandDto ruby: WriteField("text", ruby.Text); break;
                case AddLineCommandDto add: WriteField("branchId", add.BranchId); WriteField("afterLineId", add.AfterLineId); break;
                case SetCanvasSizeCommandDto canvas: WriteField("width", canvas.Width); WriteField("height", canvas.Height); break;
                case SetFrameRateCommandDto rate: WriteField("numerator", rate.Numerator); WriteField("denominator", rate.Denominator); break;
                case SetBackgroundColorCommandDto color:
                    WriteField("red", color.Red); WriteField("green", color.Green); WriteField("blue", color.Blue); WriteField("alpha", color.Alpha); break;
                case SetOutputDurationCommandDto duration:
                    // A null duration is an explicit instruction, even when ordinary null response fields are omitted.
                    writer.WritePropertyName("durationUs");
                    if (duration.DurationUs is null) writer.WriteNullValue(); else writer.WriteStringValue(duration.DurationUs);
                    break;
                case SetOrchestrationDefaultContainerCommandDto orchestration:
                    WriteField("branchId", orchestration.BranchId); WriteField("containerId", orchestration.ContainerId); break;
                case SetOrchestrationLayoutDefaultPlacementCommandDto layoutPlacement:
                    WriteField("containerId", layoutPlacement.ContainerId);
                    writer.WritePropertyName("placement"); JsonSerializer.Serialize(writer, layoutPlacement.Placement, options); break;
                case SetOrchestrationNodeCommandDto node:
                    WriteField("nodeId", node.NodeId); writer.WritePropertyName("name");
                    if (node.Name is null) writer.WriteNullValue(); else writer.WriteStringValue(node.Name); break;
                case ReparentOrchestrationNodeCommandDto reparent:
                    WriteField("nodeId", reparent.NodeId); writer.WritePropertyName("parentId");
                    if (reparent.ParentId is null) writer.WriteNullValue(); else writer.WriteStringValue(reparent.ParentId); break;
                case ReorderOrchestrationChildrenCommandDto children:
                    writer.WritePropertyName("parentId"); if (children.ParentId is null) writer.WriteNullValue(); else writer.WriteStringValue(children.ParentId);
                    WriteField("orderedIds", children.OrderedIds); break;
                case AddOrchestrationSlotCommandDto slot:
                    WriteField("containerId", slot.ContainerId); WriteField("slotId", slot.SlotId); writer.WritePropertyName("name");
                    if (slot.Name is null) writer.WriteNullValue(); else writer.WriteStringValue(slot.Name); break;
                case RemoveOrchestrationNodeCommandDto removeNode: WriteField("nodeId", removeNode.NodeId); break;
                case ReorderOrchestrationSlotsCommandDto slots:
                    WriteField("containerId", slots.ContainerId); WriteField("orderedIds", slots.OrderedIds); break;
                case CreateOrchestrationClipCommandDto create:
                    WriteField("branchId", create.BranchId); WriteField("clipId", create.ClipId); WriteField("name", create.Name);
                    WriteField("lineIds", create.LineIds); WriteField("containerId", create.ContainerId); break;
                case RenameOrchestrationClipCommandDto rename:
                    WriteField("branchId", rename.BranchId); WriteField("clipId", rename.ClipId);
                    writer.WritePropertyName("name"); if (rename.Name is null) writer.WriteNullValue(); else writer.WriteStringValue(rename.Name); break;
                case SetOrchestrationClipContainerCommandDto move:
                    WriteField("branchId", move.BranchId); WriteField("clipId", move.ClipId); WriteField("containerId", move.ContainerId); break;
                case DeleteOrchestrationClipCommandDto delete:
                    WriteField("branchId", delete.BranchId); WriteField("clipId", delete.ClipId); break;
                case BasicLayoutCommandDto layout:
                    WriteField("branchId", layout.BranchId); WriteField("preset", layout.Preset); WriteField("selectedLineIds", layout.SelectedLineIds);
                    WriteField("layoutName", layout.LayoutName); WriteField("slotNames", layout.SlotNames);
                    WriteField("selectedSegmentName", layout.SelectedSegmentName); WriteField("retainedSegmentName", layout.RetainedSegmentName); break;
                case ApplyPresetApplicationCommandDto apply:
                    WriteField("templateLibraryId", apply.TemplateLibraryId); WriteField("templateId", apply.TemplateId); WriteField("templateRevision", apply.TemplateRevision); WriteField("targets", apply.Targets); WriteField("branchInputs", apply.BranchInputs); break;
                case UpdatePresetApplicationCommandDto update:
                    WriteField("applicationId", update.ApplicationId); WriteField("templateLibraryId", update.TemplateLibraryId); WriteField("templateId", update.TemplateId); WriteField("templateRevision", update.TemplateRevision); WriteField("branchInputs", update.BranchInputs); break;
                case SetPresetApplicationEnabledCommandDto enabled:
                    WriteField("applicationId", enabled.ApplicationId); WriteField("enabled", enabled.Enabled); break;
                case RemovePresetApplicationCommandDto remove:
                    WriteField("applicationIds", remove.ApplicationIds); break;
                case AddTranslationBranchCommandDto branch: WriteField("name", branch.Name); WriteField("language", branch.Language); break;
                case ConfigureBranchCommandDto configure:
                    WriteField("branchId", configure.BranchId); WriteField("name", configure.Name);
                    writer.WritePropertyName("language"); JsonSerializer.Serialize(writer, configure.Language, options);
                    WriteField("role", configure.Role); WriteField("timingMode", configure.TimingMode); break;
                case RemoveBranchCommandDto remove: WriteField("branchId", remove.BranchId); break;
                case LinkPrimaryLineCommandDto link:
                    WriteField("branchId", link.BranchId); WriteField("lineId", link.LineId);
                    writer.WritePropertyName("primaryLineId"); JsonSerializer.Serialize(writer, link.PrimaryLineId, options); break;
                case UseLyricsEndCommandDto or UseAudioDurationCommandDto: break;
                default:
                    WriteField("startUs", value.StartUs);
                    WriteField("endUs", value.EndUs);
                    WriteField("analysisJobId", value.AnalysisJobId);
                    break;
            }
        }
        // Preserve malformed in-process payloads in receipt identity; HTTP rejects these foreign fields.
        if (value.GetType() != typeof(ProjectCommandDto))
        {
            if (!ownTarget && value.Target is not null) WriteField("target", value.Target);
            if (value.StartUs is not null) WriteField("startUs", value.StartUs);
            if (value.EndUs is not null) WriteField("endUs", value.EndUs);
            if (value.AnalysisJobId is not null) WriteField("analysisJobId", value.AnalysisJobId);
        }
        writer.WriteEndObject();

        void WriteField<T>(string name, T field)
        {
            if (field is null && options.DefaultIgnoreCondition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault)
                return;
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, field, options);
        }
    }
}
