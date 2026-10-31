using System.ComponentModel;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>Host固定源／授权文件送入独立Worker，只消费脱敏完整报告。</summary>
public sealed class WorkerBackendExportExecutor : IBackendExportExecutor, IBackendImageExportExecutor,
    IBackendRenderBackendExecutor<IBackendExportExecutor>
{
    private readonly WorkerProcessOptions _options;
    private readonly string _ffmpeg, _ffprobe;
    private readonly TimeSpan _timeout;
    private readonly WorkerExportResourcePolicy _resourcePolicy;
    private string[] _sourceProfiles = [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis];
    private readonly object _imageExportCapabilityGate = new();
    private bool? _imageExportAvailable;
    public WorkerBackendExportExecutor(string workerPath, string ffmpegPath, string ffprobePath, TimeSpan? operationTimeout = null, BackendExportOptions? exportOptions = null)
        : this(new WorkerProcessOptions(workerPath), ffmpegPath, ffprobePath, operationTimeout, exportOptions) { }
    public WorkerBackendExportExecutor(WorkerProcessOptions options, string ffmpegPath, string ffprobePath, TimeSpan? operationTimeout = null, BackendExportOptions? exportOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        exportOptions ??= new(); exportOptions.Validate();
        _resourcePolicy = exportOptions.ResourcePolicy(operationTimeoutSeconds: operationTimeout is { } configured && configured != Timeout.InfiniteTimeSpan
            ? checked((int)Math.Max(1d, Math.Ceiling(configured.TotalSeconds))) : null);
        _options = options with { ExportMediaFileSizeLimitBytes = exportOptions.MaxMediaBytes };
        _ffmpeg = ffmpegPath; _ffprobe = ffprobePath; _timeout = operationTimeout ?? Timeout.InfiniteTimeSpan;
        if (_timeout != Timeout.InfiniteTimeSpan && _timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        WorkerExportContract.ValidateMetadata(Metadata(new("opaqueMp4", false, "", null), capabilities: true), capabilities: true);
    }
    public void ValidateInput(ProjectSnapshot snapshot, BackendExportExecutionInput input) => _ = EncodeInput(snapshot, input);

    public bool ImageExportAvailable
    {
        get
        {
            lock (_imageExportCapabilityGate) return _imageExportAvailable == true;
        }
    }

    private WorkerBackendExportExecutor(WorkerBackendExportExecutor source, string renderBackend)
    {
        _options = source._options with { RenderBackend = renderBackend };
        _ffmpeg = source._ffmpeg; _ffprobe = source._ffprobe; _timeout = source._timeout;
        _resourcePolicy = source._resourcePolicy;
        _sourceProfiles = [.. Volatile.Read(ref source._sourceProfiles)];
        lock (source._imageExportCapabilityGate) _imageExportAvailable = source._imageExportAvailable;
    }

    public IBackendExportExecutor WithRenderBackend(string renderBackend)
    {
        if (renderBackend is not ("cpu" or "gpu")) throw new ArgumentException("渲染后端无效。", nameof(renderBackend));
        return new WorkerBackendExportExecutor(this, renderBackend);
    }

    private async Task<bool> ProbeImageAsync(CancellationToken cancellationToken)
    {
        await using WorkerProcessProxy proxy = new(_options);
        try
        {
            ProjectImageExportWorkerCapabilities value = await proxy.GetImageExportCapabilitiesAsync(_ffmpeg, _ffprobe,
                _timeout, _resourcePolicy, cancellationToken).ConfigureAwait(false);
            return value.ExportCapabilities.OpaqueMp4 || value.ExportCapabilities.TransparentProRes4444;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is WorkerProtocolException or RenderSessionFailureException or OperationCanceledException
            or TimeoutException or IOException or InvalidOperationException or ObjectDisposedException or Win32Exception)
        { return false; }
    }

    public void ValidateImageInput(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes, BackendExportExecutionInput input)
        => _ = EncodeImageInput(snapshot, imageBytes, input);

    public async Task<ExportReportDto> ExecuteImageAsync(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes,
        BackendExportExecutionInput input, IProgress<BackendJobProgressDto>? progress = null,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = EncodeImageInput(snapshot, imageBytes, input);
        var plan = CompositionFramePlan.Create(snapshot.Composition.OutputDurationUs!.Value, snapshot.Composition.FrameRate);
        WorkerProcessProxy proxy = new(_options); Exception? primary = null;
        try
        {
            ProjectImageExportWorkerCapabilities capabilities = await proxy.GetImageExportCapabilitiesAsync(_ffmpeg, _ffprobe,
                _timeout, input.ResourcePolicy ?? _resourcePolicy, cancellationToken).ConfigureAwait(false);
            if (input.Format == "opaqueMp4" ? !capabilities.ExportCapabilities.OpaqueMp4 : !capabilities.ExportCapabilities.TransparentProRes4444)
                throw new RenderSessionFailureException("WorkerImageExportUnavailable", "实际图片导出 Worker 没有此格式的编码能力。");
            ProjectImageExportWorkerInput fixedInput = ProjectImageExportWorkerContract.DecodeInput(payload);
            WorkerMessage result = await proxy.InvokeExportTaskAsync(
                new(ProjectImageExportWorkerContract.RequestKind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
                    sourceSha256: fixedInput.SourceSha256,
                    taskId: fixedInput.TaskId.ToString("N"),
                    exportFormat: input.Format, exportIncludeAudio: input.IncludeAudio),
                plan.TotalFrames, _timeout, progress, cancellationToken, imageExport: true).ConfigureAwait(false);
            return WorkerExportContract.DecodeReport(result.Payload, new(input.Format, input.IncludeAudio,
                snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, snapshot.Composition.FrameRate.Numerator,
                snapshot.Composition.FrameRate.Denominator, plan.TotalFrames, plan.VideoDurationUs, input.Audio?.AudioProfile, input.Quality,
                input.ResourcePolicy ?? _resourcePolicy));
        }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is WorkerProtocolException protocol)
                throw new RenderSessionFailureException(protocol.Code, "固定图片导出 Worker 未完成。", protocol.Diagnostics is null ? null : DecodeDiagnostics(protocol.Diagnostics), protocol);
            if (failure is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new RenderSessionFailureException("WorkerImageExportUnavailable", "独立图片导出 Worker 不可用。", innerException: failure);
            throw;
        }
        finally
        {
            try { await proxy.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerImageExportCleanupFailure"] = cleanup; }
        }
    }

    private byte[] EncodeImageInput(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes, BackendExportExecutionInput input)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(input);
        if (snapshot.Composition.OutputDurationUs is not > 0)
            throw new RenderSessionFailureException("WorkerExportInputRejected", "图片导出要求显式正数时长。");
        var plan = CompositionFramePlan.Create(snapshot.Composition.OutputDurationUs.Value, snapshot.Composition.FrameRate);
        WorkerExportContract.ValidatePlanLimits(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight,
            snapshot.Composition.FrameRate.Numerator, snapshot.Composition.FrameRate.Denominator, plan.TotalFrames, plan.VideoDurationUs, input.Format, input.ResourcePolicy ?? _resourcePolicy);
        ProjectSnapshot basis = snapshot with { Background = BackgroundSource.Color };
        string baseProfile = ProjectPreviewSnapshotProfile.Select(basis);
        if (baseProfile == BackendSnapshotProfiles.StaticLyricsScenePresets)
            throw new RenderSessionFailureException("SnapshotEffectsUnsupported", "图片导出不支持固定预设来源。");
        if (input.IncludeAudio && (snapshot.AudioPath is null || input.Audio is null || snapshot.AudioMedia is { } media
            && (media.Hash != input.Audio.Sha256 || media.ByteLength != input.Audio.Size)))
            throw new RenderSessionFailureException("WorkerExportAudioMismatch", "导出音频没有匹配的固定授权。");
        Project project = ProjectSnapshotSourceProject.Restore(snapshot);
        if (ProjectValidator.Validate(project, ValidationContext.Export).HasErrors)
            throw new RenderSessionFailureException("WorkerExportSourceInvalid", "固定工程含正式导出阻断。");
        byte[] envelope = ProjectImagePreviewSnapshotCodec.Encode(snapshot, imageBytes.Span);
        var metadata = ImageMetadata(input, capabilities: false, baseProfile);
        return ProjectImageExportWorkerContract.EncodeInput(Guid.NewGuid(), metadata, envelope);
    }

    private ProjectImageExportWorkerMetadata ImageMetadata(BackendExportExecutionInput input, bool capabilities,
        string baseProfile = BackendSnapshotProfiles.StaticLyricsScene)
        => new(ProjectImageExportWorkerContract.ExportProfile, baseProfile,
            WorkerExportContract.GetExecutionProfile(capabilities || input.Audio is null ? null : input.Audio.AudioProfile), input.Format,
            capabilities ? false : input.IncludeAudio, capabilities ? null : input.CandidatePath,
            capabilities || input.Audio is null ? null : new(input.Audio.Path, input.Audio.Size, input.Audio.Sha256,
                input.Audio.AudioProfile == BackendAudioInputProfiles.Pcm16Wave ? null : input.Audio.AudioProfile), _ffmpeg, _ffprobe, input.Quality, input.ResourcePolicy ?? _resourcePolicy);
    private byte[] EncodeInput(ProjectSnapshot snapshot, BackendExportExecutionInput input)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(input);
        if (snapshot.Composition.OutputDurationUs is not > 0)
            throw new RenderSessionFailureException("WorkerExportInputRejected", "静态导出要求显式正数时长。");
        var plan = CompositionFramePlan.Create(snapshot.Composition.OutputDurationUs.Value, snapshot.Composition.FrameRate);
        WorkerExportContract.ValidatePlanLimits(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight,
            snapshot.Composition.FrameRate.Numerator, snapshot.Composition.FrameRate.Denominator, plan.TotalFrames, plan.VideoDurationUs, input.Format, input.ResourcePolicy ?? _resourcePolicy);
        string profile = input.RuntimeSource.IsEmpty ? ProjectSnapshotSourceProfile.Select(snapshot)
            : ProjectRuntimeInputCodec.ValidateEnvelope(input.RuntimeSource);
        if (!Volatile.Read(ref _sourceProfiles).Contains(profile, StringComparer.Ordinal))
            throw new RenderSessionFailureException("WorkerExportProfileMismatch", "实际导出Worker未提供所选来源能力。");
        if (input.IncludeAudio && (snapshot.AudioPath is null || input.Audio is null || snapshot.AudioMedia is { } media
            && (media.Hash != input.Audio.Sha256 || media.ByteLength != input.Audio.Size)))
            throw new RenderSessionFailureException("WorkerExportAudioMismatch", "导出音频没有匹配的固定授权。");
        // The Core export validator is low cost; it performs no Runtime or media I/O.
        Project project = ProjectSnapshotSourceProject.Restore(snapshot);
        if (ProjectValidator.Validate(project, ValidationContext.Export).HasErrors)
            throw new RenderSessionFailureException("WorkerExportSourceInvalid", "固定工程含正式导出阻断。");
        byte[] snapshotBytes;
        if (input.RuntimeSource.IsEmpty) snapshotBytes = ProjectSnapshotSourceProfile.Encode(snapshot);
        else
        {
            ProjectRuntimeInputCodec.ValidateSnapshot(input.RuntimeSource, snapshot);
            snapshotBytes = input.RuntimeSource.ToArray();
        }
        return WorkerExportContract.EncodeInput(Guid.NewGuid(), Metadata(input, false, profile), snapshotBytes);
    }
    private WorkerExportMetadata Metadata(BackendExportExecutionInput input, bool capabilities,
        string profile = BackendSnapshotProfiles.StaticLyricsScene) => new(WorkerExportContract.GetExecutionProfile(capabilities ? null : input.Audio?.AudioProfile),
        profile, input.Format, capabilities ? false : input.IncludeAudio, capabilities ? null : input.CandidatePath,
        capabilities || input.Audio is null ? null : new(input.Audio.Path, input.Audio.Size, input.Audio.Sha256,
            input.Audio.AudioProfile == BackendAudioInputProfiles.Pcm16Wave ? null : input.Audio.AudioProfile), _ffmpeg, _ffprobe, input.Quality, input.ResourcePolicy ?? _resourcePolicy);
    public async Task<BackendExportCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _sourceProfiles, []);
        try
        {
            byte[] payload = WorkerExportContract.EncodeInput(Guid.NewGuid(), Metadata(new("opaqueMp4", false, "", null), true), []);
            var message = await InvokeAsync(payload, true, 0, null, cancellationToken).ConfigureAwait(false);
            var value = WorkerExportContract.DecodeJson<WorkerExportCapabilitiesDto>(message.Payload, 4096);
            if (value.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity || value.SourceProfiles is not { Length: > 0 }
                || value.SourceProfiles.Distinct(StringComparer.Ordinal).Count() != value.SourceProfiles.Length
                || !value.SourceProfiles.All(BackendSnapshotProfiles.IsExportSupported)
                || !value.SourceProfiles.Contains(BackendSnapshotProfiles.StaticLyricsScene, StringComparer.Ordinal)
                || !value.SourceProfiles.Contains(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, StringComparer.Ordinal)
                || value.AudioProfiles is not { Length: 6 }
                || !value.AudioProfiles.Contains(BackendAudioInputProfiles.Pcm16Wave, StringComparer.Ordinal)
                || !value.AudioProfiles.Contains(BackendAudioInputProfiles.Mp3, StringComparer.Ordinal)
                || !value.AudioProfiles.Contains(BackendAudioInputProfiles.Flac, StringComparer.Ordinal)
                || !value.AudioProfiles.Contains(BackendAudioInputProfiles.Aac, StringComparer.Ordinal)
                || !value.AudioProfiles.Contains(BackendAudioInputProfiles.Mov, StringComparer.Ordinal)
                || !value.AudioProfiles.Contains(BackendAudioInputProfiles.Ogg, StringComparer.Ordinal)
                || value.ExecutionProfiles is not { Length: 2 }
                || !value.ExecutionProfiles.Contains(WorkerExportContract.ExecutionProfile, StringComparer.Ordinal)
                || !value.ExecutionProfiles.Contains(WorkerExportContract.CompressedExecutionProfile, StringComparer.Ordinal))
                return new(false, false, false, "WorkerExportProfileMismatch", [], []);
            Volatile.Write(ref _sourceProfiles, [.. value.SourceProfiles]);
            bool imageAvailable = await ProbeImageAsync(cancellationToken).ConfigureAwait(false);
            lock (_imageExportCapabilityGate) _imageExportAvailable = imageAvailable;
            return new(value.GuardAvailable, value.OpaqueMp4, value.TransparentProRes4444, value.FailureCode,
                value.AudioProfiles, value.ExecutionProfiles, [.. value.SourceProfiles], imageAvailable,
                imageAvailable ? null : "WorkerImageExportUnavailable",
                imageAvailable ? [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
                    BackendSnapshotProfiles.PackageLyricsScene] : []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RenderSessionFailureException failure) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("导出能力探测已取消。", failure, cancellationToken); }
        catch (RenderSessionFailureException failure) { return new(false, false, false, failure.Code); }
        catch (WorkerProtocolException failure) { return new(false, false, false, failure.Code); }
    }
    public async Task<ExportReportDto> ExecuteAsync(ProjectSnapshot snapshot, BackendExportExecutionInput input,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
    {
        byte[] payload = EncodeInput(snapshot, input);
        var plan = CompositionFramePlan.Create(snapshot.Composition.OutputDurationUs!.Value, snapshot.Composition.FrameRate);
        WorkerMessage result = await InvokeAsync(payload, false, plan.TotalFrames, progress, cancellationToken).ConfigureAwait(false);
        return WorkerExportContract.DecodeReport(result.Payload, new(input.Format, input.IncludeAudio,
            snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, snapshot.Composition.FrameRate.Numerator,
            snapshot.Composition.FrameRate.Denominator, plan.TotalFrames, plan.VideoDurationUs, input.Audio?.AudioProfile, input.Quality,
            input.ResourcePolicy ?? _resourcePolicy));
    }
    private async Task<WorkerMessage> InvokeAsync(byte[] payload, bool capabilities, int frames, IProgress<BackendJobProgressDto>? progress, CancellationToken token)
    {
        var input = WorkerExportContract.DecodeInput(payload, capabilities);
        WorkerMessage request = new(capabilities ? WorkerExportContract.CapabilitiesKind : WorkerExportContract.RequestKind,
            Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload, sourceSha256: input.SourceSha256,
            taskId: input.TaskId.ToString("N"), exportFormat: input.Metadata.Format, exportIncludeAudio: input.Metadata.IncludeAudio);
        var proxy = new WorkerProcessProxy(_options); Exception? primary = null;
        try { return await proxy.InvokeExportTaskAsync(request, frames, _timeout, progress, token).ConfigureAwait(false); }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is WorkerProtocolException protocol)
            {
                var neutral = new RenderSessionFailureException(protocol.Code, "独立静态导出Worker未完成。",
                    protocol.Diagnostics is null ? null : DecodeDiagnostics(protocol.Diagnostics), protocol);
                if (protocol.Data["WorkerExportDiagnosticsFailureCode"] is { } secondary)
                    neutral.Data["WorkerExportDiagnosticsFailureCode"] = secondary;
                throw neutral;
            }
            if (failure is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                throw new RenderSessionFailureException("WorkerExportUnavailable", "独立静态导出Worker不可用。", innerException: failure);
            if (failure is IOException)
                throw new RenderSessionFailureException("WorkerSessionConflict", "独立静态导出Worker连接提前结束。", innerException: failure);
            throw;
        }
        finally
        {
            try { await proxy.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerExportCleanupFailure"] = cleanup; }
        }
    }
    internal static RuntimeDiagnosticSnapshot DecodeDiagnostics(WorkerDiagnosticsSnapshotDto snapshot)
    {
        WorkerExportContract.ValidateDiagnostics(snapshot);
        return RuntimeDiagnosticSnapshot.FromOccurrences(snapshot.Occurrences.Select(item =>
        {
            var value = item.Diagnostic;
            var diagnostic = new RuntimeDiagnostic(new(value.InstanceId), value.ArtifactHash, value.Stage, value.TimeUs,
                new(Enum.Parse<DiagnosticSeverity>(value.Severity), value.Code, value.Message,
                    value.Location is null ? null : new(value.Location.Path, value.Location.Line, value.Location.Column)))
            { PackageIdentity = value.Package is null ? null : new(value.Package.PackageId, value.Package.Revision, value.Package.ExportId) };
            return new RuntimeDiagnosticOccurrence(item.Epoch, item.Sequence, diagnostic);
        }));
    }
}
