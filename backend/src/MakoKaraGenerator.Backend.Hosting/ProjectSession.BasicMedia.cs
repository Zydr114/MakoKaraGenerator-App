using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private static readonly string[] BasicAudioChangeAreas = ["audio", "audioAnalysis", "waveform", "timeline", "renderInputs"];
    private static readonly string[] BasicAnalysisChangeAreas = ["audioAnalysis", "renderInputs"];
    private readonly Dictionary<string, AudioHistoryState> _audioHistory = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _audioHistoryInitialized;

    internal sealed record BasicAudioCapture(Project Project, long Revision, bool Clear, bool NoOp);

    internal sealed class AudioHistoryState(string assetId, string path, string? hash, long bytes, long duration,
        IDisposable? resource, WaveformCache.Binding? waveform, string unavailable) : IDisposable
    {
        internal readonly string AssetId = assetId, Path = path, Unavailable = unavailable;
        internal readonly string? Hash = hash;
        internal readonly long Bytes = bytes, Duration = duration;
        internal readonly WaveformCache.Binding? Waveform = waveform;
        private IDisposable? _resource = resource;
        public void Dispose() { try { Waveform?.Dispose(); } finally { Interlocked.Exchange(ref _resource, null)?.Dispose(); } }
    }

    private void InitializeAudioHistory()
    {
        if (_audioHistoryInitialized) return;
        _audioHistoryInitialized = true;
        if (PreparedAudioSourcePath is { } path)
            _audioHistory.Add(path, new(AudioAssetId, path, _waveformSourceSha256 ?? Workspace.Project.AudioMedia?.Hash,
                _waveformSourceBytes, AudioDurationUs, Interlocked.Exchange(ref _resourceReference, null), _waveform, _waveformUnavailable));
        Workspace.History.Changed += OnAudioHistoryChanged;
    }

    private void OnAudioHistoryChanged(object? sender, EventArgs args)
    {
        // History raises Changed before Workspace assigns the returned immutable Project. Every
        // prospective current state is in the retained history while either stack is nonempty.
        var snapshots = Workspace.History.RetainedProjects.ToArray();
        var retained = (snapshots.Length == 0 ? new[] { Workspace.Project } : snapshots)
            .Select(project => project.AudioPath).OfType<string>().ToHashSet(_audioHistory.Comparer);
        foreach (string path in _audioHistory.Keys.Where(path => !retained.Contains(path)).ToArray())
        { AudioHistoryState old = _audioHistory[path]; _audioHistory.Remove(path); old.Dispose(); }
    }

    private void DisposeAudioHistory()
    {
        Workspace.History.Changed -= OnAudioHistoryChanged;
        foreach (AudioHistoryState state in _audioHistory.Values) state.Dispose();
        _audioHistory.Clear();
    }

    private void SynchronizeAudioState()
    {
        if (!_audioHistoryInitialized) return;
        if (Workspace.Project.AudioPath is { } path && _audioHistory.TryGetValue(path, out AudioHistoryState? state))
        {
            AudioAssetId = state.AssetId; AudioDurationUs = state.Duration; PreparedAudioSourcePath = state.Path;
            _waveform = state.Waveform; _waveformAssetId = state.AssetId; _waveformSourceSha256 = state.Hash;
            _waveformSourceBytes = state.Bytes; _waveformUnavailable = state.Unavailable;
        }
        else
        {
            AudioAssetId = string.Empty; AudioDurationUs = 0; PreparedAudioSourcePath = null;
            _waveform = null; _waveformAssetId = null; _waveformSourceSha256 = null; _waveformSourceBytes = 0;
            _waveformUnavailable = "noAudio";
        }
    }

    internal async Task<(BasicAudioCapture? Capture, BackendErrorResponse? Error)> CaptureBasicAudioAsync(ProjectCommandRequest request)
    {
        await Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (BasicMediaMutationError(request) is { } error) return (null, error);
            bool clear = request.Command is ClearProjectAudioCommandDto;
            if (!clear && request.Command is not ReplaceProjectAudioAssetCommandDto)
                return (null, new(BackendErrorCode.InvalidRequest, "音频命令类型不匹配。", "command"));
            if (request.Command is ReplaceProjectAudioAssetCommandDto replace && !IsStableId(replace.AssetId))
                return (null, new(BackendErrorCode.InvalidRequest, "必须提供有效资源身份。", "command.assetId"));
            InitializeAudioHistory();
            bool noOp = clear ? Workspace.Project.AudioPath is null && Workspace.Project.AudioAnalysis is null
                && Workspace.Project.AudioMedia is null : ((ReplaceProjectAudioAssetCommandDto)request.Command).AssetId == AudioAssetId;
            return (new(Workspace.Project, ProjectRevision, clear, noOp), null);
        }
        finally { Writer.Release(); }
    }

    private BackendErrorResponse? BasicMediaMutationError(ProjectCommandRequest request)
    {
        if (MutationBlocked) return new(BackendErrorCode.SessionClosing, "工程会话正在关闭。", "command");
        if (IsReadOnly) return new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。", "command");
        if (!TryParseRevision(request.ExpectedRevision, out long revision))
            return new(BackendErrorCode.InvalidRequest, "expectedRevision必须是非负十进制修订。", "expectedRevision");
        if (revision != ProjectRevision) return new(BackendErrorCode.RevisionConflict, "工程修订已经改变。", "expectedRevision");
        if (!EditingCommandTypeMatches(request.Command)) return new(BackendErrorCode.InvalidRequest, "命令type与DTO不匹配。", "command");
        if (HasTimingFields(request.Command) || request.Command.AnalysisJobId is not null)
            return new(BackendErrorCode.InvalidRequest, "音频命令不能携带歌词或分析任务字段。", "command");
        return null;
    }

    internal async Task<ProjectCommandExecutionResult> CommitBasicAudioAsync(ProjectCommandRequest request,
        BasicAudioCapture capture, StoredAsset? audio, AudioHistoryState? prepared)
    {
        await Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (BasicMediaMutationError(request) is { } error) return ProjectCommandExecutionResult.Rejected(error);
            if (ProjectRevision != capture.Revision || !ReferenceEquals(Workspace.Project, capture.Project))
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.RevisionConflict, "音频准备期间工程已经改变。", "expectedRevision"));
            Project before = Workspace.Project;
            Project after = capture.NoOp ? before : capture.Clear
                ? before with { AudioPath = null, AudioMedia = null, AudioAnalysis = null }
                : before with { AudioPath = audio!.StoragePath, AudioMedia = new(audio.Sha256, audio.Size, audio.FileName), AudioAnalysis = null };
            bool changed = !ReferenceEquals(before, after);
            ProjectChangeSetDto changes = new(changed ? ["audio", "audioAnalysis", "waveform", "timeline", "renderInputs"] : [], [], [], [], changed);
            // Complete response budget is checked before ownership transfer and the only domain mutation.
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText,
                (changed ? checked(ProjectRevision + 1) : ProjectRevision).ToString(CultureInfo.InvariantCulture), changed,
                changed || Workspace.IsDirty, changed || Workspace.CanUndo, changed ? false : Workspace.CanRedo,
                changed ? request.Command.Type : Workspace.UndoName, changed ? null : Workspace.RedoName, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture));
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ResourceTooLarge, "完整音频回执超过预算。", "command"));
            if (changed)
            {
                if (prepared is not null && !_audioHistory.ContainsKey(prepared.Path))
                { _audioHistory.Add(prepared.Path, prepared); prepared = null; }
                Workspace.History.BreakMergeGroup();
                Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => after));
                Workspace.History.BreakMergeGroup();
                ProjectRevision = checked(ProjectRevision + 1);
                SynchronizeAudioState();
            }
            string cursor = changed ? PublishEvent("project.changed", changes) : _events.Cursor(OwnerClientId);
            return ProjectCommandExecutionResult.FromResponse(BasicAudioResponse(capture.Revision, changed, changes, cursor));
        }
        finally { prepared?.Dispose(); Writer.Release(); }
    }

    private ProjectCommandResponse BasicAudioResponse(long previous, bool changed, ProjectChangeSetDto changes, string cursor) =>
        new(ProjectSessionId, previous.ToString(CultureInfo.InvariantCulture), RevisionText,
            changed, Workspace.IsDirty, Workspace.CanUndo, Workspace.CanRedo,
            Workspace.UndoName, Workspace.RedoName, changes, EventCursor: cursor);

    private static ProjectChangeSetDto IncludeBasicMediaChanges(Project before, Project after, ProjectChangeSetDto changes)
    {
        bool audioChanged = !string.Equals(before.AudioPath, after.AudioPath, StringComparison.Ordinal) || before.AudioMedia != after.AudioMedia;
        bool analysisChanged = before.AudioAnalysis != after.AudioAnalysis;
        bool nameChanged = before.Name != after.Name;
        bool backgroundChanged = before.Background != after.Background;
        if (!audioChanged && !analysisChanged && !nameChanged && !backgroundChanged) return changes;
        IEnumerable<string> areas = changes.Areas;
        if (audioChanged) areas = areas.Concat(BasicAudioChangeAreas);
        else if (analysisChanged) areas = areas.Concat(BasicAnalysisChangeAreas);
        if (nameChanged) areas = areas.Append("project");
        if (backgroundChanged) areas = areas.Concat(["background", "settings", "resources", "renderInputs"]);
        return changes with { Areas = [.. areas.Distinct(StringComparer.Ordinal)], RequiresFullRefresh = changes.RequiresFullRefresh || audioChanged };
    }

    private bool TryPrepareBasicProjectCommand(Project before, ProjectCommandDto command, out Project after,
        out ProjectChangeSetDto? changes, out BackendErrorResponse? error)
    {
        after = before; changes = null; error = null;
        if (command is not RenameProjectCommandDto rename) return false;
        if (HasTimingFields(command) || command.AnalysisJobId is not null || string.IsNullOrWhiteSpace(rename.Name) || rename.Name.Length > 256)
        { error = new(BackendErrorCode.InvalidRequest, "工程名称必须非空白且不超过256个UTF-16单元。", "command.name"); return true; }
        string name = rename.Name.Trim();
        bool changed = name != before.Name;
        after = changed ? before with { Name = name } : before;
        changes = new(changed ? ["project"] : [], [], [], [], false);
        try
        {
            string revision = (changed ? checked(ProjectRevision + 1) : ProjectRevision).ToString(CultureInfo.InvariantCulture);
            Composition composition = after.Composition;
            RgbaColor color = composition.BackgroundColor;
            BoundedReadModel(new ProjectSettingsResponse(ProjectSessionId, revision, after.Name,
                composition.CanvasWidth, composition.CanvasHeight, composition.FrameRate.Numerator, composition.FrameRate.Denominator,
                color.Red, color.Green, color.Blue, color.Alpha, composition.OutputDurationUs?.ToString(CultureInfo.InvariantCulture),
                AudioDurationUs.ToString(CultureInfo.InvariantCulture), Background: BackgroundSettings(after.Background)));
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText, revision, changed, changed || Workspace.IsDirty,
                changed || Workspace.CanUndo, changed ? false : Workspace.CanRedo, changed ? command.Type : Workspace.UndoName,
                changed ? null : Workspace.RedoName, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture));
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                throw new ReadModelBudgetExceededException();
        }
        catch (ReadModelBudgetExceededException)
        { after = before; error = new(BackendErrorCode.ResourceTooLarge, "完整改名候选或回执超过预算；工程未改变。", "command"); }
        return true;
    }
}
