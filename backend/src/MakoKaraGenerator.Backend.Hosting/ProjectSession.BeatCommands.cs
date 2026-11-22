using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal static bool IsLyricRefactorCommand(ProjectCommandDto command) => command.Type is "lyrics.strikeBeat"
        or "lyrics.closeBeat" or "lyrics.togglePhraseEndHint" or "lyrics.splitLine"
        or "lyrics.mergeLines" or "lyrics.setRubySegments" or "lyrics.applyTextDiff"
        or "lyrics.clearLineTiming" or "lyrics.clearBranchTiming";

    internal static BackendErrorResponse? CheckLyricRefactorIntent(ProjectCommandDto command)
    {
        if (!IsLyricRefactorCommand(command)) return null;
        BackendErrorResponse Invalid() => new(BackendErrorCode.InvalidRequest, "歌词连打或结构编辑命令的字段、目标或时间格式不合法。", "command");
        BackendErrorResponse TooLarge(string field = "command") => new(BackendErrorCode.ResourceTooLarge, "命令载荷超过单次操作的预算上限。", field);

        static bool ValidBeatTarget(LyricBeatTargetDto? target) => target is not null
            && IsStableId(target.BranchId) && IsStableId(target.LineId) && IsStableId(target.TokenId)
            && ((target.RubyId is null && target.SegmentId is null)
                || (IsStableId(target.RubyId) && IsStableId(target.SegmentId)));

        if (command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null)
            return Invalid();

        switch (command)
        {
            case StrikeBeatCommandDto strike when command.Type == "lyrics.strikeBeat":
                if (command.Target is not null || !IsStableId(strike.BranchId)
                    || !ValidBeatTarget(strike.OpenTarget) || strike.OpenTarget!.BranchId != strike.BranchId
                    || !TryParseMicroseconds(strike.PlayheadUs, out _))
                    return Invalid();
                if (strike.CloseTarget is not null)
                {
                    if (!ValidBeatTarget(strike.CloseTarget) || strike.CloseTarget.BranchId != strike.BranchId
                        || strike.CloseTarget == strike.OpenTarget)
                        return Invalid();
                }
                if (strike.CloseGuardGapUs is not null
                    && (!TryParseMicroseconds(strike.CloseGuardGapUs, out long guardGap) || guardGap < 0))
                    return Invalid();
                if (strike.FallbackDurationUs is not null
                    && (!TryParseMicroseconds(strike.FallbackDurationUs, out long fallbackDur) || fallbackDur <= 0))
                    return Invalid();
                return null;

            case CloseBeatCommandDto close when command.Type == "lyrics.closeBeat":
                if (command.Target is not null || !ValidBeatTarget(close.BeatTarget)
                    || !TryParseMicroseconds(close.PlayheadUs, out _))
                    return Invalid();
                return null;

            case TogglePhraseEndHintCommandDto when command.Type == "lyrics.togglePhraseEndHint":
                return TryEditingTarget(command.Target, tokenRequired: true, out _) ? null : Invalid();

            case SplitLineCommandDto split when command.Type == "lyrics.splitLine":
                return TryEditingTarget(command.Target, tokenRequired: false, out _) && split.TokenIndex is >= 1 ? null : Invalid();

            case MergeLinesCommandDto when command.Type == "lyrics.mergeLines":
                return TryEditingTarget(command.Target, tokenRequired: false, out _) ? null : Invalid();

            case SetRubySegmentsCommandDto setRuby when command.Type == "lyrics.setRubySegments":
                if (!TryEditingTarget(command.Target, tokenRequired: true, out _) || setRuby.Segments is null)
                    return Invalid();
                if (setRuby.Segments.Length > LyricRefactorEditingLimits.MaxRubySegments)
                    return TooLarge("command.segments");
                if (setRuby.Segments.Any(s => s is null))
                    return Invalid();
                try { ValidateIntentBytes(command, LyricRefactorEditingLimits.MaxIntentBytes); }
                catch (ReadModelBudgetExceededException) { return TooLarge(); }
                return null;

            case ApplyTextDiffCommandDto diff when command.Type == "lyrics.applyTextDiff":
                if (command.Target is not null || !IsStableId(diff.BranchId) || diff.Lines is null)
                    return Invalid();
                if (diff.Lines.Length > LyricRefactorEditingLimits.MaxDiffLines)
                    return TooLarge("command.lines");
                foreach (TextDiffLineDto? item in diff.Lines)
                {
                    if (item is null || item.Text is null || (item.SourceLineId is not null && !IsStableId(item.SourceLineId)))
                        return Invalid();
                }
                try { ValidateIntentBytes(command, LyricRefactorEditingLimits.MaxIntentBytes); }
                catch (ReadModelBudgetExceededException) { return TooLarge(); }
                return null;

            case ClearLineTimingCommandDto when command.Type == "lyrics.clearLineTiming":
                return TryEditingTarget(command.Target, tokenRequired: false, out _) ? null : Invalid();

            case ClearBranchTimingCommandDto clearBranch when command.Type == "lyrics.clearBranchTiming":
                return command.Target is null && IsStableId(clearBranch.BranchId) ? null : Invalid();

            default:
                return Invalid();
        }
    }

    private static ProjectCommandDto CanonicalLyricRefactorCommand(ProjectCommandDto command)
    {
        static string? CanonicalTime(string? value) => TryParseMicroseconds(value, out long parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture) : value;

        return command switch
        {
            StrikeBeatCommandDto strike => strike with
            {
                PlayheadUs = CanonicalTime(strike.PlayheadUs),
                CloseGuardGapUs = CanonicalTime(strike.CloseGuardGapUs),
                FallbackDurationUs = CanonicalTime(strike.FallbackDurationUs),
            },
            CloseBeatCommandDto close => close with { PlayheadUs = CanonicalTime(close.PlayheadUs) },
            SetRubySegmentsCommandDto setRuby => setRuby with { Segments = setRuby.Segments?.ToArray() },
            ApplyTextDiffCommandDto diff => diff with { Lines = diff.Lines?.ToArray() },
            _ => command,
        };
    }

    private bool TryExecuteLyricRefactorCommand(
        Project before,
        ProjectCommandDto command,
        out Project after,
        out ProjectChangeSetDto? changeSet,
        out LyricTimingCommandResultDto? timingResult,
        out LyricContentCommandFeedbackDto? feedback,
        out RubySegmentCommandResultDto? rubyResult,
        out BackendErrorResponse? error)
    {
        after = before;
        changeSet = null;
        timingResult = null;
        feedback = null;
        rubyResult = null;
        error = null;

        if (!IsLyricRefactorCommand(command)) return false;
        if (CheckLyricRefactorIntent(command) is { } invalid) { error = invalid; return true; }

        switch (command)
        {
            case StrikeBeatCommandDto strike:
            {
                long playheadUs = long.Parse(strike.PlayheadUs!, CultureInfo.InvariantCulture);
                long guardGapUs = strike.CloseGuardGapUs is not null
                    ? long.Parse(strike.CloseGuardGapUs, CultureInfo.InvariantCulture)
                    : LyricTimingEditor.DefaultCloseGuardGapUs;
                long fallbackUs = strike.FallbackDurationUs is not null
                    ? long.Parse(strike.FallbackDurationUs, CultureInfo.InvariantCulture)
                    : LyricTimingEditor.DefaultCloseFallbackDurationUs;

                LyricBeatTarget openTarget = ToCoreBeatTarget(strike.OpenTarget!);
                LyricBeatTarget? closeTarget = strike.CloseTarget is not null ? ToCoreBeatTarget(strike.CloseTarget) : null;

                Workspace.History.BreakMergeGroup();
                LyricTimingStampResult result;
                try
                {
                    result = LyricTimingEditUseCase.StrikeBeat(
                        Workspace, closeTarget, openTarget, playheadUs, AudioDurationUs, guardGapUs, fallbackUs);
                }
                finally { Workspace.History.BreakMergeGroup(); }

                if (!result.Accepted)
                {
                    error = MapTimingError(result.Error) with
                    {
                        Field = result.Error?.Code is LyricTimingEditErrorCode.TargetStale ? "command.openTarget" : "command.playheadUs",
                        FailureCode = result.Error?.Code.ToString(),
                    };
                    return true;
                }

                after = result.Project;
                timingResult = new LyricTimingCommandResultDto(result.EstimatedPlaceholder, result.CloseGuardApplied);
                if (result.Changed)
                {
                    bool hasRuby = openTarget.IsRubySegment || (closeTarget?.IsRubySegment ?? false);
                    string[] areas = hasRuby
                        ? ["lyricsTiming", "lyricsRubyTiming", "timeline", "renderInputs"]
                        : ["lyricsTiming", "timeline", "renderInputs"];
                    string[] lineIds = closeTarget is not null && closeTarget.LineId != openTarget.LineId
                        ? [closeTarget.LineId, openTarget.LineId]
                        : [openTarget.LineId];
                    string[] tokenIds = closeTarget is not null && closeTarget.TokenId != openTarget.TokenId
                        ? [closeTarget.TokenId, openTarget.TokenId]
                        : [openTarget.TokenId];
                    changeSet = new ProjectChangeSetDto(areas, [strike.BranchId!], lineIds, tokenIds, false);
                }
                else
                {
                    changeSet = new ProjectChangeSetDto([], [], [], [], false);
                }
                return true;
            }

            case CloseBeatCommandDto close:
            {
                long playheadUs = long.Parse(close.PlayheadUs!, CultureInfo.InvariantCulture);
                LyricBeatTarget target = ToCoreBeatTarget(close.BeatTarget!);

                Workspace.History.BreakMergeGroup();
                LyricTimingStampResult result;
                try
                {
                    result = LyricTimingEditUseCase.CloseBeat(Workspace, target, playheadUs, AudioDurationUs);
                }
                finally { Workspace.History.BreakMergeGroup(); }

                if (!result.Accepted)
                {
                    error = MapTimingError(result.Error) with
                    {
                        Field = result.Error?.Code is LyricTimingEditErrorCode.Untimed or LyricTimingEditErrorCode.TargetStale
                            ? "command.target" : "command.playheadUs",
                        FailureCode = result.Error?.Code.ToString(),
                    };
                    return true;
                }

                after = result.Project;
                timingResult = new LyricTimingCommandResultDto(false);
                if (result.Changed)
                {
                    string[] areas = target.IsRubySegment
                        ? ["lyricsTiming", "lyricsRubyTiming", "timeline", "renderInputs"]
                        : ["lyricsTiming", "timeline", "renderInputs"];
                    changeSet = new ProjectChangeSetDto(areas, [target.BranchId], [target.LineId], [target.TokenId], false);
                }
                else
                {
                    changeSet = new ProjectChangeSetDto([], [], [], [], false);
                }
                return true;
            }

            case TogglePhraseEndHintCommandDto toggle:
            {
                var target = new LyricTimingEditTarget(toggle.Target!.BranchId, toggle.Target.LineId, toggle.Target.TokenId);
                Workspace.History.BreakMergeGroup();
                LyricTimingEditResult result;
                try { result = LyricTimingEditUseCase.TogglePhraseEndHint(Workspace, target); }
                finally { Workspace.History.BreakMergeGroup(); }

                if (!result.Accepted)
                {
                    error = MapTimingError(result.Error) with
                    {
                        Field = "command.target",
                        FailureCode = result.Error?.Code.ToString(),
                    };
                    return true;
                }

                after = result.Project;
                changeSet = result.Changed
                    ? new ProjectChangeSetDto(["lyricsContent", "timeline", "renderInputs"],
                        [target.BranchId], [target.LineId], [target.TokenId!], false)
                    : new ProjectChangeSetDto([], [], [], [], false);
                return true;
            }

            case SplitLineCommandDto split:
            {
                string branchId = split.Target!.BranchId;
                string lineId = split.Target.LineId;
                LyricLine? line = FindContentLine(before, branchId, lineId);
                if (line is null) { error = ContentRejected(command, "TargetStale") with { Field = "command.target" }; return true; }

                var baseline = new LyricContentEditBaseline(Workspace, Workspace.Generation, new(branchId, lineId), line);
                PreparedLyricContentEdit prepared = LyricContentEditUseCase.PrepareSplitLine(Workspace, baseline, split.TokenIndex!.Value);
                if (!prepared.Result.Accepted)
                {
                    error = ContentRejected(command, prepared.Result.Error?.Code.ToString(), prepared.Result.Error?.Detail) with
                    {
                        Field = prepared.Result.Error?.Code == LyricContentEditErrorCode.InvalidTarget ? "command.tokenIndex" : "command.target",
                    };
                    return true;
                }

                Workspace.History.BreakMergeGroup();
                LyricContentEditResult result;
                try { result = LyricContentEditUseCase.CommitPrepared(Workspace, prepared); }
                finally { Workspace.History.BreakMergeGroup(); }

                if (!result.Accepted)
                {
                    error = ContentRejected(command, result.Error?.Code.ToString(), result.Error?.Detail);
                    return true;
                }

                after = result.Project;
                changeSet = result.Changed
                    ? new ProjectChangeSetDto(["lyricsContent", "lyricsStructure", "timeline", "renderInputs"],
                        [branchId], result.CreatedLineId is not null ? [lineId, result.CreatedLineId] : [lineId], [], true)
                    : new ProjectChangeSetDto([], [], [], [], false);
                feedback = new LyricContentCommandFeedbackDto(
                    Selection: new(branchId, result.SelectedLineId is not null, result.SelectedLineId),
                    CreatedLineId: result.CreatedLineId,
                    InsertedIndex: result.InsertedIndex);
                return true;
            }

            case MergeLinesCommandDto merge:
            {
                string branchId = merge.Target!.BranchId;
                string lineId = merge.Target.LineId;
                LyricLine? line = FindContentLine(before, branchId, lineId);
                if (line is null) { error = ContentRejected(command, "TargetStale") with { Field = "command.target" }; return true; }

                var baseline = new LyricContentEditBaseline(Workspace, Workspace.Generation, new(branchId, lineId), line);
                PreparedLyricContentEdit prepared = LyricContentEditUseCase.PrepareMergeLines(Workspace, baseline);
                if (!prepared.Result.Accepted)
                {
                    error = ContentRejected(command, prepared.Result.Error?.Code.ToString(), prepared.Result.Error?.Detail) with
                    {
                        Field = "command.target",
                    };
                    return true;
                }
                if (prepared.Result.Changed && prepared.Result.SelectedLineId is { } mergedLineId)
                {
                    try { ValidateContentLineBudget(prepared.Result.Project, branchId, mergedLineId); }
                    catch (ReadModelBudgetExceededException) { error = ContentBudgetExceeded(); return true; }
                }

                Workspace.History.BreakMergeGroup();
                LyricContentEditResult result;
                try { result = LyricContentEditUseCase.CommitPrepared(Workspace, prepared); }
                finally { Workspace.History.BreakMergeGroup(); }

                if (!result.Accepted)
                {
                    error = ContentRejected(command, result.Error?.Code.ToString(), result.Error?.Detail);
                    return true;
                }

                after = result.Project;
                changeSet = result.Changed
                    ? new ProjectChangeSetDto(["lyricsContent", "lyricsStructure", "timeline", "renderInputs"],
                        [branchId], result.SelectedLineId is not null ? [result.SelectedLineId] : [], [], true)
                    : new ProjectChangeSetDto([], [], [], [], false);
                feedback = new LyricContentCommandFeedbackDto(
                    Selection: new(branchId, result.SelectedLineId is not null, result.SelectedLineId),
                    DeletedLineId: lineId);
                return true;
            }

            case SetRubySegmentsCommandDto setRuby:
            {
                string branchId = setRuby.Target!.BranchId;
                string lineId = setRuby.Target.LineId;
                string tokenId = setRuby.Target.TokenId!;
                try
                {
                    ReadModelBudget identifiers = new();
                    PreparedRubySegmentEdit prepared = RubySegmentEditUseCase.PrepareSetSegments(
                        Workspace, branchId, lineId, tokenId, [.. setRuby.Segments!],
                        id => { identifiers.Item(); identifiers.Text(id); });
                    RubySegmentEditResult preview = prepared.Result;
                    if (!preview.Accepted)
                    {
                        string field = preview.Error == RubySegmentEditErrorCode.EmptyText ? "command.segments" : "command.target";
                        error = new BackendErrorResponse(BackendErrorCode.CommandRejected,
                            preview.Detail ?? "注音分段设置命令被共享领域规则拒绝。", field, FailureCode: preview.Error?.ToString());
                        return true;
                    }

                    if (preview.Changed)
                        ValidateContentLineBudget(preview.Project, branchId, lineId);

                    Workspace.History.BreakMergeGroup();
                    RubySegmentEditResult committed;
                    try { committed = RubySegmentEditUseCase.Commit(Workspace, prepared); }
                    finally { Workspace.History.BreakMergeGroup(); }

                    if (!committed.Accepted)
                    {
                        error = new BackendErrorResponse(BackendErrorCode.CommandRejected,
                            committed.Detail ?? "注音分段设置提交被拒绝。", "command.target", FailureCode: committed.Error?.ToString());
                        return true;
                    }

                    after = committed.Project;
                    RubyAnnotation? updatedRuby = after.Lyrics.Branches.First(b => b.Id == branchId)
                        .Lines.First(l => l.Id == lineId)
                        .RubyAnnotations.FirstOrDefault(r => r.BaseTokenId == tokenId);
                    RubyGroupTargetDto? groupTarget = updatedRuby is not null
                        ? new RubyGroupTargetDto(branchId, lineId, tokenId, updatedRuby.Id) : null;

                    changeSet = committed.Changed
                        ? new ProjectChangeSetDto(["lyricsRubyContent", "lyricsStructure", "timeline", "renderInputs"],
                            [branchId], [lineId], [tokenId], true)
                        : new ProjectChangeSetDto([], [], [], [], false);
                    rubyResult = new RubySegmentCommandResultDto(
                        committed.ChangedSegmentCount,
                        groupTarget,
                        committed.SelectedSegmentId,
                        committed.CreatedSegmentId,
                        SelectionChanged: committed.Changed);
                    return true;
                }
                catch (ReadModelBudgetExceededException)
                {
                    error = ContentBudgetExceeded();
                    return true;
                }
            }

            case ApplyTextDiffCommandDto diff:
            {
                string branchId = diff.BranchId!;
                try
                {
                    ReadModelBudget items = new();
                    var inputs = diff.Lines!.Select(l => new LyricTextDiffLineInput(l.Text, l.SourceLineId)).ToArray();
                    PreparedLyricContentEdit prepared = LyricContentEditUseCase.PrepareApplyTextDiff(
                        Workspace, branchId, inputs, () => items.Item());
                    LyricContentEditResult preview = prepared.Result;
                    if (!preview.Accepted)
                    {
                        error = ContentRejected(command, preview.Error?.Code.ToString(), preview.Error?.Detail) with
                        {
                            Field = preview.Error?.Code == LyricContentEditErrorCode.TargetStale ? "command.branchId" : "command.lines",
                        };
                        return true;
                    }

                    if (preview.Changed)
                    {
                        LyricBranch nextBranch = preview.Project.Lyrics.Branches.First(b => b.Id == branchId);
                        LyricBranch oldBranch = before.Lyrics.Branches.First(b => b.Id == branchId);
                        var oldMap = oldBranch.Lines.ToDictionary(l => l.Id, StringComparer.Ordinal);
                        long totalBytes = 0;
                        for (int i = 0; i < nextBranch.Lines.Length; i++)
                        {
                            LyricLine nextLine = nextBranch.Lines[i];
                            if (oldMap.TryGetValue(nextLine.Id, out LyricLine? oldLine) && oldLine == nextLine) continue;
                            long lineBytes = ValidatePreparedTokenLineBudget(nextBranch, i, nextLine);
                            if (lineBytes > LyricRefactorEditingLimits.MaxPreparedBytes - totalBytes)
                                throw new ReadModelBudgetExceededException();
                            totalBytes += lineBytes;
                        }
                    }

                    Workspace.History.BreakMergeGroup();
                    LyricContentEditResult committed;
                    try { committed = LyricContentEditUseCase.CommitPrepared(Workspace, prepared); }
                    finally { Workspace.History.BreakMergeGroup(); }

                    if (!committed.Accepted)
                    {
                        error = ContentRejected(command, committed.Error?.Code.ToString(), committed.Error?.Detail);
                        return true;
                    }

                    after = committed.Project;
                    changeSet = committed.Changed
                        ? new ProjectChangeSetDto(["lyricsContent", "lyricsStructure", "timeline", "renderInputs"],
                            [branchId], [], [], true)
                        : new ProjectChangeSetDto([], [], [], [], false);
                    feedback = new LyricContentCommandFeedbackDto(
                        Selection: committed.Changed ? new(branchId, committed.SelectedLineId is not null, committed.SelectedLineId) : null,
                        PreservedTokenCount: committed.PreservedCount,
                        ResetTokenCount: committed.ResetCount);
                    return true;
                }
                catch (ReadModelBudgetExceededException)
                {
                    error = ContentBudgetExceeded();
                    return true;
                }
            }

            case ClearLineTimingCommandDto clearLine:
            {
                var target = new LyricTimingEditTarget(clearLine.Target!.BranchId, clearLine.Target.LineId);
                bool keepLineTime = clearLine.KeepLineTime == true;
                Workspace.History.BreakMergeGroup();
                LyricTimingEditResult result;
                try { result = LyricTimingEditUseCase.ClearLineTiming(Workspace, target, keepLineTime); }
                finally { Workspace.History.BreakMergeGroup(); }

                if (!result.Accepted)
                {
                    error = MapTimingError(result.Error) with
                    {
                        Field = "command.target",
                        FailureCode = result.Error?.Code.ToString(),
                    };
                    return true;
                }

                after = result.Project;
                if (result.Changed)
                {
                    bool presentationChanged = !Equals(before.LyricPresentation, after.LyricPresentation);
                    string[] areas = presentationChanged
                        ? ["lyricsTiming", "lyricsRubyTiming", "orchestration", "timeline", "renderInputs"]
                        : ["lyricsTiming", "lyricsRubyTiming", "timeline", "renderInputs"];
                    changeSet = new ProjectChangeSetDto(areas, [target.BranchId], [target.LineId], [], presentationChanged);
                }
                else
                {
                    changeSet = new ProjectChangeSetDto([], [], [], [], false);
                }
                return true;
            }

            case ClearBranchTimingCommandDto clearBranch:
            {
                string branchId = clearBranch.BranchId!;
                Workspace.History.BreakMergeGroup();
                LyricTimingEditResult result;
                try { result = LyricTimingEditUseCase.ClearBranchTiming(Workspace, branchId); }
                finally { Workspace.History.BreakMergeGroup(); }

                if (!result.Accepted)
                {
                    error = MapTimingError(result.Error) with
                    {
                        Field = "command.branchId",
                        FailureCode = result.Error?.Code.ToString(),
                    };
                    return true;
                }

                after = result.Project;
                if (result.Changed)
                {
                    bool presentationChanged = !Equals(before.LyricPresentation, after.LyricPresentation);
                    string[] areas = presentationChanged
                        ? ["lyricsTiming", "lyricsRubyTiming", "orchestration", "timeline", "renderInputs"]
                        : ["lyricsTiming", "lyricsRubyTiming", "timeline", "renderInputs"];
                    changeSet = new ProjectChangeSetDto(areas, [branchId], [], [], true);
                }
                else
                {
                    changeSet = new ProjectChangeSetDto([], [], [], [], false);
                }
                return true;
            }

            default:
                return false;
        }
    }

    private static LyricBeatTarget ToCoreBeatTarget(LyricBeatTargetDto dto) =>
        new(dto.BranchId, dto.LineId, dto.TokenId, dto.RubyId, dto.SegmentId);

    private static void ValidateIntentBytes(ProjectCommandDto command, int maxBytes)
    {
        using var counter = new BoundedIntentStream(maxBytes);
        JsonSerializer.Serialize(counter, command, ReadModelJson);
    }

    private sealed class BoundedIntentStream(int limit) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - _written) throw new ReadModelBudgetExceededException();
            _written += buffer.Length;
        }
    }
}
