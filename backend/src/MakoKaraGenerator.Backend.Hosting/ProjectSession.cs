using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>独立后端内的单写者工程会话；所有工程命令都经过同一个写入闸门。</summary>
public sealed partial class ProjectSession
{
    internal ProjectSession(string id, string hostInstanceId, string ownerClientId, ProjectWorkspace workspace,
        string audioAssetId, long audioDurationUs, TimeProvider timeProvider,
        ProjectCommandRetentionOptions commandRetention, ClientEventJournal events, IDisposable? resourceReference = null)
    {
        ProjectSessionId = id;
        HostInstanceId = hostInstanceId;
        OwnerClientId = ownerClientId;
        Workspace = workspace;
        AudioAssetId = audioAssetId;
        AudioDurationUs = audioDurationUs;
        PreparedAudioSourcePath = workspace.Project.AudioPath;
        _timeProvider = timeProvider;
        _events = events;
        _resourceReference = resourceReference;
        _branchReceiptMaxBytes = commandRetention.MaxReceiptBytes;
    }

    /// <summary>不透明的工程会话 ID。</summary>
    public string ProjectSessionId { get; }
    /// <summary>创建当前会话的服务进程身份。</summary>
    public string HostInstanceId { get; }
    /// <summary>有权访问当前会话的客户端身份。</summary>
    public string OwnerClientId { get; }
    /// <summary>固定的托管音频资源身份。</summary>
    public string AudioAssetId { get; private set; }
    /// <summary>已准备音频内容的整数微秒时长。</summary>
    public long AudioDurationUs { get; private set; }
    internal ProjectWorkspace Workspace { get; }
    internal string? PreparedAudioSourcePath { get; private set; }
    internal SemaphoreSlim Writer { get; } = new(1, 1);
    internal long ProjectRevision { get; private set; }
    private readonly TimeProvider _timeProvider;
    private readonly ClientEventJournal _events;
    private IDisposable? _resourceReference;
    internal bool IsClosing { get; set; }
    internal bool IsClosed { get; set; }
    internal bool IsReadOnly { get; set; }
    internal bool MutationBlocked => IsClosing || IsClosed || ClosePending;

    internal void DisposeResources()
    {
        try { DisposeBackgroundHistory(); DisposeAudioHistory(); Interlocked.Exchange(ref _waveform, null)?.Dispose(); }
        finally { Interlocked.Exchange(ref _resourceReference, null)?.Dispose(); }
    }
    internal ValueTask DisposeResourcesAsync() { DisposeResources(); return ValueTask.CompletedTask; }
    internal string PublishEvent(string type, ProjectChangeSetDto? changes = null) =>
        _events.Append(OwnerClientId, type, BuildSummary(), changes);

    internal async Task<ProjectSessionSummaryResponse?> ReadSummaryAsync(CancellationToken cancellationToken)
    {
        await Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsClosed) return null;
            return BuildSummary();
        }
        finally { Writer.Release(); }
    }

    internal ProjectSessionSummaryResponse BuildSummary() => new(
        ProjectSessionId,
        HostInstanceId,
        ProjectRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Workspace.Project.Name,
        AudioAssetId,
        Workspace.Project.Lyrics.Lines.Length,
        AudioDurationUs,
        Workspace.IsDirty,
        Workspace.CanUndo,
        Workspace.CanRedo,
        EventCursor: _events.Cursor(OwnerClientId),
        HasPath: Workspace.HasPath,
        SavedProjectRevision: SavedRevision?.ToString(CultureInfo.InvariantCulture),
        IsReadOnly: IsReadOnly,
        AudioAnalysis: SummarizeAcceptedAnalysis(Workspace.Project.AudioAnalysis));

    private static ProjectAudioAnalysisSummaryDto? SummarizeAcceptedAnalysis(ProjectAudioAnalysis? analysis)
    {
        if (analysis is null) return null;
        if (analysis.Data is not { } data) return new(analysis.MissingStream ? "noStream" : "failed");
        return new("available", AudioAnalysisProfile.AlgorithmVersion, AudioAnalysisProfile.SampleRate,
            AudioAnalysisProfile.HopSamples, "0", data.SourceHash, data.PcmHash,
            data.SampleCount.ToString(CultureInfo.InvariantCulture), data.DurationUs.ToString(CultureInfo.InvariantCulture),
            data.Revision, data.ContentHash, data.Points.Length);
    }

    /// <summary>在当前会话写者闸门内执行一个具名命令。</summary>
    internal async Task<ProjectCommandExecutionResult> ExecuteCommandAsync(
        ProjectCommandRequest? request, CancellationToken cancellationToken,
        Func<ProjectSession, string, BackendOperationResult<ProjectAudioAnalysis>>? prepareAnalysis = null,
        Func<ProjectCommandDto, StyleTemplate?>? resolveTemplate = null)
    {
        await Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request is null)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest, "命令请求不能为空。"));
            if (MutationBlocked)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
            if (IsReadOnly && request.Command is not PreviewLeadInGenerationCommandDto)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。"));
            static ProjectCommandExecutionResult Finish(ProjectCommandExecutionResult result) => result;

            if (!TryParseRevision(request.ExpectedRevision, out long expectedRevision))
                return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
                    "expectedRevision 必须是非负十进制修订。", nameof(request.ExpectedRevision))));
            if (expectedRevision != ProjectRevision)
                return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.RevisionConflict,
                    $"工程修订已是 {ProjectRevision}，请先读取当前状态。", nameof(request.ExpectedRevision))));
            if (request.Command is null || string.IsNullOrWhiteSpace(request.Command.Type)
                || request.Command.Type.Length > 128)
                return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
                    "必须提供具名命令。", nameof(request.Command))));
            if (!EditingCommandTypeMatches(request.Command))
                return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
                    "命令 type 与其 DTO 类型不一致。", nameof(request.Command))));

            Project before = Workspace.Project;
            Project after;
            ProjectTimingChangeSet changes;
            LyricTimingCommandResultDto? timingResult = null;
            LyricContentCommandFeedbackDto? feedback = null;
            LyricBranchCommandResultDto? branchResult = null;
            LyricTokenCommandResultDto? tokenResult = null;
            RubySegmentCommandResultDto? rubyResult = null;
            StyleCommandResultDto? styleResult = null;
            PresetApplicationCommandResultDto? presetApplicationResult = null;
            OrchestrationCommandResultDto? orchestrationResult = null;
            OrchestrationLayoutCommandResultDto? layoutResult = null;
            OrchestrationStructureCommandResultDto? structureResult = null;
            BasicLayoutCommandResultDto? basicLayoutResult = null;
            OrchestrationClipCommandResultDto? clipResult = null;
            OrchestrationClipMembersCommandResultDto? clipMembersResult = null;
            OrchestrationLeadInCommandResultDto? leadInResult = null;
            LeadInGenerationCommandResultDto? leadInGenerationResult = null;
            LineLocalTransformCommandResultDto? lineLocalTransformResult = null;
            OrchestrationPropertiesCommandResultDto? propertiesResult = null;
            ProjectChangeSetDto? contentChangeSet = null;
            switch (request.Command.Type)
            {
                case "orchestration.setClipMembers":
                case "orchestration.splitClip":
                case "orchestration.mergeClips":
                    if (!TryPrepareClipMembers(before, request.Command, out after, out clipMembersResult, out BackendErrorResponse? clipMembersError))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandNotSupported, "未开放此具名命令。", "command")));
                    if (clipMembersError is not null) return Finish(ProjectCommandExecutionResult.Rejected(clipMembersError));
                    if (MutationBlocked) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。")));
                    if (!ReferenceEquals(before, after))
                    {
                        Workspace.History.BreakMergeGroup();
                        Project clipMembersCandidate = after;
                        after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => clipMembersCandidate));
                        Workspace.History.BreakMergeGroup();
                        contentChangeSet = new(["orchestration", "presetApplications", "renderInputs"], [], [], [], true);
                        changes = ProjectTimingChangeSet.FullRefresh;
                    }
                    else { contentChangeSet = new([], [], [], [], false); changes = ProjectTimingChangeSet.None; }
                    break;
                case "lyrics.setTimeRange":
                    if (request.Command.AnalysisJobId is not null || request.Command.Target is not { } dto || !IsStableId(dto.BranchId)
                        || !IsStableId(dto.LineId) || (dto.TokenId is not null && !IsStableId(dto.TokenId))
                        || !TryParseMicroseconds(request.Command.StartUs, out long startUs)
                        || !TryParseMicroseconds(request.Command.EndUs, out long endUs))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
                            "lyrics.setTimeRange 需要稳定目标以及十进制 startUs/endUs。", nameof(request.Command))));
                    LyricTimingEditTarget target = new(dto.BranchId, dto.LineId, dto.TokenId);
                    if (!LyricTimingEditUseCase.TryReadTime(before, target, out TimeRange? expectedTime))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected,
                            "歌词时间目标不存在。", nameof(request.Command.Target))));
                    LyricTimingEditResult timing = LyricTimingEditUseCase.Commit(Workspace,
                        new LyricTimingEditRequest(Workspace, Workspace.Generation, target, expectedTime, startUs, endUs,
                            AudioDurationUs));
                    if (!timing.Accepted)
                        return Finish(ProjectCommandExecutionResult.Rejected(MapTimingError(timing.Error)));
                    after = timing.Project;
                    changes = timing.ChangeSet;
                    break;

                case "audio.analysis.apply":
                    if (!IsStableId(request.Command.AnalysisJobId) || HasTimingFields(request.Command))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
                            "audio.analysis.apply只接受analysisJobId。", nameof(request.Command))));
                    ProjectAudioAnalysisEditBaseline baseline = ProjectAudioAnalysisEditUseCase.Capture(Workspace);
                    BackendOperationResult<ProjectAudioAnalysis> prepared = prepareAnalysis?.Invoke(this, request.Command.AnalysisJobId!)
                        ?? BackendOperationResult<ProjectAudioAnalysis>.Rejected(new(BackendErrorCode.JobNotFound, "分析任务不可用。"));
                    if (!prepared.Accepted) return Finish(ProjectCommandExecutionResult.Rejected(prepared.Error!));
                    // All file reads and fallible lease cleanup have completed before this final synchronous mutation gate.
                    if (MutationBlocked) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。")));
                    if (IsReadOnly) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。")));
                    if (ProjectRevision != expectedRevision) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.RevisionConflict, "工程修订已经改变。")));
                    ProjectAudioAnalysisEditResult adopted = ProjectAudioAnalysisEditUseCase.AdoptPrepared(Workspace, baseline, prepared.Response!);
                    if (!adopted.Accepted) return Finish(RejectedAnalysisEdit(adopted));
                    after = adopted.Project;
                    changes = LyricTimingEditUseCase.DescribeChanges(before, after);
                    break;

                case "audio.analysis.clear":
                    if (HasTimingFields(request.Command) || request.Command.AnalysisJobId is not null)
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
                            "audio.analysis.clear不能携带分析任务或歌词时间字段。", nameof(request.Command))));
                    ProjectAudioAnalysisEditResult cleared = ProjectAudioAnalysisEditUseCase.Clear(Workspace,
                        ProjectAudioAnalysisEditUseCase.Capture(Workspace));
                    if (!cleared.Accepted) return Finish(RejectedAnalysisEdit(cleared));
                    after = cleared.Project;
                    changes = LyricTimingEditUseCase.DescribeChanges(before, after);
                    break;

                case "orchestration.setDefaultContainer":
                    if (request.Command is not SetOrchestrationDefaultContainerCommandDto orchestration
                        || !IsStableId(orchestration.BranchId) || !IsStableId(orchestration.ContainerId))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest, "orchestration.setDefaultContainer需要非空branchId和containerId。", "command")));
                    var orchestrationTree = before.LyricPresentation.WithDefaultPrograms(before.Lyrics);
                    var program = orchestrationTree.FindProgram(orchestration.BranchId!);
                    var container = orchestrationTree.FindNode(orchestration.ContainerId!);
                    if (program is null) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "来源程序不存在。", "command.branchId")));
                    if (container is null || container.Layout is null) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "默认容器必须是当前工程中存在的歌词容器。", "command.containerId")));
                    if (program.DefaultContainerId == container.Id) { after = before; orchestrationResult = new(orchestration.BranchId!, container.Id, false); contentChangeSet = new([], [], [], [], false); changes = ProjectTimingChangeSet.None; break; }
                    var updatedTree = orchestrationTree with { Programs = orchestrationTree.Programs.Replace(program, program with { DefaultContainerId = container.Id }) };
                    if (!PresentationEditor.TrySetTree(before, updatedTree, out after, out _)) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected,
                        "编排默认容器更新未通过工程校验。", "command.containerId", FailureCode: "PresentationInvalid")));
                    Workspace.History.BreakMergeGroup();
                    // Record exactly the validated candidate. This keeps the history entry's
                    // After reference identical to the command result used by undo/redo.
                    Project validated = after;
                    after = Workspace.Execute(ProjectCommand.Create("orchestration.setDefaultContainer", _ => validated));
                    Workspace.History.BreakMergeGroup();
                    orchestrationResult = new(orchestration.BranchId!, container.Id, true); contentChangeSet = new(["orchestration", "renderInputs"], [], [], [], true); changes = ProjectTimingChangeSet.FullRefresh; break;

                case "orchestration.setLayoutDefaultPlacement":
                    if (request.Command is not SetOrchestrationLayoutDefaultPlacementCommandDto layoutPlacement
                        || !IsStableId(layoutPlacement.ContainerId)
                        || layoutPlacement.Placement is { } requestedPlacement && !TryReadPlacement(requestedPlacement, out _))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
                            "orchestration.setLayoutDefaultPlacement需要有效containerId和placement。", "command")));
                    var layoutTree = before.LyricPresentation.WithDefaultPrograms(before.Lyrics);
                    var layoutNode = layoutTree.FindNode(layoutPlacement.ContainerId!);
                    if (layoutNode?.Layout is null)
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected,
                            "目标节点不是歌词内部布局容器。", "command.containerId", FailureCode: "TargetStale")));
                    ScenePlacement? nextPlacement = null;
                    if (layoutPlacement.Placement is { } placementDto)
                    {
                        if (!TryReadPlacement(placementDto, out nextPlacement))
                            return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
                                "placement数值必须有限且scale大于0，anchor必须为公开枚举。", "command.placement")));
                    }
                    if (layoutNode.Layout.DefaultPlacement == nextPlacement)
                    {
                        after = before; layoutResult = new(layoutNode.Id, false);
                        contentChangeSet = new([], [], [], [], false); changes = ProjectTimingChangeSet.None; break;
                    }
                    if (!SceneTreeEditor.TrySetLayoutDefaultPlacement(before, layoutNode.Id, nextPlacement, out after, out _))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected,
                            "内部布局默认位置未通过工程校验。", "command.placement", FailureCode: "PresentationInvalid")));
                    Workspace.History.BreakMergeGroup();
                    Project layoutValidated = after;
                    after = Workspace.Execute(ProjectCommand.Create("orchestration.setLayoutDefaultPlacement", _ => layoutValidated));
                    Workspace.History.BreakMergeGroup();
                    layoutResult = new(layoutNode.Id, true);
                    contentChangeSet = new(["orchestration", "renderInputs"], [], [], [], true);
                    changes = ProjectTimingChangeSet.FullRefresh; break;

                case "orchestration.previewBasicLayout":
                case "orchestration.applyBasicLayout":
                    if (request.Command is not BasicLayoutCommandDto layout || !IsStableId(layout.BranchId)
                        || string.IsNullOrWhiteSpace(layout.Preset) || string.IsNullOrWhiteSpace(layout.LayoutName)
                        || layout.SlotNames is not { Length: >= 1 and <= 3 } slotNames
                        || slotNames.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(layout.SelectedSegmentName)
                        || string.IsNullOrWhiteSpace(layout.RetainedSegmentName))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest, "快速布局命令字段无效。", "command")));
                    if (!Enum.TryParse(layout.Preset, true, out BasicLayoutPreset preset) || !Enum.IsDefined(preset))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected, "快速布局预设不存在。", "command.preset", FailureCode: BasicLayoutErrorCode.InvalidPreset.ToString())));
                    int expectedSlotCount = BasicLayoutPresets.Placements(preset).Length;
                    if (slotNames.Length != expectedSlotCount)
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
                            $"当前快速布局预设必须提供 {expectedSlotCount} 个槽位名称。", "command.slotNames",
                            FailureCode: BasicLayoutErrorCode.InvalidRange.ToString())));
                    BasicLayoutNames names = new(layout.LayoutName!, ImmutableArray.CreateRange(slotNames), layout.SelectedSegmentName!, layout.RetainedSegmentName!);
                    BasicLayoutPreview layoutPreview;
                    BasicLayoutError? layoutError;
                    try
                    {
                        if (!BasicLayoutOperation.TryPreview(before, layout.BranchId!, preset, layout.SelectedLineIds, names, out layoutPreview, out layoutError))
                            return Finish(ProjectCommandExecutionResult.Rejected(MapBasicLayoutError(layoutError)));
                    }
                    catch (ArgumentException exception)
                    {
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest, exception.Message, "command.slotNames", FailureCode: BasicLayoutErrorCode.InvalidRange.ToString())));
                    }
                    string scope = layout.SelectedLineIds is null ? "source" : "range";
                    string? recognized = BasicLayoutOperation.Recognize(layoutPreview.Result.LyricPresentation.WithDefaultPrograms(layoutPreview.Result.Lyrics),
                        layoutPreview.Result.LyricPresentation.WithDefaultPrograms(layoutPreview.Result.Lyrics).FindProgram(layout.BranchId!)?.DefaultContainerId ?? "")?.ToString();
                    basicLayoutResult = new(layout.BranchId!, preset.ToString(), scope, recognized, layoutPreview.AffectedLines,
                        layoutPreview.ExistingClips, layoutPreview.PreservedNodes, layoutPreview.SplitClips, false,
                        layoutPreview.NewClipIds);
                    if (layout.Preview)
                    {
                        after = before; changes = ProjectTimingChangeSet.None; contentChangeSet = new([], [], [], [], false); break;
                    }
                    if (ReferenceEquals(layoutPreview.Result, before))
                    {
                        after = before; changes = ProjectTimingChangeSet.None; contentChangeSet = new([], [], [], [], false); break;
                    }
                    Workspace.History.BreakMergeGroup();
                    after = Workspace.Execute(ProjectCommand.Create("orchestration.applyBasicLayout", _ => layoutPreview.Result));
                    Workspace.History.BreakMergeGroup();
                    basicLayoutResult = basicLayoutResult with { Changed = true };
                    changes = ProjectTimingChangeSet.FullRefresh;
                    contentChangeSet = new(["orchestration", "renderInputs"], [], [], [], true);
                    break;

                case "orchestration.createNode":
                case "orchestration.copySubtree":
                    TryPrepareNodeCreation(before, request.Command, out after, out structureResult, out BackendErrorResponse? nodeCreationError);
                    if (nodeCreationError is not null) return Finish(ProjectCommandExecutionResult.Rejected(nodeCreationError));
                    if (MutationBlocked) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。")));
                    Workspace.History.BreakMergeGroup();
                    Project nodesValidated = after;
                    after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => nodesValidated));
                    Workspace.History.BreakMergeGroup();
                    contentChangeSet = new(["orchestration", "renderInputs"], [], [], [], true);
                    changes = ProjectTimingChangeSet.FullRefresh;
                    break;

                case "orchestration.setNode":
                case "orchestration.reparent":
                case "orchestration.reorderChildren":
                case "orchestration.addSlot":
                case "orchestration.removeNode":
                case "orchestration.reorderSlots":
                    if (!TryExecuteStructureCommand(before, request.Command, out after, out contentChangeSet,
                        out structureResult, out BackendErrorResponse? structureError))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandNotSupported,
                            "未开放此具名命令。", nameof(request.Command.Type))));
                    if (structureError is not null) return Finish(ProjectCommandExecutionResult.Rejected(structureError));
                    if (!ReferenceEquals(before, after))
                    {
                        Workspace.History.BreakMergeGroup();
                        Project structureValidated = after;
                        after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => structureValidated));
                        Workspace.History.BreakMergeGroup();
                        contentChangeSet = new(["orchestration", "renderInputs"], [], [], [], true);
                        changes = ProjectTimingChangeSet.FullRefresh;
                    }
                    else changes = ProjectTimingChangeSet.None;
                    break;

                case "orchestration.setNodeGeometry":
                case "orchestration.setLayoutSettings":
                case "orchestration.setSlotAnimation":
                case "orchestration.setProgramAnimationPolicy":
                    TryPrepareOrchestrationProperties(before, request.Command, out after, out propertiesResult, out BackendErrorResponse? propertiesError);
                    if (propertiesError is not null) return Finish(ProjectCommandExecutionResult.Rejected(propertiesError));
                    if (!ReferenceEquals(before, after))
                    {
                        if (MutationBlocked) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。")));
                        Workspace.History.BreakMergeGroup();
                        Project propertiesValidated = after;
                        after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => propertiesValidated));
                        Workspace.History.BreakMergeGroup();
                        contentChangeSet = new(["orchestration", "renderInputs"], [], [], [], true);
                        changes = ProjectTimingChangeSet.FullRefresh;
                    }
                    else { contentChangeSet = new([], [], [], [], false); changes = ProjectTimingChangeSet.None; }
                    break;

                case "orchestration.setLayoutKind":
                case "orchestration.setLineEnterAnimation":
                case "orchestration.setProjectEnterAnimation":
                    TryPrepareLayoutAndLineAnimation(before, request.Command, out after, out propertiesResult, out BackendErrorResponse? completionError);
                    if (completionError is not null) return Finish(ProjectCommandExecutionResult.Rejected(completionError));
                    if (!ReferenceEquals(before, after))
                    {
                        if (MutationBlocked) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。")));
                        Workspace.History.BreakMergeGroup();
                        Project completionValidated = after;
                        after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => completionValidated));
                        Workspace.History.BreakMergeGroup();
                        contentChangeSet = new(["orchestration", "renderInputs"], [], [], [], true);
                        changes = ProjectTimingChangeSet.FullRefresh;
                    }
                    else { contentChangeSet = new([], [], [], [], false); changes = ProjectTimingChangeSet.None; }
                    break;

                case "orchestration.setLeadInGeneration":
                case "orchestration.previewLeadInGeneration":
                case "orchestration.applyLeadInGeneration":
                case "orchestration.deleteLeadInAndExclude":
                case "orchestration.addMark":
                case "orchestration.setMarkText":
                case "orchestration.reorderMarks":
                case "orchestration.spaceMarks":
                case "orchestration.setCountdownAppearance":
                case "orchestration.setCountdownPresentation":
                case "orchestration.setMarkAppearance":
                    TryPrepareLeadInEditing(before, request.Command, out after, out leadInResult,
                        out leadInGenerationResult, out BackendErrorResponse? leadInEditingError);
                    if (leadInEditingError is not null) return Finish(ProjectCommandExecutionResult.Rejected(leadInEditingError));
                    if (!ReferenceEquals(before, after))
                    {
                        if (MutationBlocked) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。")));
                        Workspace.History.BreakMergeGroup();
                        Project leadInEditingValidated = after;
                        after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => leadInEditingValidated));
                        Workspace.History.BreakMergeGroup();
                        contentChangeSet = new(["orchestration", "renderInputs"], [], [], [], true);
                        changes = ProjectTimingChangeSet.FullRefresh;
                    }
                    else { contentChangeSet = new([], [], [], [], false); changes = ProjectTimingChangeSet.None; }
                    break;

                case "orchestration.createCountdown":
                case "orchestration.createLeadIn":
                case "orchestration.updateLeadIn":
                case "orchestration.deleteLeadIn":
                    if (!TryPrepareLeadInCommand(before, request.Command, out after, out contentChangeSet,
                        out leadInResult, out BackendErrorResponse? leadInError))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandNotSupported, "未开放此具名命令。", "command.type")));
                    if (leadInError is not null) return Finish(ProjectCommandExecutionResult.Rejected(leadInError));
                    if (!ReferenceEquals(before, after))
                    {
                        if (MutationBlocked) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。")));
                        Workspace.History.BreakMergeGroup();
                        Project leadInValidated = after;
                        after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => leadInValidated));
                        Workspace.History.BreakMergeGroup();
                        changes = ProjectTimingChangeSet.FullRefresh;
                    }
                    else changes = ProjectTimingChangeSet.None;
                    break;

                case "orchestration.setLineLocalTransform":
                    if (!TryExecuteLineLocalTransformCommand(before, request.Command, out after,
                        out lineLocalTransformResult, out BackendErrorResponse? localTransformError))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandNotSupported,
                            "未开放此具名命令。", nameof(request.Command.Type))));
                    if (localTransformError is not null) return Finish(ProjectCommandExecutionResult.Rejected(localTransformError));
                    if (!ReferenceEquals(before, after))
                    {
                        if (MutationBlocked) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionClosing, "工程会话正在关闭。")));
                        Workspace.History.BreakMergeGroup();
                        Project localValidated = after;
                        after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => localValidated));
                        Workspace.History.BreakMergeGroup();
                        contentChangeSet = new(["orchestration", "lineLocalTransform", "renderInputs"], [], [], [], true);
                        changes = ProjectTimingChangeSet.FullRefresh;
                    }
                    else
                    {
                        contentChangeSet = new([], [], [], [], false);
                        changes = ProjectTimingChangeSet.None;
                    }
                    break;

                case "orchestration.createClip":
                case "orchestration.renameClip":
                case "orchestration.setClipContainer":
                case "orchestration.deleteClip":
                    if (!TryExecuteClipCommand(before, request.Command, out after, out contentChangeSet,
                        out clipResult, out BackendErrorResponse? clipError))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandNotSupported,
                            "未开放此具名命令。", nameof(request.Command.Type))));
                    if (clipError is not null) return Finish(ProjectCommandExecutionResult.Rejected(clipError));
                    if (!ReferenceEquals(before, after))
                    {
                        Workspace.History.BreakMergeGroup();
                        Project clipValidated = after;
                        after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => clipValidated));
                        Workspace.History.BreakMergeGroup();
                        contentChangeSet = new(["orchestration", "renderInputs"], [], [], [], true);
                        changes = ProjectTimingChangeSet.FullRefresh;
                    }
                    else changes = ProjectTimingChangeSet.None;
                    break;

                case "presetApplications.apply":
                case "presetApplications.update":
                case "presetApplications.setEnabled":
                case "presetApplications.remove":
                    if (resolveTemplate is null) return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.TaskUnavailable, "模板库当前不可用。", "command")));
                    Project? assignment = ExecutePresetApplicationCommand(before, request.Command, resolveTemplate, out BackendErrorResponse? assignmentError);
                    if (assignmentError is not null) return Finish(ProjectCommandExecutionResult.Rejected(assignmentError));
                    after = assignment!; changes = LyricTimingEditUseCase.DescribeChanges(before, after); break;

                case "history.undo":
                    if (HasTimingFields(request.Command) || request.Command.AnalysisJobId is not null)
                        return Finish(UnexpectedHistoryFields());
                    if (!Workspace.CanUndo)
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected,
                            "当前没有可撤销的工程命令。", nameof(request.Command.Type))));
                    after = Workspace.Undo();
                    changes = LyricTimingEditUseCase.DescribeChanges(before, after);
                    break;

                case "history.redo":
                    if (HasTimingFields(request.Command) || request.Command.AnalysisJobId is not null)
                        return Finish(UnexpectedHistoryFields());
                    if (!Workspace.CanRedo)
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected,
                            "当前没有可重做的工程命令。", nameof(request.Command.Type))));
                    after = Workspace.Redo();
                    changes = LyricTimingEditUseCase.DescribeChanges(before, after);
                    break;

                default:
                    if (TryExecuteLyricRefactorCommand(before, request.Command, out after, out contentChangeSet, out timingResult, out feedback, out rubyResult, out BackendErrorResponse? refactorError))
                    {
                        if (refactorError is not null) return Finish(ProjectCommandExecutionResult.Rejected(refactorError));
                        changes = ProjectTimingChangeSet.None;
                        break;
                    }
                    if (TryExecuteRubyCommand(before, request.Command, out after, out contentChangeSet, out rubyResult, out BackendErrorResponse? rubyError))
                    {
                        if (rubyError is not null) return Finish(ProjectCommandExecutionResult.Rejected(rubyError));
                        changes = ProjectTimingChangeSet.None;
                        break;
                    }
                    if (TryPrepareBasicProjectCommand(before, request.Command, out after, out contentChangeSet, out BackendErrorResponse? basicProjectError))
                    {
                        if (basicProjectError is not null) return Finish(ProjectCommandExecutionResult.Rejected(basicProjectError));
                        if (!ReferenceEquals(before, after))
                        {
                            Project basicProjectCandidate = after;
                            Workspace.History.BreakMergeGroup();
                            after = Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => basicProjectCandidate));
                            Workspace.History.BreakMergeGroup();
                        }
                        changes = ProjectTimingChangeSet.None;
                        break;
                    }
                    if (TryExecuteStyleCommand(before, request.Command, out after, out contentChangeSet, out styleResult, out BackendErrorResponse? styleError))
                    {
                        if (styleError is not null) return Finish(ProjectCommandExecutionResult.Rejected(styleError));
                        changes = ProjectTimingChangeSet.None;
                        break;
                    }
                    if (TryExecuteContentCommand(before, request.Command, out after, out contentChangeSet, out feedback, out BackendErrorResponse? contentError))
                    {
                        if (contentError is not null) return Finish(ProjectCommandExecutionResult.Rejected(contentError));
                        changes = ProjectTimingChangeSet.None;
                        break;
                    }
                    if (TryExecuteTokenCommand(before, request.Command, out after, out contentChangeSet, out tokenResult, out BackendErrorResponse? tokenError))
                    {
                        if (tokenError is not null) return Finish(ProjectCommandExecutionResult.Rejected(tokenError));
                        changes = ProjectTimingChangeSet.None;
                        break;
                    }
                    bool branchHandled = TryExecuteBranchCommand(before, request.Command, out after, out changes, out branchResult, out BackendErrorResponse? editingError);
                    if (!branchHandled && !TryExecuteEditingCommand(before, request.Command, out after, out changes, out timingResult, out editingError))
                        return Finish(ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandNotSupported,
                            "未开放此具名命令。", nameof(request.Command.Type))));
                    if (editingError is not null)
                        return Finish(ProjectCommandExecutionResult.Rejected(editingError));
                    break;
            }

            bool changed = !ReferenceEquals(before, after);
            SynchronizeAudioState();
            long previousRevision = ProjectRevision;
            if (changed)
            {
                ProjectRevision = checked(ProjectRevision + 1);
            }
            if (IsPresetApplicationCommand(request.Command))
                presetApplicationResult = PresetApplicationResult(request.Command, before, after);
            ProjectChangeSetDto changeSet = contentChangeSet ??
                (before.LyricPresentation != after.LyricPresentation
                    ? new(["orchestration", "renderInputs"], [], [], [], true)
                    : !before.PresetApplications.Equals(after.PresetApplications)
                        ? new(["presetApplications", "renderInputs"], [], [], [], false)
                        : ToChangeSet(changes));
            if (request.Command.Type is "history.undo" or "history.redo")
            {
                changeSet = IncludeBasicMediaChanges(before, after, changeSet);
                changeSet = IncludeRubyHistoryChanges(before, after, changeSet);
                rubyResult = RubyHistoryFeedback(before, after, changeSet);
            }
            string cursor = changed ? PublishEvent("project.changed", changeSet) : _events.Cursor(OwnerClientId);
            return Finish(ProjectCommandExecutionResult.FromResponse(new ProjectCommandResponse(
                ProjectSessionId,
                previousRevision.ToString(CultureInfo.InvariantCulture),
                ProjectRevision.ToString(CultureInfo.InvariantCulture),
                changed,
                Workspace.IsDirty,
                Workspace.CanUndo,
                Workspace.CanRedo,
                Workspace.UndoName,
                Workspace.RedoName,
                changeSet,
                EventCursor: cursor,
                TimingResult: timingResult,
                Feedback: feedback,
                BranchResult: branchResult,
                TokenResult: tokenResult,
                StyleResult: styleResult,
                PresetApplicationResult: presetApplicationResult,
                OrchestrationResult: orchestrationResult,
                LayoutResult: layoutResult,
                StructureResult: structureResult,
                BasicLayoutResult: basicLayoutResult,
                ClipResult: clipResult,
                ClipMembersResult: clipMembersResult,
                LeadInResult: leadInResult,
                LineLocalTransformResult: lineLocalTransformResult,
                PropertiesResult: propertiesResult,
                LeadInGenerationResult: leadInGenerationResult,
                RubyResult: rubyResult)));
        }
        finally { Writer.Release(); }
    }

    private static bool TryReadPlacement(ProjectOrchestrationPlacementDto value, out ScenePlacement? placement)
    {
        placement = null;
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.RotationDegrees)
            || !double.IsFinite(value.Scale) || value.Scale <= 0d
            || !Enum.GetNames<ScenePlacementAnchor>().Contains(value.Anchor, StringComparer.OrdinalIgnoreCase)
            || !Enum.TryParse<ScenePlacementAnchor>(value.Anchor, true, out ScenePlacementAnchor anchor)
            || !Enum.IsDefined(anchor)) return false;
        try
        {
            placement = ScenePlacement.Create(new NormalizedPosition(value.X, value.Y), anchor, value.RotationDegrees, value.Scale);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool IsPresetApplicationCommand(ProjectCommandDto command) => command.Type is
        "presetApplications.apply" or "presetApplications.update" or "presetApplications.setEnabled" or "presetApplications.remove";

    private static PresetApplicationCommandResultDto? PresetApplicationResult(ProjectCommandDto command, Project before, Project after)
    {
        string[] ids = command switch
        {
            ApplyPresetApplicationCommandDto apply when apply.Targets is { Length: > 0 } => [.. ToBindings(after, apply.Targets)
                .Select(target => after.PresetApplications.Find(target)?.Id)
                .Where(id => id is not null).Select(id => id!)],
            UpdatePresetApplicationCommandDto update when update.ApplicationId is not null => [update.ApplicationId],
            SetPresetApplicationEnabledCommandDto enabled when enabled.ApplicationId is not null => [enabled.ApplicationId],
            RemovePresetApplicationCommandDto remove when remove.ApplicationIds is not null => [.. remove.ApplicationIds
                .Where(id => before.PresetApplications.Items.Any(item => item.Id == id)
                    && after.PresetApplications.Items.All(item => item.Id != id))],
            _ => [],
        };
        return new PresetApplicationCommandResultDto(ids);
    }

    internal async Task<ProjectHistoryResponse?> ReadHistoryAsync(CancellationToken cancellationToken)
    {
        await Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsClosed) return null;
            return new(ProjectRevision.ToString(CultureInfo.InvariantCulture), Workspace.CanUndo, Workspace.CanRedo,
                Workspace.UndoName, Workspace.RedoName, Workspace.History.UndoCount, Workspace.History.RedoCount);
        }
        finally { Writer.Release(); }
    }

    internal async Task<LyricsLinesResponse?> ReadLyricsAsync(string branchId, CancellationToken cancellationToken)
    {
        await Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsClosed) return null;
            Core.Lyrics.LyricBranch? branch = Workspace.Project.Lyrics.Branches.FirstOrDefault(item => item.Id == branchId);
            if (branch is null) return null;
            LyricLineDto[] lines = [.. branch.Lines.Select(line => new LyricLineDto(
                branch.Id,
                line.Id,
                line.Text,
                ToTime(line.Time),
                [.. line.Tokens.Select(token => new LyricTokenDto(token.Id, token.Text, ToTime(token.Time))) ]))];
            return new(ProjectSessionId, ProjectRevision.ToString(CultureInfo.InvariantCulture), branch.Id, lines);
        }
        finally { Writer.Release(); }
    }

    private static bool IsStableId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static bool HasTimingFields(ProjectCommandDto command) =>
        command.Target is not null || command.StartUs is not null || command.EndUs is not null;

    private static ProjectCommandExecutionResult UnexpectedHistoryFields() =>
        ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest,
            "历史命令不能携带歌词时间或分析任务字段。", nameof(ProjectCommandRequest.Command)));

    private static ProjectCommandExecutionResult RejectedAnalysisEdit(ProjectAudioAnalysisEditResult result) =>
        ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.CommandRejected,
            result.Error?.Detail ?? "分析采用或清除被领域规则拒绝。", result.Error?.Code.ToString()));

    private static LyricTimeDto ToTime(TimeRange? time) => time is { } range
        ? new(range.StartUs.ToString(CultureInfo.InvariantCulture), range.EndUs.ToString(CultureInfo.InvariantCulture))
        : new(null, null);

    private static ProjectChangeSetDto ToChangeSet(ProjectTimingChangeSet changes)
    {
        if (changes.FullRefreshRequired)
            return new(["project"], [], [], [], true);
        string[] branchIds = [.. changes.Targets.Select(target => target.BranchId).Distinct(StringComparer.Ordinal)];
        string[] lineIds = [.. changes.Targets.Select(target => target.LineId).Distinct(StringComparer.Ordinal)];
        string[] tokenIds = [.. changes.Targets.Where(target => target.TokenId is not null)
            .Select(target => target.TokenId!).Distinct(StringComparer.Ordinal)];
        string[] areas = changes.HasChanges ? ["lyricsTiming", "timeline", "renderInputs"] : [];
        ProjectChangeSetDto result = new(areas, branchIds, lineIds, tokenIds, false);
        return JsonSerializer.SerializeToUtf8Bytes(result).Length > ProjectCommandLimits.MaxChangeSetBytes
            ? new(areas, [], [], [], true)
            : result;
    }

    private static BackendErrorResponse MapTimingError(LyricTimingEditError? error) => new(
        BackendErrorCode.CommandRejected,
        error?.Detail ?? "歌词时间命令被领域规则拒绝。",
        error?.Code.ToString());

    private static BackendErrorResponse MapBasicLayoutError(BasicLayoutError? error) => new(
        BackendErrorCode.CommandRejected,
        error?.Detail ?? "快速布局未通过工程领域校验。",
        "command",
        FailureCode: error?.Code.ToString());

    private static bool TryParseRevision(string? value, out long revision) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision >= 0;

    private static bool TryParseMicroseconds(string? value, out long microseconds) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out microseconds);

    internal static ProjectCommandRequest CanonicalCommand(ProjectCommandRequest request)
    {
        // DTO 固定字段顺序；数字字符串和 UTC 截止时间按语义规范化，保留固定大小摘要。
        ProjectCommandDto? command = request.Command;
        if (command is not null)
            command = command with
            {
                StartUs = TryParseMicroseconds(command.StartUs, out long start) ? start.ToString(CultureInfo.InvariantCulture) : command.StartUs,
                EndUs = TryParseMicroseconds(command.EndUs, out long end) ? end.ToString(CultureInfo.InvariantCulture) : command.EndUs,
            };
        if (command is ApplyProjectEditsCommandDto edits)
            command = edits with { Edits = edits.Edits?.Select(edit => edit is null ? null!
                : CanonicalCommand(new("0", edit)).Command).ToArray() };
        else if (command is not null) command = CanonicalEditingCommand(command);
        ProjectCommandRequest canonical = request with
        {
            ExpectedRevision = TryParseRevision(request.ExpectedRevision, out long revision) ? revision.ToString(CultureInfo.InvariantCulture) : request.ExpectedRevision,
            ValidUntilUtc = request.ValidUntilUtc?.ToUniversalTime(),
            Command = command!,
        };
        return canonical;
    }
}
