using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class LyricBeatCommandJson
{
    internal static bool IsType(string? type) => type is "lyrics.strikeBeat" or "lyrics.closeBeat"
        or "lyrics.togglePhraseEndHint" or "lyrics.splitLine" or "lyrics.mergeLines"
        or "lyrics.setRubySegments" or "lyrics.applyTextDiff"
        or "lyrics.clearLineTiming" or "lyrics.clearBranchTiming";

    internal static ProjectCommandDto Read(JsonElement root, string type)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("命令必须是对象。");

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty item in root.EnumerateObject())
        {
            if (!fields.TryAdd(item.Name, item.Value))
                throw new JsonException("歌词编辑命令包含重复字段。");
        }

        string[] allowed = type switch
        {
            "lyrics.strikeBeat" => ["type", "branchId", "closeTarget", "openTarget", "playheadUs", "closeGuardGapUs", "fallbackDurationUs"],
            "lyrics.closeBeat" => ["type", "target", "playheadUs"],
            "lyrics.togglePhraseEndHint" => ["type", "target"],
            "lyrics.splitLine" => ["type", "target", "tokenIndex"],
            "lyrics.mergeLines" => ["type", "target"],
            "lyrics.setRubySegments" => ["type", "target", "segments"],
            "lyrics.applyTextDiff" => ["type", "branchId", "lines"],
            "lyrics.clearLineTiming" => ["type", "target", "keepLineTime"],
            "lyrics.clearBranchTiming" => ["type", "branchId"],
            _ => throw new JsonException("未知歌词命令。"),
        };

        string[] required = type switch
        {
            "lyrics.strikeBeat" => ["type", "branchId", "openTarget", "playheadUs"],
            "lyrics.closeBeat" => ["type", "target", "playheadUs"],
            "lyrics.togglePhraseEndHint" => ["type", "target"],
            "lyrics.splitLine" => ["type", "target", "tokenIndex"],
            "lyrics.mergeLines" => ["type", "target"],
            "lyrics.setRubySegments" => ["type", "target", "segments"],
            "lyrics.applyTextDiff" => ["type", "branchId", "lines"],
            "lyrics.clearLineTiming" => ["type", "target"],
            "lyrics.clearBranchTiming" => ["type", "branchId"],
            _ => throw new JsonException("未知歌词命令。"),
        };

        foreach (string key in fields.Keys)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
                throw new JsonException("歌词编辑命令包含未知字段。");
        }
        foreach (string key in required)
        {
            if (!fields.ContainsKey(key))
                throw new JsonException("歌词编辑命令缺少必填字段。");
        }

        return type switch
        {
            "lyrics.strikeBeat" => new StrikeBeatCommandDto(
                ReadStableId(fields["branchId"]),
                fields.TryGetValue("closeTarget", out JsonElement closeEl) && closeEl.ValueKind != JsonValueKind.Null
                    ? ReadBeatTarget(closeEl) : null,
                ReadBeatTarget(fields["openTarget"]),
                ReadTime(fields["playheadUs"]),
                fields.TryGetValue("closeGuardGapUs", out JsonElement guardEl) && guardEl.ValueKind != JsonValueKind.Null
                    ? ReadTime(guardEl) : null,
                fields.TryGetValue("fallbackDurationUs", out JsonElement fallbackEl) && fallbackEl.ValueKind != JsonValueKind.Null
                    ? ReadTime(fallbackEl) : null),

            "lyrics.closeBeat" => new CloseBeatCommandDto(
                ReadBeatTarget(fields["target"]),
                ReadTime(fields["playheadUs"])),

            "lyrics.togglePhraseEndHint" => new TogglePhraseEndHintCommandDto(
                ReadTimingTarget(fields["target"], tokenRequired: true)),

            "lyrics.splitLine" => new SplitLineCommandDto(
                ReadTimingTarget(fields["target"], tokenRequired: false),
                ReadInt32(fields["tokenIndex"])),

            "lyrics.mergeLines" => new MergeLinesCommandDto(
                ReadTimingTarget(fields["target"], tokenRequired: false)),

            "lyrics.setRubySegments" => new SetRubySegmentsCommandDto(
                ReadTimingTarget(fields["target"], tokenRequired: true),
                ReadRubySegments(fields["segments"])),

            "lyrics.applyTextDiff" => new ApplyTextDiffCommandDto(
                ReadStableId(fields["branchId"]),
                ReadDiffLines(fields["lines"])),

            "lyrics.clearLineTiming" => new ClearLineTimingCommandDto(
                ReadTimingTarget(fields["target"], tokenRequired: false),
                fields.TryGetValue("keepLineTime", out JsonElement keepEl) && keepEl.ValueKind != JsonValueKind.Null
                    ? ReadBoolean(keepEl) : null),

            "lyrics.clearBranchTiming" => new ClearBranchTimingCommandDto(
                ReadStableId(fields["branchId"])),

            _ => throw new JsonException("未知歌词命令。"),
        };
    }

    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", command.Type);
        void Field<T>(string name, T value) { writer.WritePropertyName(name); JsonSerializer.Serialize(writer, value, options); }

        switch (command)
        {
            case StrikeBeatCommandDto strike:
                Field("branchId", strike.BranchId);
                if (strike.CloseTarget is not null) Field("closeTarget", strike.CloseTarget);
                Field("openTarget", strike.OpenTarget);
                Field("playheadUs", strike.PlayheadUs);
                if (strike.CloseGuardGapUs is not null) Field("closeGuardGapUs", strike.CloseGuardGapUs);
                if (strike.FallbackDurationUs is not null) Field("fallbackDurationUs", strike.FallbackDurationUs);
                if (command.Target is not null) Field("inheritedTarget", command.Target);
                break;

            case CloseBeatCommandDto close:
                Field("target", close.BeatTarget);
                Field("playheadUs", close.PlayheadUs);
                if (command.Target is not null) Field("inheritedTarget", command.Target);
                break;

            case TogglePhraseEndHintCommandDto toggle:
                Field("target", toggle.Target);
                break;

            case SplitLineCommandDto split:
                Field("target", split.Target);
                Field("tokenIndex", split.TokenIndex);
                break;

            case MergeLinesCommandDto merge:
                Field("target", merge.Target);
                break;

            case SetRubySegmentsCommandDto setRuby:
                Field("target", setRuby.Target);
                Field("segments", setRuby.Segments);
                break;

            case ApplyTextDiffCommandDto diff:
                Field("branchId", diff.BranchId);
                Field("lines", diff.Lines);
                if (command.Target is not null) Field("inheritedTarget", command.Target);
                break;

            case ClearLineTimingCommandDto clearLine:
                Field("target", clearLine.Target);
                if (clearLine.KeepLineTime is { } keepLineTime) Field("keepLineTime", keepLineTime);
                break;

            case ClearBranchTimingCommandDto clearBranch:
                Field("branchId", clearBranch.BranchId);
                if (command.Target is not null) Field("inheritedTarget", command.Target);
                break;
        }

        if (command.StartUs is not null) Field("inheritedStartUs", command.StartUs);
        if (command.EndUs is not null) Field("inheritedEndUs", command.EndUs);
        if (command.AnalysisJobId is not null) Field("analysisJobId", command.AnalysisJobId);
        writer.WriteEndObject();
    }

    private static LyricBeatTargetDto ReadBeatTarget(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("拍点目标必须是对象。");

        var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty p in element.EnumerateObject())
        {
            if (p.Name is not ("branchId" or "lineId" or "tokenId" or "rubyId" or "segmentId") || !props.TryAdd(p.Name, p.Value))
                throw new JsonException("拍点目标包含未知或重复字段。");
        }
        if (!props.TryGetValue("branchId", out JsonElement branchEl)
            || !props.TryGetValue("lineId", out JsonElement lineEl)
            || !props.TryGetValue("tokenId", out JsonElement tokenEl))
            throw new JsonException("拍点目标必须包含 branchId、lineId、tokenId。");

        string branchId = ReadStableId(branchEl);
        string lineId = ReadStableId(lineEl);
        string tokenId = ReadStableId(tokenEl);

        string? rubyId = props.TryGetValue("rubyId", out JsonElement rubyEl) && rubyEl.ValueKind != JsonValueKind.Null
            ? ReadStableId(rubyEl) : null;
        string? segmentId = props.TryGetValue("segmentId", out JsonElement segEl) && segEl.ValueKind != JsonValueKind.Null
            ? ReadStableId(segEl) : null;

        if ((rubyId is null) != (segmentId is null))
            throw new JsonException("拍点目标的 rubyId 与 segmentId 必须同时提供或同时省略。");

        return new LyricBeatTargetDto(branchId, lineId, tokenId, rubyId, segmentId);
    }

    private static LyricTimingTargetDto ReadTimingTarget(JsonElement element, bool tokenRequired)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("target 必须是对象。");

        var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty p in element.EnumerateObject())
        {
            bool allowed = tokenRequired
                ? p.Name is "branchId" or "lineId" or "tokenId"
                : p.Name is "branchId" or "lineId";
            if (!allowed || !props.TryAdd(p.Name, p.Value))
                throw new JsonException("target 包含未知或重复字段。");
        }
        if (!props.TryGetValue("branchId", out JsonElement branchEl)
            || !props.TryGetValue("lineId", out JsonElement lineEl)
            || (tokenRequired && !props.TryGetValue("tokenId", out JsonElement _)))
            throw new JsonException("target 缺少必填字段。");

        string? tokenId = null;
        if (tokenRequired && props.TryGetValue("tokenId", out JsonElement tokenEl))
            tokenId = ReadStableId(tokenEl);

        return new LyricTimingTargetDto(
            ReadStableId(branchEl),
            ReadStableId(lineEl),
            tokenId);
    }

    private static string[] ReadRubySegments(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new JsonException("segments 必须是字符串数组。");
        int count = element.GetArrayLength();
        if (count > LyricRefactorEditingLimits.MaxRubySegments)
            throw new ProjectCommandBudgetExceededException();
        var result = new string[count];
        int i = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new JsonException("segments 的每一项必须是非空值字符串。");
            result[i++] = item.GetString()!;
        }
        return result;
    }

    private static TextDiffLineDto[] ReadDiffLines(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new JsonException("lines 必须是对象数组。");
        int count = element.GetArrayLength();
        if (count > LyricRefactorEditingLimits.MaxDiffLines)
            throw new ProjectCommandBudgetExceededException();
        var result = new TextDiffLineDto[count];
        int i = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new JsonException("lines 的每一项必须是对象。");
            var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty p in item.EnumerateObject())
            {
                if (p.Name is not ("text" or "sourceLineId") || !props.TryAdd(p.Name, p.Value))
                    throw new JsonException("diff 行对象包含未知或重复字段。");
            }
            if (!props.TryGetValue("text", out JsonElement textEl) || textEl.ValueKind != JsonValueKind.String)
                throw new JsonException("diff 行对象必须包含字符串字段 text。");
            string? sourceLineId = props.TryGetValue("sourceLineId", out JsonElement srcEl) && srcEl.ValueKind != JsonValueKind.Null
                ? ReadStableId(srcEl) : null;
            result[i++] = new TextDiffLineDto(textEl.GetString()!, sourceLineId);
        }
        return result;
    }

    private static string ReadStableId(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 } id && !string.IsNullOrWhiteSpace(id)
            ? id
            : throw new JsonException("目标身份必须非空且不超过 128 字符。");

    private static string ReadTime(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("时间必须是十进制字符串。");

    private static int ReadInt32(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int num)
            ? num
            : throw new JsonException("必须是 32 位整数。");

    private static bool ReadBoolean(JsonElement value) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new JsonException("必须是布尔值。");
}
