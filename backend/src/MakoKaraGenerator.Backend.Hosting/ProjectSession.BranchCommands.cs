using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private readonly int _branchReceiptMaxBytes = 64 * 1024;

    private static bool BranchCommandTypeMatches(ProjectCommandDto command) => command switch
    {
        AddTranslationBranchCommandDto => command.Type == "lyrics.addTranslationBranch",
        ConfigureBranchCommandDto => command.Type == "lyrics.configureBranch",
        RemoveBranchCommandDto => command.Type == "lyrics.removeBranch",
        LinkPrimaryLineCommandDto => command.Type == "lyrics.linkPrimaryLine",
        _ => false,
    };

    private bool TryExecuteBranchCommand(Project before, ProjectCommandDto command, out Project after,
        out ProjectTimingChangeSet changes, out LyricBranchCommandResultDto? branchResult, out BackendErrorResponse? error)
    {
        after = before; changes = ProjectTimingChangeSet.None; branchResult = null; error = null;
        if (command.Type is not ("lyrics.addTranslationBranch" or "lyrics.configureBranch" or "lyrics.removeBranch" or "lyrics.linkPrimaryLine")) return false;
        if (!BranchCommandTypeMatches(command) || command.Target is not null || command.StartUs is not null
            || command.EndUs is not null || command.AnalysisJobId is not null)
        { error = InvalidBranchCommand("command"); return true; }

        PreparedLyricBranchEdit prepared;
        switch (command)
        {
            case AddTranslationBranchCommandDto add:
                if (add.Name is null) { error = InvalidBranchCommand("command.name"); return true; }
                prepared = LyricBranchEditUseCase.PrepareAddTranslation(Workspace,
                    new(Workspace, Workspace.Generation, before.Lyrics, add.Name, add.Language));
                break;
            case ConfigureBranchCommandDto configure:
                if (!TryBranch(before, configure.BranchId, out LyricBranch? branch, out error)) return true;
                if (configure.Name is null) { error = InvalidBranchCommand("command.name"); return true; }
                if (configure.Role is not { } role || !Enum.IsDefined(role)) { error = InvalidBranchCommand("command.role"); return true; }
                if (configure.TimingMode is not { } mode || !Enum.IsDefined(mode)) { error = InvalidBranchCommand("command.timingMode"); return true; }
                prepared = LyricBranchEditUseCase.PrepareConfigure(Workspace, new(
                    new(Workspace, Workspace.Generation, branch!.Id, branch),
                    new(configure.Name, configure.Language, (LyricBranchRole)role, (LyricBranchTimingMode)mode, branch.StyleOverride)));
                break;
            case RemoveBranchCommandDto remove:
                if (!TryBranch(before, remove.BranchId, out LyricBranch? removed, out error)) return true;
                prepared = LyricBranchEditUseCase.PrepareRemove(Workspace,
                    new(Workspace, Workspace.Generation, removed!.Id, removed));
                break;
            case LinkPrimaryLineCommandDto link:
                if (!TryBranch(before, link.BranchId, out LyricBranch? linked, out error)) return true;
                if (string.IsNullOrWhiteSpace(link.LineId)) { error = InvalidBranchCommand("command.lineId"); return true; }
                LyricLine? line = linked!.Lines.FirstOrDefault(item => item.Id == link.LineId);
                if (line is null) { error = BranchRejected(LyricBranchEditErrorCode.TargetStale, "command.lineId"); return true; }
                prepared = LyricBranchEditUseCase.PrepareLinkPrimaryLine(Workspace,
                    new(Workspace, Workspace.Generation, linked.Id, line.Id, line.PrimaryLineId, link.PrimaryLineId));
                break;
            default: error = InvalidBranchCommand("command"); return true;
        }

        LyricBranchEditResult candidate = prepared.Result;
        if (!candidate.Accepted) { error = BranchFailure(command, candidate.Error); return true; }
        if (!ReferenceEquals(candidate.Project, before))
        {
            try
            {
                switch (command)
                {
                    case AddTranslationBranchCommandDto:
                        ValidateBranchMetadataBudget(candidate.Project, candidate.SelectedBranchId!);
                        break;
                    case ConfigureBranchCommandDto configure:
                        ValidateBranchMetadataBudget(candidate.Project, configure.BranchId!);
                        break;
                    case LinkPrimaryLineCommandDto link:
                        ValidateContentLineBudget(candidate.Project, link.BranchId!, link.LineId!);
                        break;
                }
                ValidateBranchFeedbackBudget(prepared, command);
            }
            catch (ReadModelBudgetExceededException)
            {
                error = new(BackendErrorCode.ResourceTooLarge,
                    "来源候选超过单项读模型或命令回执预算；工程未修改。", "command");
                return true;
            }
        }
        LyricBranchEditResult committed = LyricBranchEditUseCase.CommitPrepared(Workspace, prepared);
        if (!committed.Accepted) { error = BranchFailure(command, committed.Error); return true; }
        after = committed.Project;
        changes = ReferenceEquals(after, before) ? ProjectTimingChangeSet.None : ProjectTimingChangeSet.FullRefresh;
        if (!ReferenceEquals(after, before) && committed.SelectedBranchId is { } selected)
            branchResult = new(selected);
        return true;
    }

    private static bool TryBranch(Project project, string? branchId, out LyricBranch? branch, out BackendErrorResponse? error)
    {
        branch = null; error = null;
        if (string.IsNullOrWhiteSpace(branchId)) { error = InvalidBranchCommand("command.branchId"); return false; }
        branch = project.Lyrics.Branches.FirstOrDefault(item => item.Id == branchId);
        if (branch is not null) return true;
        error = BranchRejected(LyricBranchEditErrorCode.TargetStale, "command.branchId"); return false;
    }

    private static BackendErrorResponse InvalidBranchCommand(string field) =>
        new(BackendErrorCode.InvalidRequest, "来源命令字段缺失、类型或组合无效。", field);

    private static BackendErrorResponse BranchRejected(LyricBranchEditErrorCode code, string field, string? detail = null) =>
        new(BackendErrorCode.CommandRejected, detail ?? "来源命令被共享领域规则拒绝。", field, FailureCode: code.ToString());

    private static BackendErrorResponse BranchFailure(ProjectCommandDto command, LyricBranchEditError? error)
    {
        LyricBranchEditErrorCode code = error?.Code ?? LyricBranchEditErrorCode.DomainValidationFailed;
        string field = code switch
        {
            LyricBranchEditErrorCode.InvalidPrimaryLine => "command.primaryLineId",
            LyricBranchEditErrorCode.PrimaryBranchProtected => command is ConfigureBranchCommandDto ? "command.role" : "command.branchId",
            LyricBranchEditErrorCode.InvalidConfiguration => command switch
            {
                AddTranslationBranchCommandDto => "command.name",
                ConfigureBranchCommandDto configure when string.IsNullOrWhiteSpace(configure.Name) => "command.name",
                _ => "command",
            },
            LyricBranchEditErrorCode.TargetStale or LyricBranchEditErrorCode.InvalidTarget => command is LinkPrimaryLineCommandDto ? "command.lineId" : "command.branchId",
            _ => "command",
        };
        return BranchRejected(code, field, error?.Detail);
    }
}
