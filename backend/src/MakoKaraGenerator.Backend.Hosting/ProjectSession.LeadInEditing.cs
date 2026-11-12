using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private static bool IsLeadInEditing(ProjectCommandDto command) => command is SetLeadInGenerationCommandDto
        or PreviewLeadInGenerationCommandDto or ApplyLeadInGenerationCommandDto or DeleteLeadInAndExcludeCommandDto
        or AddOrchestrationMarkCommandDto or SetOrchestrationMarkTextCommandDto or ReorderOrchestrationMarksCommandDto
        or SpaceOrchestrationMarksCommandDto or SetCountdownAppearanceCommandDto or SetMarkAppearanceCommandDto;

    internal static BackendErrorResponse? CheckLeadInAttachmentIntent(SetLeadInAttachmentCommandDto command)
    {
        if (!EditingCommandTypeMatches(command) || !IsStableId(command.BranchId) || !LeadInCueIdentity(command.LeadInId)
            || !IsStableId(command.ContainerId) || command.Mode is not ("FixedParent" or "CueTargetText")
            || command.Target is not null || command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null)
            return LeadInInvalid("附着切换需要明确来源、cue、提示容器及合法mode。");
        return null;
    }

    internal static BackendErrorResponse? CheckLeadInEditingIntent(ProjectCommandDto command)
    {
        if (!IsLeadInEditing(command)) return null;
        try
        {
            if (!EditingCommandTypeMatches(command) || command.Target is not null || command.StartUs is not null
                || command.EndUs is not null || command.AnalysisJobId is not null) return LeadInInvalid("命令type、DTO或继承字段不匹配。");
            // The complete strict JSON path also validates in-process callers and detaches nested arrays.
            if (command is SetMarkAppearanceCommandDto { Changes.Length: > 26 }
                or ApplyLeadInGenerationCommandDto { SelectedLineIds.Length: > 1000 }
                or ReorderOrchestrationMarksCommandDto { OrderedIds.Length: > 64 }) throw new ReadModelBudgetExceededException();
            var settings = command switch { SetLeadInGenerationCommandDto v => v.Settings, PreviewLeadInGenerationCommandDto v => v.Settings,
                ApplyLeadInGenerationCommandDto v => v.Settings, _ => null };
            if (settings?.ExcludedLineIds?.Length > 1000 || settings?.DurationUs?.Length > 64 * 1024
                || settings?.GapThresholdUs?.Length > 64 * 1024) throw new ReadModelBudgetExceededException();
            string? identity = command switch { SetLeadInGenerationCommandDto v => v.BranchId, PreviewLeadInGenerationCommandDto v => v.BranchId,
                ApplyLeadInGenerationCommandDto v => v.BranchId, DeleteLeadInAndExcludeCommandDto v => v.BranchId,
                AddOrchestrationMarkCommandDto v => v.ContainerId, ReorderOrchestrationMarksCommandDto v => v.ContainerId,
                SpaceOrchestrationMarksCommandDto v => v.ContainerId, SetCountdownAppearanceCommandDto v => v.ContainerId,
                SetOrchestrationMarkTextCommandDto v => v.NodeId, SetMarkAppearanceCommandDto v => v.NodeId, _ => null };
            if (!IsStableId(identity) || settings?.IndicatorContainerId is { } indicator && !IsStableId(indicator)
                || settings?.ExcludedLineIds?.Any(id => !IsStableId(id)) == true
                || command is ApplyLeadInGenerationCommandDto apply && apply.SelectedLineIds?.Any(id => !IsStableId(id)) == true
                || command is ReorderOrchestrationMarksCommandDto reorder && reorder.OrderedIds?.Any(id => !IsStableId(id)) == true
                || command is DeleteLeadInAndExcludeCommandDto delete && !LeadInCueIdentity(delete.LeadInId)
                || command is AddOrchestrationMarkCommandDto add && !ValidMarkText(add.Text)
                || command is SetOrchestrationMarkTextCommandDto text && !ValidMarkText(text.Text))
                return LeadInInvalid("前奏身份、文字或生成字段超出合法范围。");
            if (command is SetCountdownAppearanceCommandDto appearance) _ = CoreSceneAppearance(appearance.Appearance);
            if (command is SetMarkAppearanceCommandDto mark) _ = CoreMarkAppearance(mark.Changes);
            using (StyleIntentStream counter = new()) JsonSerializer.Serialize(counter, command, ReadModelJson);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(command, ReadModelJson);
            if (bytes.Length > 64 * 1024) throw new ReadModelBudgetExceededException();
            _ = JsonSerializer.Deserialize<ProjectCommandDto>(bytes, ReadModelJson);
            return null;
        }
        catch (Exception exception) when (exception is ReadModelBudgetExceededException or StyleCommandBudgetExceededException) { return LeadInEditingBudget(); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { return LeadInInvalid("前奏制作命令必须符合严格完整字段格式。"); }
    }

    private static ProjectCommandDto CanonicalLeadInEditingCommand(ProjectCommandDto command)
    {
        ProjectCommandDto detached = JsonSerializer.Deserialize<ProjectCommandDto>(JsonSerializer.SerializeToUtf8Bytes(command, ReadModelJson), ReadModelJson)!;
        LeadInGenerationSettingsDto? Settings(LeadInGenerationSettingsDto? s) => s is null ? null : s with
        { GapThresholdUs = Canonical(s.GapThresholdUs), DurationUs = Canonical(s.DurationUs) };
        static string Canonical(string value) => TryParseMicroseconds(value, out long parsed) ? Us(parsed) : value;
        return detached switch
        {
            SetLeadInGenerationCommandDto v => v with { Settings = Settings(v.Settings) },
            PreviewLeadInGenerationCommandDto v => v with { Settings = Settings(v.Settings) },
            ApplyLeadInGenerationCommandDto v => v with { Settings = Settings(v.Settings) },
            _ => detached,
        };
    }

    private void TryPrepareLeadInEditing(Project before, ProjectCommandDto command, out Project after,
        out OrchestrationLeadInCommandResultDto? result, out LeadInGenerationCommandResultDto? generationResult,
        out BackendErrorResponse? error)
    {
        after = before; result = null; generationResult = null; error = CheckLeadInEditingIntent(command);
        if (error is not null) return;
        try
        {
            GuardNodeCreationTree(before);
            if (PresentationValidator.Validate(before).HasErrors) throw new InvalidNodeCreationTreeException();
            var tree = before.LyricPresentation.WithDefaultPrograms(before.Lyrics);
            Project input = ReferenceEquals(tree, before.LyricPresentation) ? before : before with { LyricPresentation = tree };
            GuardNodeCreationTree(input);
            ValidationReport report = ValidationReport.Empty;
            string operation = command.Type["orchestration.".Length..];
            if (command is SetLeadInGenerationCommandDto or PreviewLeadInGenerationCommandDto or ApplyLeadInGenerationCommandDto
                or DeleteLeadInAndExcludeCommandDto)
            {
                (string? branchId, LeadInGenerationSettingsDto? dto) = command switch
                {
                    SetLeadInGenerationCommandDto c => (c.BranchId, c.Settings),
                    PreviewLeadInGenerationCommandDto c => (c.BranchId, c.Settings),
                    ApplyLeadInGenerationCommandDto c => (c.BranchId, c.Settings),
                    DeleteLeadInAndExcludeCommandDto c => (c.BranchId, null),
                    _ => (null, null),
                };
                if (!IsStableId(branchId)) { error = LeadInInvalid("需要有效branchId。"); return; }
                var program = tree.FindProgram(branchId!);
                var branch = input.Lyrics.Branches.FirstOrDefault(b => b.Id == branchId);
                if (program is null || branch is null) { error = LeadInRejected("来源不存在。", "command.branchId", "SourceMissing"); return; }
                if (command is DeleteLeadInAndExcludeCommandDto delete)
                {
                    if (!LeadInCueIdentity(delete.LeadInId)) { error = LeadInInvalid("前奏身份无效。"); return; }
                    var existing = program.LeadIns.FirstOrDefault(c => c.Id == delete.LeadInId);
                    if (existing is null) { error = LeadInRejected("前奏不存在。", "command.leadInId", "TargetStale"); return; }
                    if (!LeadInEditor.TryDeleteLeadIn(input, program.Id, existing.Id, true, out after, out report))
                    { error = LeadInValidation(report); return; }
                    result = new(operation, branchId, existing.Id, existing.IndicatorContainerId, null, !ReferenceEquals(input, after));
                }
                else
                {
                    LeadInGenerationSettings? settings = null;
                    if (dto is not null)
                    {
                        if (!TryParseMicroseconds(dto.DurationUs, out long duration) || duration <= 0
                            || !TryParseMicroseconds(dto.GapThresholdUs, out long gap) || gap < 0
                            || dto.IndicatorContainerId is not null && !IsStableId(dto.IndicatorContainerId)
                            || dto.ExcludedLineIds is null || dto.ExcludedLineIds.Length > 1000
                            || dto.ExcludedLineIds.Any(id => !IsStableId(id))
                            || dto.ExcludedLineIds.Distinct(StringComparer.Ordinal).Count() != dto.ExcludedLineIds.Length)
                        { error = LeadInInvalid("生成时长、阈值、提示身份或排除列表无效。"); return; }
                        if (dto.ExcludedLineIds.Any(id => !branch.Lines.Any(line => line.Id == id)))
                        { error = LeadInRejected("排除目标不存在。", "command.settings.excludedLineIds", "TargetStale"); return; }
                        settings = new() { IncludeOpening = dto.IncludeOpening, IncludeLongGaps = dto.IncludeLongGaps,
                            DurationUs = duration, GapThresholdUs = gap, IndicatorContainerId = dto.IndicatorContainerId,
                            ExcludedLineIds = [.. dto.ExcludedLineIds] };
                    }
                    LeadInAnalysis? analysis = null;
                    string[] selected = [];
                    if (command is SetLeadInGenerationCommandDto)
                    {
                        if (!PresentationEditor.TrySetTree(input, tree with { Programs = tree.Programs.Replace(program,
                            program with { LeadInGeneration = settings }) }, out after, out report))
                        { error = LeadInValidation(report); return; }
                    }
                    else
                    {
                        if (settings is null) { error = LeadInInvalid("分析和应用需要完整settings。"); return; }
                        if (branch.Lines.Length > 1000) throw new ReadModelBudgetExceededException();
                        analysis = LeadInGeneration.Analyze(input, program.Id, settings);
                        if (command is ApplyLeadInGenerationCommandDto apply)
                        {
                            selected = apply.SelectedLineIds ?? [];
                            if (apply.SelectedLineIds is null || selected.Length > 1000 || selected.Any(id => !IsStableId(id))
                                || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
                            { error = LeadInInvalid("selectedLineIds必须是唯一完整来源行身份列表。"); return; }
                            if (selected.Any(id => !branch.Lines.Any(line => line.Id == id)))
                            { error = LeadInRejected("应用目标不存在。", "command.selectedLineIds", "TargetStale"); return; }
                            if (!LeadInGeneration.TryApply(input, program.Id, settings, selected, out after, out report))
                            { error = LeadInValidation(report); return; }
                        }
                        // A preview always preserves the exact source reference, dirty state and redo.
                        else after = before;
                    }
                    ReadModelBudget budget = new();
                    generationResult = new(operation, branchId!, ProjectGenerationSettings(settings, budget),
                        analysis is null ? [] : [.. analysis.Candidates.Select(c => ProjectGenerationCandidate(input, program, c, budget))],
                        analysis is null ? [] : [.. analysis.Report.Issues.Select(issue =>
                        { budget.Item(); budget.Text(issue.Code.ToString(), issue.Severity.ToString(), issue.Message, issue.Location);
                            return new LeadInGenerationDiagnosticDto(issue.Code.ToString(), issue.Severity.ToString(), issue.Message, issue.Location); })], selected.ToArray(), false);
                }
            }
            else
            {
                string? nodeId = command switch
                {
                    AddOrchestrationMarkCommandDto c => c.ContainerId,
                    ReorderOrchestrationMarksCommandDto c => c.ContainerId,
                    SpaceOrchestrationMarksCommandDto c => c.ContainerId,
                    SetCountdownAppearanceCommandDto c => c.ContainerId,
                    SetOrchestrationMarkTextCommandDto c => c.NodeId,
                    SetMarkAppearanceCommandDto c => c.NodeId,
                    _ => null,
                };
                if (!IsStableId(nodeId)) { error = LeadInInvalid("需要有效节点身份。"); return; }
                var node = tree.FindNode(nodeId!);
                if (node is null) { error = LeadInRejected("节点不存在。", "command", "TargetStale"); return; }
                bool markCommand = command is SetOrchestrationMarkTextCommandDto or SetMarkAppearanceCommandDto;
                if (markCommand ? node.Content is not TextMarkContent : node.Content is not CountdownSequenceContent)
                { error = LeadInRejected("节点内容种类与操作不匹配。", "command", "ContainerInvalid"); return; }
                string? addedMarkId = null;
                bool accepted;
                switch (command)
                {
                    case AddOrchestrationMarkCommandDto add:
                        if (!ValidMarkText(add.Text)) { error = LeadInInvalid("标记文字必须为1..4096字符的非空白字符串。"); return; }
                        if (((CountdownSequenceContent)node.Content!).MarkNodeIds.Length >= 64
                            || tree.Nodes.Length >= OrchestrationNodeCreationLimits.MaxNodes) throw new ReadModelBudgetExceededException();
                        accepted = LeadInEditor.TryAddMark(input, nodeId!, add.Text!, out addedMarkId, out after, out report); break;
                    case SetOrchestrationMarkTextCommandDto text:
                        if (!ValidMarkText(text.Text)) { error = LeadInInvalid("标记文字必须为1..4096字符的非空白字符串。"); return; }
                        accepted = SceneTreeEditor.TrySetNode(input, node with { Content = ((TextMarkContent)node.Content!) with { Text = text.Text! } }, out after, out report); break;
                    case ReorderOrchestrationMarksCommandDto reorder:
                        if (reorder.OrderedIds is null || reorder.OrderedIds.Length > 64 || reorder.OrderedIds.Any(id => !IsStableId(id)))
                        { error = LeadInInvalid("需要完整标记身份排列。"); return; }
                        accepted = LeadInEditor.TryReorderMarks(input, nodeId!, reorder.OrderedIds, out after, out report); break;
                    case SpaceOrchestrationMarksCommandDto:
                        accepted = LeadInEditor.TrySpaceMarks(input, nodeId!, out after, out report); break;
                    case SetCountdownAppearanceCommandDto appearance:
                        var baseline = CoreSceneAppearance(appearance.Appearance);
                        if (!baseline.IsValid) { error = LeadInInvalid("提示基线外观无效。"); return; }
                        accepted = SceneTreeEditor.TrySetNode(input, node with { Content = ((CountdownSequenceContent)node.Content!) with { Appearance = baseline } }, out after, out report); break;
                    case SetMarkAppearanceCommandDto appearance:
                        var owner = tree.Nodes.FirstOrDefault(n => n.Content is CountdownSequenceContent seq && seq.MarkNodeIds.Contains(nodeId!));
                        var overlay = CoreMarkAppearance(appearance.Changes);
                        if (owner?.Content is not CountdownSequenceContent own || !own.Appearance.Resolve(overlay).IsValid)
                        { error = LeadInInvalid("标记覆盖外观无效。"); return; }
                        accepted = SceneTreeEditor.TrySetNode(input, node with { Content = ((TextMarkContent)node.Content!) with { AppearanceOverride = overlay } }, out after, out report); break;
                    default: error = LeadInInvalid("未知前奏制作操作。"); return;
                }
                if (!accepted) { error = LeadInValidation(report); return; }
                string? containerId = markCommand ? node.ParentId : node.Id;
                string[]? marks = after.LyricPresentation.FindNode(containerId!)?.Content is CountdownSequenceContent updated ? updated.MarkNodeIds.ToArray() : null;
                result = new(operation, null, null, containerId, addedMarkId is null ? marks : [addedMarkId], !ReferenceEquals(input, after));
            }
            if (ReferenceEquals(input, after)) after = before;
            bool changed = !ReferenceEquals(before, after);
            if (result is not null) result = result with { Changed = changed };
            if (generationResult is not null) generationResult = generationResult with { Changed = changed };
            GuardNodeCreationTree(after);
            string revision = changed ? Us(checked(ProjectRevision + 1)) : RevisionText;
            _ = BuildOrchestration(after, revision);
            ProjectChangeSetDto changes = changed ? new(["orchestration", "renderInputs"], [], [], [], true) : new([], [], [], [], false);
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText, revision, changed, false, false, false,
                changed ? command.Type : Workspace.UndoName, changed ? null : Workspace.RedoName, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture),
                LeadInResult: result, LeadInGenerationResult: generationResult);
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper), ReadModelJson).Length > _branchReceiptMaxBytes)
                throw new ReadModelBudgetExceededException();
        }
        catch (ReadModelBudgetExceededException) { after = before; error = LeadInEditingBudget(); }
        catch (InvalidNodeCreationTreeException) { after = before; error = LeadInRejected("源展示树无效。", "command", "PresentationInvalid"); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { after = before; error = LeadInInvalid("前奏制作字段或外观值无效。"); }
    }

    private static bool ValidMarkText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 4096;
    private static bool LeadInCueIdentity(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 4096;
    private static string Us(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static BackendErrorResponse LeadInEditingBudget() => new(BackendErrorCode.ResourceTooLarge,
        "前奏意图、最多1000分析行、源或候选树、完整读模型、分析报告或回执超过预算；工程未改变。", "command");

    private static LeadInGenerationSettingsDto? ProjectGenerationSettings(LeadInGenerationSettings? value, ReadModelBudget budget)
    {
        if (value is null) return null;
        budget.Item(value.ExcludedLineIds.Length + 1); budget.Text(value.IndicatorContainerId);
        foreach (string id in value.ExcludedLineIds) budget.Text(id);
        return new(value.IncludeOpening, value.IncludeLongGaps, Us(value.GapThresholdUs), Us(value.DurationUs), value.IndicatorContainerId, value.ExcludedLineIds.ToArray());
    }
    private static LeadInGenerationCandidateDto ProjectGenerationCandidate(Project project, LyricDisplayProgram program, LeadInCandidate candidate, ReadModelBudget budget)
    {
        budget.Item(); budget.Text(candidate.TargetLineId);
        return new(candidate.TargetLineId, candidate.Status.ToString(), candidate.Reason.ToString(),
            candidate.Existing is null ? null : ProjectLeadIn(project, program, candidate.Existing, budget),
            candidate.Proposed is null ? null : ProjectLeadIn(project, program, candidate.Proposed, budget),
            candidate.Range is { } range ? Us(range.StartUs) : null, candidate.Range is { } end ? Us(end.EndUs) : null,
            candidate.Anchor is { } anchor ? new(Us(anchor.TimeUs), anchor.TimingStatus.ToString(), anchor.UsesLineTime, anchor.HasMissingPrefix) : null);
    }
}
