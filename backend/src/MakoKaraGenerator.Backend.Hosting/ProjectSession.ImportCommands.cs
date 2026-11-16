using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal sealed record ImportCapture(LyricsImportEditSnapshot Snapshot, LyricsImportEditBaseline Baseline,
        string SessionId, long Revision, long NextRevision, long AudioDuration, string HostId, string ClientId,
        int ReceiptLimit, ProjectCommandDto Command, string? Name, LyricBranchRole Role);

    internal async Task<(ImportCapture? Capture, BackendErrorResponse? Error)> CaptureImportAsync(ProjectCommandRequest request)
    {
        await Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (MutationBlocked) return (null, new(BackendErrorCode.SessionClosing, "工程会话正在关闭。", "command"));
            if (IsReadOnly) return (null, new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。", "command"));
            if (!TryParseRevision(request.ExpectedRevision, out long revision)) return (null, new(BackendErrorCode.InvalidRequest, "expectedRevision 无效。", "expectedRevision"));
            if (revision != ProjectRevision) return (null, new(BackendErrorCode.RevisionConflict, "工程修订已经改变。", "expectedRevision"));
            ProjectCommandDto? command = request.Command;
            if (command is null || command.Target is not null || command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null
                || command is ImportAssetCommandDto && command.Type != "lyrics.importAsset"
                || command is AppendTextCommandDto && command.Type != "lyrics.appendText") return (null, InvalidImport("command"));
            LyricsImportDestinationDto? destination;
            switch (command)
            {
                case ImportAssetCommandDto import:
                    if (string.IsNullOrWhiteSpace(import.AssetId)) return (null, InvalidImport("command.assetId"));
                    if (import.Options is { } options && !Enum.IsDefined(options.SplitRule)) return (null, InvalidImport("command.options.splitRule"));
                    if (import.Options?.SourceBranchId is { } sourceBranchId && !IsStableId(sourceBranchId))
                        return (null, InvalidImport("command.options.sourceBranchId"));
                    destination = import.Destination; break;
                case AppendTextCommandDto append:
                    if (append.Text is null) return (null, InvalidImport("command.text"));
                    destination = append.Destination; break;
                default: return (null, InvalidImport("command"));
            }
            var snapshot = LyricsImportEditUseCase.Capture(Workspace);
            LyricsImportEditBaseline baseline; string? name = null; LyricBranchRole role = LyricBranchRole.Translation;
            switch (destination)
            {
                case ExistingLyricsDestinationDto existing when destination.Kind == "existing":
                    if (!IsStableId(existing.BranchId)) return (null, InvalidImport("command.destination.branchId"));
                    var branch = snapshot.Project.Lyrics.Branches.FirstOrDefault(value => value.Id == existing.BranchId);
                    if (branch is null) return (null, ImportFailure(new(LyricsImportEditErrorCode.TargetStale), "command.destination.branchId"));
                    baseline = new(Workspace, Workspace.Generation, branch.Id, branch); break;
                case NewBranchLyricsDestinationDto created when command is ImportAssetCommandDto && destination.Kind == "newBranch":
                    if (!Enum.IsDefined(created.Role) || created.Role == ProjectLyricBranchRole.Primary) return (null, InvalidImport("command.destination.role"));
                    name = created.Name; role = (LyricBranchRole)created.Role;
                    baseline = new(Workspace, Workspace.Generation, null, ExpectedLyrics: snapshot.Project.Lyrics); break;
                case NewTranslationLyricsDestinationDto translation when command is AppendTextCommandDto && destination.Kind == "newTranslation":
                    name = translation.Name; baseline = new(Workspace, Workspace.Generation, null, ExpectedLyrics: snapshot.Project.Lyrics); break;
                case AllSourcesLyricsDestinationDto when command is ImportAssetCommandDto importAll && destination.Kind == "allSources":
                    if (importAll.Options?.SourceBranchId is not null) return (null, InvalidImport("command.options.sourceBranchId"));
                    baseline = new(Workspace, Workspace.Generation, null, ExpectedLyrics: snapshot.Project.Lyrics); break;
                default: return (null, InvalidImport("command.destination"));
            }
            if (destination is not ExistingLyricsDestinationDto and not AllSourcesLyricsDestinationDto && string.IsNullOrWhiteSpace(name))
                return (null, InvalidImport("command.destination.name"));
            return (new(snapshot, baseline, ProjectSessionId, revision, checked(revision + 1), AudioDurationUs,
                HostInstanceId, OwnerClientId, _branchReceiptMaxBytes, command, name, role), null);
        }
        finally { Writer.Release(); }
    }

    internal async Task<ProjectCommandExecutionResult> CommitImportAsync(ImportCapture capture,
        PreparedLyricsImportEdit prepared, LyricsImportCommandResultDto feedback)
    {
        await Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (MutationBlocked) return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。", "command"));
            if (IsReadOnly) return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。", "command"));
            if (ProjectRevision != capture.Revision || !ReferenceEquals(Workspace.Project, capture.Snapshot.Project))
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.RevisionConflict, "准备期间工程修订已经改变。", "expectedRevision"));
            var committed = LyricsImportEditUseCase.CommitCandidate(Workspace, prepared);
            if (!committed.Accepted) return ProjectCommandExecutionResult.Rejected(ImportFailure(committed.Error, "command"));
            ProjectRevision = capture.NextRevision;
            ProjectChangeSetDto changes = new(["project"], [], [], [], true);
            string cursor = PublishEvent("project.changed", changes);
            return ProjectCommandExecutionResult.FromResponse(new(ProjectSessionId, capture.Revision.ToString(CultureInfo.InvariantCulture),
                capture.NextRevision.ToString(CultureInfo.InvariantCulture), true, Workspace.IsDirty, Workspace.CanUndo, Workspace.CanRedo,
                Workspace.UndoName, Workspace.RedoName, changes, EventCursor: cursor, ImportResult: feedback));
        }
        finally { Writer.Release(); }
    }

    internal static BackendErrorResponse InvalidImport(string field) => new(BackendErrorCode.InvalidRequest, "导入命令字段或目标组合无效。", field);
    internal static BackendErrorResponse ImportFailure(LyricsImportEditError? error, string field)
    {
        var code = error?.Code ?? LyricsImportEditErrorCode.DomainValidationFailed;
        return new(code is LyricsImportEditErrorCode.EmptyImport or LyricsImportEditErrorCode.EmptyText ? BackendErrorCode.EmptyLyrics : BackendErrorCode.CommandRejected,
            "歌词导入或追加被共享规则拒绝。", field, FailureCode: code.ToString());
    }

    internal static LyricsImportCommandResultDto BudgetImportCandidate(ImportCapture capture, PreparedLyricsImportEdit prepared, LyricsImportReport? report)
    {
        LyricsImportEditResult result = prepared.Result;
        var branch = result.Project.Lyrics.Branches.Single(value => value.Id == result.SelectedBranchId);
        if (!IsStableId(result.SelectedBranchId) || !IsStableId(result.SelectedLineId)) throw new InvalidImportIdentifierException();
        var metaBudget = new ReadModelBudget(); metaBudget.Item(); metaBudget.Text(branch.Id, branch.Name, branch.Language, capture.SessionId, result.Project.Lyrics.PrimaryBranchId);
        int branchOffset = result.Project.Lyrics.Branches.IndexOf(branch);
        _ = BoundedReadModel(new ProjectLyricBranchesResponse(capture.SessionId, capture.NextRevision.ToString(CultureInfo.InvariantCulture), result.Project.Lyrics.PrimaryBranchId,
            branchOffset, result.Project.Lyrics.Branches.Length, NextOffset(branchOffset, 1, result.Project.Lyrics.Branches.Length),
            [new(branch.Id, branch.Name, (ProjectLyricBranchRole)branch.Role, branch.Language, (ProjectLyricTimingMode)branch.TimingMode, branch.Lines.Length)]));
        LyricsImportCommandResultDto feedback = new(result.LineCount, result.SelectedBranchId!, result.SelectedLineId!, report is null ? null : ProjectImportReport(report));
        ProjectCommandResponse upper = new(capture.SessionId, capture.Revision.ToString(CultureInfo.InvariantCulture), capture.NextRevision.ToString(CultureInfo.InvariantCulture),
            true, false, false, false, capture.Command is AppendTextCommandDto ? "lyrics.paste"
                : capture.Command is ImportAssetCommandDto { Destination: AllSourcesLyricsDestinationDto } ? "lyrics.importAll"
                : capture.Baseline.BranchId is null ? "branch.import" : "lyrics.import",
            null, new(["project"], [], [], [], true), EventCursor: capture.HostId + ":" + capture.ClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture), ImportResult: feedback);
        if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > capture.ReceiptLimit)
            throw new LyricsImportPreparationLimitException();
        return feedback;
    }

    internal sealed class ImportRowBudget(ImportCapture capture)
    {
        private long _total;
        internal void Observe(LyricBranch branch, LyricLine line, int index, int total)
        {
            if (!IsStableId(branch.Id) || !IsStableId(line.Id) || line.Tokens.Any(token => !IsStableId(token.Id))
                || line.RubyAnnotations.Any(ruby => !IsStableId(ruby.Id) || !IsStableId(ruby.BaseTokenId) || ruby.EffectiveSegments.Any(segment => !IsStableId(segment.Id)))) throw new InvalidImportIdentifierException();
            ReadModelBudget budget = new(); budget.Text(capture.SessionId, branch.Id);
            var projected = ProjectEditableLine(branch, line, budget);
            var response = new ProjectLyricsWindowResponse(capture.SessionId, capture.NextRevision.ToString(CultureInfo.InvariantCulture), branch.Id,
                index, total, NextOffset(index, 1, total), [projected]);
            using ReadModelCountingStream counter = new(); JsonSerializer.Serialize(counter, response, ReadModelJson);
            _total += counter.Length;
            if (_total > 16L * 1024 * 1024) throw new LyricsImportPreparationLimitException();
        }
    }

    internal static LyricsImportReportDto ProjectImportReport(LyricsImportReport report) => new(report.ImporterId, report.ImporterName, report.SourceFormat,
        report.SourceLineCount, report.LineCount, report.TokenCount, report.TimedTokenCount, report.MissingTokenCount, report.EstimatedTokenCount,
        report.SkippedLineCount, report.MergedContinuationCount, report.DerivedLineEndCount, report.FallbackLineEndCount,
        report.OffsetUs.ToString(CultureInfo.InvariantCulture), report.EncodingName, report.HadByteOrderMark, (LyricsImportNewLineKind)report.NewLineKind,
        [.. report.Issues.Select(ProjectImportIssue)], report.DetectionReason, [.. report.CandidateNames], report.IsFormatAmbiguous, [.. report.DiscardedFeatures],
        report.SplitLineCount, report.SplitCreatedTokenCount, report.SplitEstimatedTokenCount, report.IssueCount,
        report.MaxSeverity is { } severity ? (LyricsImportIssueSeverity)severity : null);

    internal static LyricsImportIssueDto ProjectImportIssue(LyricsImportIssue issue) => new((LyricsImportIssueKindDto)issue.Kind,
        (LyricsImportIssueSeverity)issue.Severity, issue.SourceLineNumber, issue.RawText, issue.Message);
    internal static bool IsImportReadBudgetFailure(Exception error) => error is ReadModelBudgetExceededException;
    internal sealed class InvalidImportIdentifierException : Exception;
}
