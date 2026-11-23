using System.Globalization;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private static bool EditingCommandTypeMatches(ProjectCommandDto command) => command switch
    {
        SetBackgroundAssetCommandDto => command.Type == "composition.setBackgroundAsset",
        ClearBackgroundCommandDto => command.Type == "composition.clearBackground",
        SetBackgroundFitCommandDto => command.Type == "composition.setBackgroundFit",
        SetBackgroundPlaybackCommandDto => command.Type == "composition.setBackgroundPlayback",
        ReplaceProjectAudioAssetCommandDto => command.Type == "audio.replaceAsset",
        ClearProjectAudioCommandDto => command.Type == "audio.clear",
        RenameProjectCommandDto => command.Type == "project.rename",
        SetOrchestrationClipMembersCommandDto => command.Type == "orchestration.setClipMembers",
        SplitOrchestrationClipCommandDto => command.Type == "orchestration.splitClip",
        MergeOrchestrationClipsCommandDto => command.Type == "orchestration.mergeClips",
        SetOrchestrationLayoutKindCommandDto => command.Type == "orchestration.setLayoutKind",
        SetLeadInAttachmentCommandDto => command.Type == "orchestration.setLeadInAttachment",
        SetLeadInGenerationCommandDto => command.Type == "orchestration.setLeadInGeneration",
        PreviewLeadInGenerationCommandDto => command.Type == "orchestration.previewLeadInGeneration",
        ApplyLeadInGenerationCommandDto => command.Type == "orchestration.applyLeadInGeneration",
        DeleteLeadInAndExcludeCommandDto => command.Type == "orchestration.deleteLeadInAndExclude",
        AddOrchestrationMarkCommandDto => command.Type == "orchestration.addMark",
        SetOrchestrationMarkTextCommandDto => command.Type == "orchestration.setMarkText",
        ReorderOrchestrationMarksCommandDto => command.Type == "orchestration.reorderMarks",
        SpaceOrchestrationMarksCommandDto => command.Type == "orchestration.spaceMarks",
        SetCountdownAppearanceCommandDto => command.Type == "orchestration.setCountdownAppearance",
        SetCountdownPresentationCommandDto => command.Type == "orchestration.setCountdownPresentation",
        SetMarkAppearanceCommandDto => command.Type == "orchestration.setMarkAppearance",
        PatchStylesCommandDto => command.Type == "styles.patch",
        RestoreStylesCommandDto => command.Type == "styles.restore",
        SetRubySegmentTimeCommandDto => command.Type == "lyrics.setRubySegmentTime",
        ClearRubySegmentTimeCommandDto => command.Type == "lyrics.clearRubySegmentTime",
        SetRubySegmentTextCommandDto => command.Type == "lyrics.setRubySegmentText",
        AddRubySegmentCommandDto => command.Type == "lyrics.addRubySegment",
        RemoveRubySegmentCommandDto => command.Type == "lyrics.removeRubySegment",
        ReorderRubySegmentsCommandDto => command.Type == "lyrics.reorderRubySegments",
        SetRubySegmentTimesCommandDto => command.Type == "lyrics.setRubySegmentTimes",
        StrikeBeatCommandDto => command.Type == "lyrics.strikeBeat",
        CloseBeatCommandDto => command.Type == "lyrics.closeBeat",
        TogglePhraseEndHintCommandDto => command.Type == "lyrics.togglePhraseEndHint",
        SplitLineCommandDto => command.Type == "lyrics.splitLine",
        MergeLinesCommandDto => command.Type == "lyrics.mergeLines",
        SetRubySegmentsCommandDto => command.Type == "lyrics.setRubySegments",
        ApplyTextDiffCommandDto => command.Type == "lyrics.applyTextDiff",
        ClearLineTimingCommandDto => command.Type == "lyrics.clearLineTiming",
        ClearBranchTimingCommandDto => command.Type == "lyrics.clearBranchTiming",
        StampTokenStartCommandDto => command.Type == "lyrics.stampTokenStart",
        StampTokenEndCommandDto => command.Type == "lyrics.stampTokenEnd",
        ClearTokenTimeCommandDto => command.Type == "lyrics.clearTokenTime",
        NudgeTokenTimeCommandDto => command.Type == "lyrics.nudgeTokenTime",
        ShiftLineCommandDto => command.Type == "lyrics.shiftLine",
        SetTokenTimesCommandDto => command.Type == "lyrics.setTokenTimes",
        SplitTokenCommandDto => command.Type == "lyrics.splitToken",
        MergeTokenCommandDto => command.Type == "lyrics.mergeToken",
        ReorderTokenCommandDto => command.Type == "lyrics.reorderToken",
        ResplitLineCommandDto => command.Type == "lyrics.resplitLine",
        ResplitBranchCommandDto => command.Type == "lyrics.resplitBranch",
        SetLineTextCommandDto => command.Type == "lyrics.setLineText",
        SetRubyTextCommandDto => command.Type == "lyrics.setRubyText",
        AddLineCommandDto => command.Type == "lyrics.addLine",
        DuplicateLineCommandDto => command.Type == "lyrics.duplicateLine",
        RemoveLineCommandDto => command.Type == "lyrics.removeLine",
        SetCanvasSizeCommandDto => command.Type == "composition.setCanvasSize",
        SetFrameRateCommandDto => command.Type == "composition.setFrameRate",
        SetBackgroundColorCommandDto => command.Type == "composition.setBackgroundColor",
        SetOutputDurationCommandDto => command.Type == "composition.setOutputDuration",
        UseLyricsEndCommandDto => command.Type == "composition.useLyricsEnd",
        UseAudioDurationCommandDto => command.Type == "composition.useAudioDuration",
        AddTranslationBranchCommandDto or ConfigureBranchCommandDto or RemoveBranchCommandDto or LinkPrimaryLineCommandDto => BranchCommandTypeMatches(command),
        ApplyPresetApplicationCommandDto => command.Type == "presetApplications.apply",
        UpdatePresetApplicationCommandDto => command.Type == "presetApplications.update",
        SetPresetApplicationEnabledCommandDto => command.Type == "presetApplications.setEnabled",
        RemovePresetApplicationCommandDto => command.Type == "presetApplications.remove",
        CreateOrchestrationCountdownCommandDto => command.Type == "orchestration.createCountdown",
        CreateOrchestrationLeadInCommandDto => command.Type == "orchestration.createLeadIn",
        UpdateOrchestrationLeadInCommandDto => command.Type == "orchestration.updateLeadIn",
        DeleteOrchestrationLeadInCommandDto => command.Type == "orchestration.deleteLeadIn",
        SetLineLocalTransformCommandDto => command.Type == "orchestration.setLineLocalTransform",
        SetOrchestrationDefaultContainerCommandDto => command.Type == "orchestration.setDefaultContainer",
        SetOrchestrationLayoutDefaultPlacementCommandDto => command.Type == "orchestration.setLayoutDefaultPlacement",
        SetOrchestrationNodeGeometryCommandDto => command.Type == "orchestration.setNodeGeometry",
        SetOrchestrationLayoutSettingsCommandDto => command.Type == "orchestration.setLayoutSettings",
        SetOrchestrationSlotAnimationCommandDto => command.Type == "orchestration.setSlotAnimation",
        SetOrchestrationProgramAnimationPolicyCommandDto => command.Type == "orchestration.setProgramAnimationPolicy",
        SetOrchestrationNodeCommandDto => command.Type == "orchestration.setNode",
        CreateOrchestrationNodeCommandDto => command.Type == "orchestration.createNode",
        CopyOrchestrationSubtreeCommandDto => command.Type == "orchestration.copySubtree",
        ReparentOrchestrationNodeCommandDto => command.Type == "orchestration.reparent",
        ReorderOrchestrationChildrenCommandDto => command.Type == "orchestration.reorderChildren",
        AddOrchestrationSlotCommandDto => command.Type == "orchestration.addSlot",
        RemoveOrchestrationNodeCommandDto => command.Type == "orchestration.removeNode",
        ReorderOrchestrationSlotsCommandDto => command.Type == "orchestration.reorderSlots",
        CreateOrchestrationClipCommandDto => command.Type == "orchestration.createClip",
        RenameOrchestrationClipCommandDto => command.Type == "orchestration.renameClip",
        SetOrchestrationClipContainerCommandDto => command.Type == "orchestration.setClipContainer",
        DeleteOrchestrationClipCommandDto => command.Type == "orchestration.deleteClip",
        BasicLayoutCommandDto layout => command.Type == (layout.Preview ? "orchestration.previewBasicLayout" : "orchestration.applyBasicLayout"),
        _ => command.GetType() == typeof(ProjectCommandDto),
    };

    private bool TryExecuteEditingCommand(Project before, ProjectCommandDto command, out Project after,
        out ProjectTimingChangeSet changes, out LyricTimingCommandResultDto? timingResult, out BackendErrorResponse? error)
    {
        after = before;
        changes = ProjectTimingChangeSet.None;
        timingResult = null;
        error = null;
        if (command.Type is not ("lyrics.stampTokenStart" or "lyrics.stampTokenEnd" or "lyrics.clearTokenTime"
            or "lyrics.nudgeTokenTime" or "lyrics.shiftLine" or "lyrics.setTokenTimes")) return false;
        if (command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null)
        {
            error = InvalidEditingCommand();
            return true;
        }

        Func<EditingTimingResult>? execute = null;
        if (command.Type == "lyrics.setTokenTimes")
        {
            if (command is not SetTokenTimesCommandDto { Changes: { Length: > 0 and <= 1000 } items } || command.Target is not null)
            { error = InvalidEditingCommand(); return true; }
            var identities = new HashSet<(string Branch, string Line, string? Token)>();
            var batch = System.Collections.Immutable.ImmutableArray.CreateBuilder<LyricTimingTokenTimeChange>(items.Length);
            foreach (TokenTimeChangeDto item in items)
            {
                if (item is null || !TryEditingTarget(item.Target, tokenRequired: true, out LyricTimingEditTarget target)
                    || !TryParseMicroseconds(item.StartUs, out long start) || !TryParseMicroseconds(item.EndUs, out long end)
                    || !identities.Add((target.BranchId, target.LineId, target.TokenId)))
                { error = InvalidEditingCommand(); return true; }
                if (!LyricTimingEditUseCase.TryReadTime(before, target, out TimeRange? time))
                { error = MissingEditingTarget(command); return true; }
                batch.Add(new(target, time, start, end));
            }
            var input = new LyricTimingTokenBatchRequest(Workspace, Workspace.Generation, batch.ToImmutable());
            execute = () => TimingResult(LyricTimingEditUseCase.CommitTokenBatch(Workspace, input));
        }
        else
        {
            bool line = command.Type == "lyrics.shiftLine";
            if (!TryEditingTarget(command.Target, !line, out LyricTimingEditTarget target))
            { error = InvalidEditingCommand(); return true; }
            if (!LyricTimingEditUseCase.TryReadTime(before, target, out TimeRange? time))
            { error = MissingEditingTarget(command); return true; }
            var baseline = new LyricTimingEditBaseline(Workspace, Workspace.Generation, target, time);
            switch (command)
            {
                case StampTokenStartCommandDto start when command.Type == "lyrics.stampTokenStart"
                    && TryParseMicroseconds(start.PlayheadUs, out long startUs):
                    execute = () => TimingResult(LyricTimingEditUseCase.StampTokenStart(Workspace, baseline, startUs, AudioDurationUs));
                    break;
                case StampTokenEndCommandDto end when command.Type == "lyrics.stampTokenEnd"
                    && TryParseMicroseconds(end.PlayheadUs, out long endUs):
                    execute = () => TimingResult(LyricTimingEditUseCase.StampTokenEnd(Workspace, baseline, endUs, AudioDurationUs));
                    break;
                case ClearTokenTimeCommandDto when command.Type == "lyrics.clearTokenTime":
                    execute = () => TimingResult(LyricTimingEditUseCase.ClearToken(Workspace, baseline));
                    break;
                case NudgeTokenTimeCommandDto { FrameDelta: { } delta } when command.Type == "lyrics.nudgeTokenTime":
                    execute = () => TimingResult(LyricTimingEditUseCase.Nudge(Workspace, baseline, delta, AudioDurationUs));
                    break;
                case ShiftLineCommandDto shift when command.Type == "lyrics.shiftLine"
                    && TryParseMicroseconds(shift.DeltaUs, out long deltaUs):
                    LyricLine original = before.Lyrics.Branches.First(branch => branch.Id == target.BranchId)
                        .Lines.First(value => value.Id == target.LineId);
                    var lineBaseline = new LyricContentEditBaseline(Workspace, Workspace.Generation,
                        new(target.BranchId, target.LineId), original);
                    execute = () => TimingResult(LyricContentEditUseCase.ShiftLine(Workspace, lineBaseline, deltaUs, AudioDurationUs));
                    break;
            }
        }
        if (execute is null) { error = InvalidEditingCommand(); return true; }

        // HTTP commits a completed user action; the Application's desktop merge window must not join requests.
        Workspace.History.BreakMergeGroup();
        EditingTimingResult result;
        try { result = execute(); }
        finally { Workspace.History.BreakMergeGroup(); }
        if (!result.Accepted) error = MapTimingError(result.Error) with
        {
            Field = EditingErrorField(command, result.Error?.Code),
            FailureCode = result.Error?.Code.ToString(),
        };
        else if (result.EstimatedPlaceholder is { } estimated) timingResult = new(estimated);
        after = result.Project;
        changes = result.ChangeSet;
        return true;
    }

    private sealed record EditingTimingResult(bool Accepted, Project Project, ProjectTimingChangeSet ChangeSet,
        LyricTimingEditError? Error, bool? EstimatedPlaceholder = null);

    private static EditingTimingResult TimingResult(LyricTimingEditResult result) =>
        new(result.Accepted, result.Project, result.ChangeSet, result.Error);

    private static EditingTimingResult TimingResult(LyricTimingStampResult result) =>
        new(result.Accepted, result.Project, result.ChangeSet, result.Error, result.EstimatedPlaceholder);

    private static bool TryEditingTarget(LyricTimingTargetDto? value, bool tokenRequired, out LyricTimingEditTarget target)
    {
        target = null!;
        if (value is null || !IsStableId(value.BranchId) || !IsStableId(value.LineId)
            || (tokenRequired ? !IsStableId(value.TokenId) : value.TokenId is not null)) return false;
        target = new(value.BranchId, value.LineId, value.TokenId);
        return true;
    }

    private static BackendErrorResponse InvalidEditingCommand() => new(BackendErrorCode.InvalidRequest,
        "命令字段或目标类型不符合该具名操作；批量时间须为 1..1000 个互不重复的完整元素目标。", "command");

    private static BackendErrorResponse MissingEditingTarget(ProjectCommandDto command) => new(BackendErrorCode.CommandRejected,
        "歌词时间目标不存在。", EditingErrorField(command, LyricTimingEditErrorCode.TargetStale), FailureCode: "TargetStale");

    private static string EditingErrorField(ProjectCommandDto command, LyricTimingEditErrorCode? code) => command switch
    {
        SetTokenTimesCommandDto => "command.changes",
        _ when code is LyricTimingEditErrorCode.Untimed or LyricTimingEditErrorCode.TargetStale => "command.target",
        StampTokenStartCommandDto or StampTokenEndCommandDto => "command.playheadUs",
        NudgeTokenTimeCommandDto => "command.frameDelta",
        ShiftLineCommandDto => "command.deltaUs",
        _ => "command.target",
    };

    private static ProjectCommandDto CanonicalEditingCommand(ProjectCommandDto command)
    {
        static string? CanonicalTime(string? text) => TryParseMicroseconds(text, out long value)
            ? value.ToString(CultureInfo.InvariantCulture) : text;
        if (IsLyricRefactorCommand(command)) return CanonicalLyricRefactorCommand(command);
        if (IsRubyCommand(command)) return CanonicalRubyCommand(command);
        if (IsLeadInEditing(command)) return CanonicalLeadInEditingCommand(command);
        return command switch
        {
            SetOrchestrationLayoutKindCommandDto => CanonicalLayoutAndLineAnimationCommand(command),
            SetOrchestrationNodeGeometryCommandDto or SetOrchestrationLayoutSettingsCommandDto
                or SetOrchestrationSlotAnimationCommandDto or SetOrchestrationProgramAnimationPolicyCommandDto => CanonicalOrchestrationPropertiesCommand(command),
            ApplyPresetApplicationCommandDto or UpdatePresetApplicationCommandDto or SetPresetApplicationEnabledCommandDto or RemovePresetApplicationCommandDto => ClonePresetApplicationCommand(command),
            PatchStylesCommandDto or RestoreStylesCommandDto => CloneStyleCommand(command),
            ResplitLineCommandDto split => split with { Segments = split.Segments?.ToArray() },
            SetOutputDurationCommandDto duration => duration with { DurationUs = CanonicalTime(duration.DurationUs) },
            SetFrameRateCommandDto { Numerator: > 0, Denominator: > 0 } rate => CanonicalFrameRate(rate),
            StampTokenStartCommandDto start => start with { PlayheadUs = CanonicalTime(start.PlayheadUs) },
            StampTokenEndCommandDto end => end with { PlayheadUs = CanonicalTime(end.PlayheadUs) },
            CreateOrchestrationLeadInCommandDto cue => cue with { DurationUs = CanonicalTime(cue.DurationUs) },
            UpdateOrchestrationLeadInCommandDto cue => cue with { DurationUs = CanonicalTime(cue.DurationUs) },
            ShiftLineCommandDto shift => shift with { DeltaUs = CanonicalTime(shift.DeltaUs) },
            SetTokenTimesCommandDto batch => batch with { Changes = batch.Changes?.Select(item => item is null ? null!
                : item with { StartUs = CanonicalTime(item.StartUs), EndUs = CanonicalTime(item.EndUs) }).ToArray() },
            _ => command,
        };
    }

    private static SetFrameRateCommandDto CanonicalFrameRate(SetFrameRateCommandDto command)
    {
        FrameRate rate = new(command.Numerator!.Value, command.Denominator!.Value);
        return command with { Numerator = rate.Numerator, Denominator = rate.Denominator };
    }
}
