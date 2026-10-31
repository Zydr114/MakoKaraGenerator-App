using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private bool TryExecuteContentCommand(Project before, ProjectCommandDto command, out Project after,
        out ProjectChangeSetDto? changeSet, out LyricContentCommandFeedbackDto? feedback, out BackendErrorResponse? error)
    {
        after = before; changeSet = null; feedback = null; error = null;
        if (command.Type is not ("lyrics.setLineText" or "lyrics.setRubyText" or "lyrics.addLine"
            or "lyrics.duplicateLine" or "lyrics.removeLine" or "composition.setCanvasSize" or "composition.setFrameRate"
            or "composition.setBackgroundColor" or "composition.setOutputDuration" or "composition.useLyricsEnd" or "composition.useAudioDuration"))
            return false;
        bool content = command.Type.StartsWith("lyrics.", StringComparison.Ordinal);
        if (command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null
            || ((!content || command is AddLineCommandDto) && command.Target is not null))
        { error = InvalidContentCommand(); return true; }
        if (!content)
        {
            ProjectSettingsEditResult? settings = ExecuteSettingsCommand(before, command);
            if (settings is null) { error = InvalidContentCommand(); return true; }
            if (!settings.Accepted)
                error = ContentRejected(command, settings.Error?.Code.ToString(), settings.Error?.Detail);
            after = settings.Project;
            changeSet = settings.Changed ? new(["composition", "timeline", "renderInputs"], [], [], [], true)
                : new([], [], [], [], false);
            return true;
        }

        string? branchId;
        string? lineId;
        PreparedLyricContentEdit? prepared;
        if (command is AddLineCommandDto add)
        {
            if (!IsStableId(add.BranchId) || add.AfterLineId is not null && !IsStableId(add.AfterLineId))
            { error = InvalidContentCommand(); return true; }
            branchId = add.BranchId!; lineId = null;
            if (!before.Lyrics.Branches.Any(branch => branch.Id == branchId))
            { error = ContentRejected(command, "TargetStale") with { Field = "command.branchId" }; return true; }
            LyricLine? previous = FindContentLine(before, branchId, add.AfterLineId);
            prepared = LyricContentEditUseCase.PrepareAddLineAfter(Workspace,
                new(Workspace, Workspace.Generation, branchId, add.AfterLineId, previous));
        }
        else
        {
            bool ruby = command is SetRubyTextCommandDto;
            if (!TryEditingTarget(command.Target, ruby, out LyricTimingEditTarget target))
            { error = InvalidContentCommand(); return true; }
            branchId = target.BranchId; lineId = target.LineId;
            LyricLine? line = FindContentLine(before, branchId, lineId);
            if (line is null) { error = ContentRejected(command, "TargetStale"); return true; }
            var contentTarget = new LyricContentEditTarget(branchId, lineId, target.TokenId);
            var baseline = new LyricContentEditBaseline(Workspace, Workspace.Generation, contentTarget, line);
            try
            {
                if (command is SetLineTextCommandDto { Text: { } text } && text != line.Text) ValidateContentTextBudget(text);
                if (command is SetRubyTextCommandDto { Text: { } rubyText }
                    && rubyText != (line.RubyAnnotations.FirstOrDefault(value => value.BaseTokenId == target.TokenId)?.Text ?? string.Empty))
                    ValidateContentTextBudget(rubyText);
                ReadModelBudget newTokens = new();
                prepared = command switch
                {
                    SetLineTextCommandDto { Text: { } value } => LyricContentEditUseCase.PrepareSetLineText(Workspace,
                        new(Workspace, Workspace.Generation, contentTarget, line.Text, value), () => newTokens.Item()),
                    SetRubyTextCommandDto { Text: { } value } => LyricContentEditUseCase.PrepareSetRubyText(Workspace,
                        new(Workspace, Workspace.Generation, contentTarget,
                            line.RubyAnnotations.FirstOrDefault(annotation => annotation.BaseTokenId == target.TokenId), value)),
                    DuplicateLineCommandDto => LyricContentEditUseCase.PrepareDuplicateLine(Workspace, baseline),
                    RemoveLineCommandDto => LyricContentEditUseCase.PrepareRemoveLine(Workspace, baseline),
                    _ => null,
                };
            }
            catch (ReadModelBudgetExceededException) { error = ContentBudgetExceeded(); return true; }
        }
        if (prepared is null) { error = InvalidContentCommand(); return true; }
        LyricContentEditResult preview = prepared.Result;
        if (!preview.Accepted) { error = ContentRejected(command, preview.Error?.Code.ToString(), preview.Error?.Detail); return true; }
        if (preview.Changed && command is not RemoveLineCommandDto)
        {
            try { ValidateContentLineBudget(preview.Project, branchId!, preview.CreatedLineId ?? lineId!); }
            catch (ReadModelBudgetExceededException)
            {
                error = ContentBudgetExceeded();
                return true;
            }
        }

        Workspace.History.BreakMergeGroup();
        LyricContentEditResult result;
        try { result = LyricContentEditUseCase.CommitPrepared(Workspace, prepared); }
        finally { Workspace.History.BreakMergeGroup(); }
        if (!result.Accepted) { error = ContentRejected(command, result.Error?.Code.ToString(), result.Error?.Detail); return true; }
        after = result.Project;
        string? affectedLine = result.CreatedLineId ?? lineId;
        changeSet = result.Changed ? new(["lyricsContent", "lyricsStructure", "timeline", "renderInputs"],
            [branchId!], affectedLine is null ? [] : [affectedLine], [], true) : new([], [], [], [], false);
        bool selectionOperation = command is AddLineCommandDto or DuplicateLineCommandDto or RemoveLineCommandDto;
        feedback = new(Selection: selectionOperation ? new(branchId!, result.SelectedLineId is not null, result.SelectedLineId) : null,
            CreatedLineId: result.CreatedLineId, DeletedLineId: command is RemoveLineCommandDto ? lineId : null,
            InsertedIndex: result.InsertedIndex, OverlapsNext: command is DuplicateLineCommandDto ? result.OverlapsNext : null,
            PreservedTokenCount: command is SetLineTextCommandDto ? result.PreservedCount : null,
            ResetTokenCount: command is SetLineTextCommandDto ? result.ResetCount : null);
        return true;
    }

    private ProjectSettingsEditResult? ExecuteSettingsCommand(Project before, ProjectCommandDto command)
    {
        Composition source = before.Composition;
        var durationBaseline = new ProjectOutputDurationEditBaseline(Workspace, Workspace.Generation, source.OutputDurationUs);
        Workspace.History.BreakMergeGroup();
        try
        {
            return command switch
            {
                SetCanvasSizeCommandDto { Width: { } width, Height: { } height } => ProjectSettingsEditUseCase.SetCanvasSize(Workspace,
                    new(Workspace, Workspace.Generation, source.CanvasWidth, source.CanvasHeight, width, height)),
                SetFrameRateCommandDto { Numerator: { } numerator, Denominator: { } denominator } => ProjectSettingsEditUseCase.SetFrameRate(Workspace,
                    new(Workspace, Workspace.Generation, source.FrameRate,
                        numerator > 0 && denominator > 0 ? new FrameRate(numerator, denominator) : default)),
                SetBackgroundColorCommandDto { Red: { } red, Green: { } green, Blue: { } blue, Alpha: { } alpha } =>
                    ProjectSettingsEditUseCase.SetBackgroundColor(Workspace,
                        new(Workspace, Workspace.Generation, source.BackgroundColor, new(red, green, blue, alpha))),
                SetOutputDurationCommandDto duration when duration.DurationUs is null || TryParseMicroseconds(duration.DurationUs, out _) =>
                    ProjectSettingsEditUseCase.SetOutputDuration(Workspace, new(Workspace, Workspace.Generation, source.OutputDurationUs,
                        duration.DurationUs is null ? null : long.Parse(duration.DurationUs, System.Globalization.CultureInfo.InvariantCulture))),
                UseLyricsEndCommandDto => ProjectSettingsEditUseCase.UseLyricsEnd(Workspace, durationBaseline),
                UseAudioDurationCommandDto => ProjectSettingsEditUseCase.UsePreparedMediaDuration(Workspace, durationBaseline, AudioDurationUs),
                _ => null,
            };
        }
        finally { Workspace.History.BreakMergeGroup(); }
    }

    private static LyricLine? FindContentLine(Project project, string branchId, string? lineId) => lineId is null ? null
        : project.Lyrics.Branches.FirstOrDefault(branch => branch.Id == branchId)?.Lines.FirstOrDefault(line => line.Id == lineId);

    private static BackendErrorResponse InvalidContentCommand() => new(BackendErrorCode.InvalidRequest,
        "命令缺少必填字段或包含不符合该具名操作的目标和值。", "command");

    private static BackendErrorResponse ContentBudgetExceeded() => new(BackendErrorCode.ResourceTooLarge,
        "编辑结果超过单行完整读模型的 1MiB JSON 或 20000 项预算；工程未改变。", "command");

    private static BackendErrorResponse ContentRejected(ProjectCommandDto command, string? code, string? detail = null)
    {
        string field = command switch
        {
            SetLineTextCommandDto or SetRubyTextCommandDto when code == "DomainValidationFailed" => "command.text",
            SetLineTextCommandDto or SetRubyTextCommandDto or DuplicateLineCommandDto or RemoveLineCommandDto
                when code == "TargetStale" => "command.target",
            AddLineCommandDto { AfterLineId: not null } when code == "TargetStale" => "command.afterLineId",
            AddLineCommandDto when code == "TargetStale" => "command.branchId",
            SetCanvasSizeCommandDto { Width: <= 0 } => "command.width",
            SetCanvasSizeCommandDto { Height: <= 0 } => "command.height",
            SetFrameRateCommandDto { Numerator: <= 0 } => "command.numerator",
            SetFrameRateCommandDto { Denominator: <= 0 } => "command.denominator",
            SetOutputDurationCommandDto => "command.durationUs",
            _ => "command",
        };
        return new(BackendErrorCode.CommandRejected, detail ?? "歌词内容或工程设置命令被共享领域规则拒绝。", field, FailureCode: code);
    }
}
