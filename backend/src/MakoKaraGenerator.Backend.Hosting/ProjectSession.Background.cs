using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal sealed class BackgroundHistoryState(StoredAsset asset, string path, IDisposable reference,
        Func<IDisposable> acquireReference) : IDisposable
    {
        internal readonly StoredAsset Asset = asset;
        internal readonly string Path = path;
        internal readonly Func<IDisposable> AcquireReference = acquireReference;
        private IDisposable? _reference = reference;
        public void Dispose() => Interlocked.Exchange(ref _reference, null)?.Dispose();
    }
    private readonly Dictionary<string, BackgroundHistoryState> _backgroundHistory = new(StringComparer.Ordinal);
    private bool _backgroundHistoryInitialized;

    internal void BindBackground(StoredAsset asset, string path, IDisposable reference, Func<IDisposable> acquireReference)
    {
        InitializeBackgroundHistory();
        _backgroundHistory.Add(path, new(asset, path, reference, acquireReference));
    }
    private void InitializeBackgroundHistory()
    {
        if (_backgroundHistoryInitialized) return;
        _backgroundHistoryInitialized = true;
        Workspace.History.Changed += OnBackgroundHistoryChanged;
    }
    private void OnBackgroundHistoryChanged(object? sender, EventArgs args)
    {
        // Changed precedes Workspace assigning the command result. The history retains both endpoints.
        var snapshots = Workspace.History.RetainedProjects.ToArray();
        var retained = (snapshots.Length == 0 ? new[] { Workspace.Project } : snapshots)
            .Select(p => p.Background.Path).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (string path in _backgroundHistory.Keys.Where(p => !retained.Contains(p)).ToArray())
        { _backgroundHistory[path].Dispose(); _backgroundHistory.Remove(path); }
    }
    private void DisposeBackgroundHistory()
    {
        Workspace.History.Changed -= OnBackgroundHistoryChanged;
        foreach (var state in _backgroundHistory.Values) state.Dispose();
        _backgroundHistory.Clear();
    }
    private static ProjectBackgroundDto BackgroundSettings(BackgroundSource background) =>
        new(background.Kind.ToString().ToLowerInvariant(), background.Fit.ToString().ToLowerInvariant(),
            background.Playback == BackgroundPlaybackMode.FreezeLastFrame ? "freezeLastFrame" : "loop",
            background.HasMedia ? background.Media is { } media ? "media:" + media.Hash : "project.background" : null);

    internal sealed record BackgroundCapture(Project Project, long Revision);
    internal async Task<(BackgroundCapture? Capture, BackendErrorResponse? Error)> CaptureBackgroundCommandAsync(ProjectCommandRequest request)
    {
        await Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (BasicMediaMutationError(request) is { } error) return (null, error);
            return (new(Workspace.Project, ProjectRevision), null);
        }
        finally { Writer.Release(); }
    }

    internal async Task<ProjectCommandExecutionResult> CommitBackgroundCommandAsync(ProjectCommandRequest request,
        BackgroundCapture capture, Func<bool> registered, Func<string, StoredAsset> getAsset, Func<string, IDisposable> acquireReference)
    {
        IDisposable? lease = null;
        await Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (BasicMediaMutationError(request) is { } error) return ProjectCommandExecutionResult.Rejected(error);
            if (!registered()) return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.SessionNotFound, "工程会话不存在。"));
            if (ProjectRevision != capture.Revision || !ReferenceEquals(Workspace.Project, capture.Project))
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.RevisionConflict, "背景准备期间工程已经改变。", "expectedRevision"));
            Project before = Workspace.Project, after = before;
            StoredAsset? asset = null;
            switch (request.Command)
            {
                case SetBackgroundAssetCommandDto set:
                    if (set.Kind is not ("image" or "video") || !IsStableId(set.AssetId))
                        return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.InvalidRequest, "必须提供 assetId 和 image/video kind。", "command"));
                    if (!TryBackgroundFit(set.Fit ?? FitName(before.Background.Fit), out var fit))
                        return InvalidBackground("fit");
                    if (!TryBackgroundPlayback(set.Playback ?? PlaybackName(before.Background.Playback), out var playback))
                        return InvalidBackground("playback");
                    lease = acquireReference(set.AssetId!);
                    asset = getAsset(set.AssetId!);
                    if (!asset.ContentType.StartsWith(set.Kind + "/", StringComparison.Ordinal))
                        return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.UnsupportedMediaType, "资源类型与背景 kind 不匹配。", "command.assetId"));
                    // Saved resources use their pinned closure path. Reusing their asset must preserve no-op semantics.
                    string path = _backgroundHistory.Values.FirstOrDefault(s => s.Asset.AssetId == asset.AssetId)?.Path ?? asset.StoragePath;
                    after = set.Kind == "image" ? ProjectBackgroundEditor.SetImage(before, path) : ProjectBackgroundEditor.SetVideo(before, path);
                    after = ProjectBackgroundEditor.SetFit(after, fit);
                    after = ProjectBackgroundEditor.SetPlaybackMode(after, playback);
                    MediaFileRevision identity = new(asset.Sha256, asset.Size, asset.FileName);
                    if (after.Background.Media != identity) after = after with { Background = after.Background with { Media = identity } };
                    break;
                case ClearBackgroundCommandDto: after = ProjectBackgroundEditor.Clear(before); break;
                case SetBackgroundFitCommandDto setFit:
                    if (!TryBackgroundFit(setFit.Fit, out var parsedFit)) return InvalidBackground("fit");
                    after = ProjectBackgroundEditor.SetFit(before, parsedFit); break;
                case SetBackgroundPlaybackCommandDto setPlayback:
                    if (!TryBackgroundPlayback(setPlayback.Playback, out var parsedPlayback)) return InvalidBackground("playback");
                    after = ProjectBackgroundEditor.SetPlaybackMode(before, parsedPlayback); break;
                default: return InvalidBackground("type");
            }
            bool changed = !ReferenceEquals(before, after);
            long previous = ProjectRevision;
            ProjectChangeSetDto changes = new(changed ? ["background", "settings", "resources", "renderInputs"] : [], [], [], [], changed);
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText,
                (changed ? checked(ProjectRevision + 1) : ProjectRevision).ToString(CultureInfo.InvariantCulture), changed,
                changed || Workspace.IsDirty, changed || Workspace.CanUndo, changed ? false : Workspace.CanRedo,
                changed ? request.Command.Type : Workspace.UndoName, changed ? null : Workspace.RedoName, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture));
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ResourceTooLarge, "完整背景回执超过预算。", "command"));
            if (changed)
            {
                if (asset is not null && !_backgroundHistory.ContainsKey(after.Background.Path!))
                {
                    BindBackground(asset, after.Background.Path!, lease!, () => acquireReference(asset.AssetId));
                    lease = null;
                }
                Workspace.History.BreakMergeGroup();
                Workspace.Execute(ProjectCommand.Create(request.Command.Type, _ => after));
                Workspace.History.BreakMergeGroup();
                ProjectRevision = checked(ProjectRevision + 1);
            }
            string cursor = changed ? PublishEvent("project.changed", changes) : _events.Cursor(OwnerClientId);
            return ProjectCommandExecutionResult.FromResponse(BasicAudioResponse(previous, changed, changes, cursor));
        }
        catch (ResourceStoreException error)
        { return ProjectCommandExecutionResult.Rejected(new(error.Code, error.Message, "command.assetId")); }
        finally { lease?.Dispose(); Writer.Release(); }
    }
    private static string FitName(BackgroundFit fit) => fit.ToString().ToLowerInvariant();
    private static string PlaybackName(BackgroundPlaybackMode playback) => playback == BackgroundPlaybackMode.FreezeLastFrame ? "freezeLastFrame" : "loop";
    private static bool TryBackgroundFit(string? value, out BackgroundFit fit)
    {
        fit = value switch { "contain" => BackgroundFit.Contain, "cover" => BackgroundFit.Cover, "original" => BackgroundFit.Original, _ => (BackgroundFit)(-1) };
        return Enum.IsDefined(fit);
    }
    private static bool TryBackgroundPlayback(string? value, out BackgroundPlaybackMode playback)
    {
        playback = value switch { "loop" => BackgroundPlaybackMode.Loop, "freezeLastFrame" => BackgroundPlaybackMode.FreezeLastFrame, _ => (BackgroundPlaybackMode)(-1) };
        return Enum.IsDefined(playback);
    }
    private static ProjectCommandExecutionResult InvalidBackground(string field) => ProjectCommandExecutionResult.Rejected(
        new(BackendErrorCode.InvalidRequest, "背景字段必须使用公开的命名值。", "command." + field));
}
