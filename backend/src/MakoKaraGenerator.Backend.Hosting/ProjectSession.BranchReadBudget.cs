using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private void ValidateBranchMetadataBudget(Project candidate, string branchId)
    {
        int offset = -1;
        for (int index = 0; index < candidate.Lyrics.Branches.Length; index++)
            if (candidate.Lyrics.Branches[index].Id == branchId) { offset = index; break; }
        if (offset < 0) throw new InvalidOperationException("固定来源候选不存在。");
        var branch = candidate.Lyrics.Branches[offset];
        ReadModelBudget budget = new(); budget.Item();
        budget.Text(ProjectSessionId, candidate.Lyrics.PrimaryBranchId, branch.Id, branch.Name, branch.Language);
        _ = BoundedReadModel(new ProjectLyricBranchesResponse(ProjectSessionId,
            checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture), candidate.Lyrics.PrimaryBranchId,
            offset, candidate.Lyrics.Branches.Length, NextOffset(offset, 1, candidate.Lyrics.Branches.Length),
            [new(branch.Id, branch.Name, (ProjectLyricBranchRole)branch.Role, branch.Language,
                (ProjectLyricTimingMode)branch.TimingMode, branch.Lines.Length)]));
    }

    private void ValidateBranchFeedbackBudget(PreparedLyricBranchEdit prepared, ProjectCommandDto command)
    {
        if (prepared.Result.SelectedBranchId is not { } selected) return;
        // Include the receipt envelope and conservative maximum cursor/boolean lengths before history changes.
        string revision = checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture);
        ProjectCommandResponse upperBound = new(ProjectSessionId, RevisionText, revision, true,
            false, false, false, command is AddTranslationBranchCommandDto ? "branch.add" : "branch.remove", null, new(["project"], [], [], [], true),
            EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture),
            BranchResult: new(selected));
        if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upperBound)).Length > _branchReceiptMaxBytes)
            throw new ReadModelBudgetExceededException();
    }
}
