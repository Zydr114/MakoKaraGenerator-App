using System.Collections.Immutable;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private bool TryExecuteTokenCommand(Project before, ProjectCommandDto command, out Project after,
        out ProjectChangeSetDto? changeSet, out LyricTokenCommandResultDto? tokenResult, out BackendErrorResponse? error)
    {
        after = before; changeSet = null; tokenResult = null; error = null;
        if (command.Type is not ("lyrics.splitToken" or "lyrics.mergeToken" or "lyrics.reorderToken"
            or "lyrics.resplitLine" or "lyrics.resplitBranch")) return false;
        if (command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null)
        { error = InvalidTokenCommand(); return true; }
        string branchId;
        string? lineId = null;
        LyricBranch? branch;
        LyricContentEditBaseline? baseline = null;
        bool wholeBranch = command is ResplitBranchCommandDto;
        if (wholeBranch)
        {
            if (command is not ResplitBranchCommandDto { BranchId: { } id, Rule: { } rule, EstimateTimes: { } }
                || !IsStableId(id) || !Enum.IsDefined(rule) || command.Target is not null)
            { error = InvalidTokenCommand(); return true; }
            branchId = id;
            branch = before.Lyrics.Branches.FirstOrDefault(value => value.Id == id);
        }
        else
        {
            bool token = command is not ResplitLineCommandDto;
            if (!TryEditingTarget(command.Target, token, out LyricTimingEditTarget target))
            { error = InvalidTokenCommand(); return true; }
            branchId = target.BranchId; lineId = target.LineId;
            branch = before.Lyrics.Branches.FirstOrDefault(value => value.Id == branchId);
            LyricLine? line = branch?.Lines.FirstOrDefault(value => value.Id == lineId);
            if (line is null || token && !line.Tokens.Any(value => value.Id == target.TokenId))
            { error = TokenCommandRejected(command, "TargetStale"); return true; }
            baseline = new(Workspace, Workspace.Generation, new(branchId, lineId,
                command is ReorderTokenCommandDto ? null : target.TokenId), line);
        }
        if (branch is null) { error = TokenCommandRejected(command, "TargetStale"); return true; }
        ReadModelBudget segments = new(); ReadModelBudget tokens = new();
        long preparedBytes = 0;
        var observers = new LyricTokenPreparationObservers(OnStartingLine: _ => { segments = new(); tokens = new(); },
            OnCreatingSegment: () => segments.Item(), OnCreatingToken: () => tokens.Item(), OnPreparedLine: (index, line) =>
            {
                long bytes = ValidatePreparedTokenLineBudget(branch, index, line);
                if (wholeBranch && bytes > TokenEditingLimits.MaxPreparedBranchBytes - preparedBytes)
                    throw new ReadModelBudgetExceededException();
                preparedBytes += bytes;
            });
        try
        {
            if (command is ReorderTokenCommandDto reorder)
            {
                if (reorder.DestinationIndex is not { } destination || reorder.ReassignTimeSlots is not { } reassign)
                { error = InvalidTokenCommand(); return true; }
                PreparedLyricContentEdit prepared = LyricContentEditUseCase.PrepareReorderToken(Workspace,
                    new(baseline!, reorder.Target!.TokenId!, destination, reassign));
                if (!prepared.Result.Accepted)
                { error = TokenCommandRejected(command, prepared.Result.Error?.Code.ToString(), prepared.Result.Error?.Detail); return true; }
                if (prepared.Result.Changed) ValidateContentLineBudget(prepared.Result.Project, branchId, lineId!);
                Workspace.History.BreakMergeGroup();
                LyricContentEditResult result;
                try { result = LyricContentEditUseCase.CommitPrepared(Workspace, prepared); }
                finally { Workspace.History.BreakMergeGroup(); }
                if (!result.Accepted) { error = TokenCommandRejected(command, result.Error?.Code.ToString(), result.Error?.Detail); return true; }
                after = result.Project; tokenResult = new(result.Changed ? 1 : 0);
            }
            else
            {
                PreparedLyricTokenEdit? prepared = command switch
                {
                    SplitTokenCommandDto => LyricTokenEditUseCase.PrepareSplitToken(Workspace, baseline!, observers),
                    MergeTokenCommandDto => LyricTokenEditUseCase.PrepareMergeToken(Workspace, baseline!, observers),
                    ResplitLineCommandDto { Segments: { Length: <= TokenEditingLimits.MaxSegments } parts, EstimateTimes: { } estimate }
                        when !parts.Any(value => value is null) => LyricTokenEditUseCase.PrepareResplitLine(Workspace,
                            new(baseline!, ImmutableArray.CreateRange(parts), estimate), observers),
                    ResplitBranchCommandDto { Rule: { } rule, EstimateTimes: { } estimate } =>
                        LyricTokenEditUseCase.PrepareResplitBranch(Workspace,
                            new(new(Workspace, Workspace.Generation, branchId, branch), (TokenSplitRule)rule, estimate), observers),
                    _ => null,
                };
                if (prepared is null) { error = InvalidTokenCommand(); return true; }
                if (!prepared.Result.Accepted)
                { error = TokenCommandRejected(command, prepared.Result.Error?.Code.ToString(), prepared.Result.Error?.Detail); return true; }
                Workspace.History.BreakMergeGroup();
                LyricTokenEditResult result;
                try { result = LyricTokenEditUseCase.CommitPrepared(Workspace, prepared); }
                finally { Workspace.History.BreakMergeGroup(); }
                if (!result.Accepted) { error = TokenCommandRejected(command, result.Error?.Code.ToString(), result.Error?.Detail); return true; }
                after = result.Project;
                bool counts = command is SplitTokenCommandDto or ResplitLineCommandDto or ResplitBranchCommandDto;
                LyricTokenSelectionDto? selection = result.SelectedLineId is { } selectedLine
                    ? new(branchId, selectedLine, result.SelectedTokenId is not null, result.SelectedTokenId) : null;
                tokenResult = new(result.ChangedLineCount, selection, counts ? result.PreservedCount : null,
                    counts ? result.CreatedCount : null, counts ? result.EstimatedCount : null);
            }
        }
        catch (ReadModelBudgetExceededException)
        {
            error = new(BackendErrorCode.ResourceTooLarge,
                "元素准备超过单行 1MiB／20000 项，或来源改变行累计 16MiB 操作预算；工程未改变。",
                command is ResplitLineCommandDto ? "command.segments" : "command");
            return true;
        }
        bool changed = !ReferenceEquals(before, after);
        changeSet = changed ? new(["lyricsContent", "lyricsStructure", "timeline", "renderInputs"], [branchId],
            lineId is null ? [] : [lineId], [], true) : new([], [], [], [], false);
        return true;
    }

    private static BackendErrorResponse InvalidTokenCommand() => new(BackendErrorCode.InvalidRequest,
        "元素命令需要与 type 相符的稳定目标、显式选择和合法字段。", "command");

    private static BackendErrorResponse TokenCommandRejected(ProjectCommandDto command, string? code, string? detail = null)
    {
        string field = command switch
        {
            ResplitBranchCommandDto when code == "TargetStale" => "command.branchId",
            _ when code == "TargetStale" => "command.target",
            ResplitLineCommandDto when code == "InvalidTarget" => "command.segments",
            ReorderTokenCommandDto when code == "DomainValidationFailed" => "command",
            _ => "command",
        };
        return new(BackendErrorCode.CommandRejected, detail ?? "元素结构命令被共享领域规则拒绝。", field, FailureCode: code);
    }
}
