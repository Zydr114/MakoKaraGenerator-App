using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using CliWrap;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Export;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>真实静态导出只调用原VideoExporter。候选是私有产物，公开提交归Host。</summary>
internal static class StaticExportTask
{
    internal static Task<WorkerMessage> ExecuteAsync(WorkerMessage request, bool guards,
        Func<WorkerMessage, Task> send, CancellationToken token, string? renderBackend = null) => ExecuteAsync(request, guards, send, null, token, renderBackend: renderBackend);

    internal static Task<WorkerMessage> ExecuteImageAsync(WorkerMessage request, bool guards,
        Func<WorkerMessage, Task> send, CancellationToken token, string? renderBackend = null) => ExecuteAsync(request, guards, send, null, token, imageExport: true, renderBackend: renderBackend);

    internal static async Task<WorkerMessage> ExecuteAsync(WorkerMessage request, bool guards,
        Func<WorkerMessage, Task> send, IRenderFrameSessionFactory? sessions, CancellationToken token, bool imageExport = false,
        string? renderBackend = null)
    {
        bool capabilities = request.Kind == (imageExport ? ProjectImageExportWorkerContract.CapabilitiesKind : WorkerExportContract.CapabilitiesKind);
        WorkerExportMetadata metadata;
        ReadOnlyMemory<byte> inputSnapshot;
        string inputSourceSha;
        Guid taskId;
        if (imageExport)
        {
            ProjectImageExportWorkerInput imageInput = ProjectImageExportWorkerContract.DecodeInput(request.Payload, capabilities);
            metadata = new(imageInput.Metadata.ExecutionProfile, imageInput.Metadata.BaseProfile, imageInput.Metadata.Format,
                imageInput.Metadata.IncludeAudio, imageInput.Metadata.CandidatePath, imageInput.Metadata.Audio,
                imageInput.Metadata.FfmpegPath, imageInput.Metadata.FfprobePath, imageInput.Metadata.Quality, imageInput.Metadata.ResourcePolicy);
            inputSnapshot = imageInput.SnapshotBytes; inputSourceSha = imageInput.SourceSha256; taskId = imageInput.TaskId;
        }
        else
        {
            WorkerExportInput input = WorkerExportContract.DecodeInput(request.Payload, capabilities);
            metadata = input.Metadata; inputSnapshot = input.SnapshotBytes; inputSourceSha = input.SourceSha256; taskId = input.TaskId;
        }
        WorkerExportContract.ValidateCorrelation(request, request);
        if (request.TaskId != taskId.ToString("N") || request.SourceSha256 != inputSourceSha
            || request.ExportFormat != metadata.Format || request.ExportIncludeAudio != metadata.IncludeAudio)
            throw Fail("WorkerExportProtocolInvalid");
        if (!guards) throw Fail("WorkerExportUnavailable");
        if (capabilities)
        {
            var supported = await ProbeCapabilitiesAsync(metadata, token).ConfigureAwait(false);
            if (imageExport)
            {
                string referenceDirectory = Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
                var references = ScriptReferenceIdentity.Read(referenceDirectory);
                PackageRenderWorkerCapabilities package = new(StaticProjectSnapshotCodec.EngineIdentity,
                    PackageProjectSnapshotCodec.Profile, ProjectEffectsJson.PackageProfile.PackageVersion,
                    ProjectEffectsJson.PackageProfile.SdkVersion, ProjectEffectsJson.PackageProfile.ShaderProfile,
                    references.Identity, references.Count, ValidationProfiles: [PackageRenderWorkerContract.ValidationProfile],
                    SupportedPackageProfiles: WorkerPackageProfiles.Current);
                var imageCapabilities = new ProjectImageExportWorkerCapabilities(StaticProjectSnapshotCodec.EngineIdentity,
                    ProjectImageExportWorkerContract.ExportProfile,
                    [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.PackageLyricsScene],
                    ProjectImagePreviewWorkerContract.ImageDecoderProfile, ProjectImageExportWorkerContract.MaximumInputBytes,
                    ProjectImageValidationWorkerContract.MaximumImageBytes, ProjectImageValidationWorkerContract.MaximumDimension,
                    ProjectImageValidationWorkerContract.MaximumDecodedBytes, ProjectImagePreviewWorkerContract.MaximumBaseSnapshotBytes,
                    ProjectImagePreviewWorkerContract.MaximumSourceValues, WorkerExportContract.MaximumReportBytes,
                    metadata.ResourcePolicy?.MaxMediaBytes, metadata.ResourcePolicy?.MaxAudioBytes,
                    WorkerPackageDiagnosticsContract.Version, package, supported);
                return Result(ProjectImageExportWorkerContract.CapabilitiesResultKind,
                    ProjectImageExportWorkerContract.EncodeCapabilities(imageCapabilities), 0);
            }
            return Result(WorkerExportContract.CapabilitiesResultKind, WorkerExportContract.EncodeJson(supported, 4096), 0);
        }
        bool compressedAudio = metadata.ExecutionProfile == WorkerExportContract.CompressedExecutionProfile;
        var prepared = WorkerScriptInputs.Read(inputSnapshot, token); inputSnapshot = prepared.Source;
        Func<IBackgroundFrameSourceResolver>? imageResolverFactory = null;
        byte[]? imageBytes = null;
        string? imageLogicalPath = null;
        using ProjectRuntimeInput? runtimeInput = imageExport ? null : await ProjectRuntimeInput.OpenAsync(inputSnapshot, token, prepared.Compiler).ConfigureAwait(false);
        ProjectSnapshot snapshot;
        try
        {
            if (imageExport)
            {
                var image = ProjectImagePreviewSnapshotCodec.Decode(inputSnapshot);
                snapshot = image.Snapshot; imageBytes = image.ImageBytes; imageLogicalPath = image.LogicalPath;
                ProjectSnapshot basis = snapshot with { Background = BackgroundSource.Color };
                string actualBaseProfile = ProjectPreviewSnapshotProfile.Select(basis);
                if (actualBaseProfile != metadata.CodecProfile) throw Fail("WorkerExportSourceMismatch");
                imageResolverFactory = () =>
                {
                    try { return new MemoryImageBackgroundFrameSourceResolver(imageLogicalPath!, imageBytes!, token); }
                    catch (ImageBackgroundDecodeException failure)
                    { throw new RenderSessionFailureException(failure.Code, failure.Message, innerException: failure); }
                };
            }
            else
                snapshot = runtimeInput!.Snapshot;
        }
        catch (SnapshotContractException error)
        {
            throw Fail("WorkerExportSourceMismatch", error);
        }
        if (!imageExport && runtimeInput!.SourceProfile != metadata.CodecProfile) throw Fail("WorkerExportSourceMismatch");
        CompositionFramePlan plan = ValidatePlan(snapshot, metadata.Format, metadata.ResourcePolicy);
        Project project = ToProject(snapshot);
        if (imageExport)
        {
            byte[] imageCanonical = ProjectImagePreviewSnapshotCodec.Encode(snapshot, imageBytes!);
            if (!imageCanonical.AsSpan().SequenceEqual(inputSnapshot.Span)) throw Fail("WorkerExportSourceMismatch");
        }
        else
        {
            ProjectRuntimeInputCodec.ValidateSnapshot(inputSnapshot, ProjectSnapshot.FromProject(project));
        }
        if (metadata.IncludeAudio && (snapshot.AudioPath is null || snapshot.AudioMedia is { } media
            && (media.Hash != metadata.Audio!.Sha256 || media.ByteLength != metadata.Audio.Size))) throw Fail("WorkerExportAudioMismatch");
        long? sourceAudioDuration = null;
        AudioStreamInfo? decodedAudio = null;
        if (metadata.Audio is { } audio)
        {
            await ValidateAudioAsync(audio, plan, token).ConfigureAwait(false);
            string format = AudioFormat(audio);
            var actual = await new FfmpegMediaProbe(metadata.FfprobePath).ProbeAsync(audio.Path, format, token).ConfigureAwait(false);
            if (compressedAudio)
            {
                ValidateCompressedProbe(actual, format);
                long samples = await CountCompressedSamplesAsync(new RestrictedCompressedReader(metadata.FfmpegPath, format), audio.Path,
                    actual!.SampleRate, token, metadata.ResourcePolicy?.MaxCompressedAudioDurationUs,
                    enforceBudget: metadata.ResourcePolicy is not null).ConfigureAwait(false);
                if ((Int128)samples * 1_000_000 < (Int128)plan.VideoDurationUs * actual.SampleRate) throw Fail("WorkerExportAudioTooShort");
                sourceAudioDuration = checked((long)((Int128)samples * 1_000_000 / actual.SampleRate));
                decodedAudio = actual with { DurationUs = sourceAudioDuration.Value };
                await ValidateAudioAsync(audio, plan, token).ConfigureAwait(false);
            }
            else
            {
                // Keep the original precise WAV path and its report values.
                if (actual is null || actual.CodecName != "pcm_s16le" || actual.Channels is not (1 or 2) || actual.DurationUs < plan.VideoDurationUs)
                    throw Fail("WorkerExportAudioUnsupported");
                sourceAudioDuration = actual.DurationUs;
            }
            project = project with { AudioPath = audio.Path, AudioMedia = project.AudioMedia ?? new(audio.Sha256, audio.Size, "audio.wav") };
        }
        CheckPath(metadata.CandidatePath!, isDirectory: false, mayBeMissing: true);
        if (File.Exists(metadata.CandidatePath)) throw Fail("WorkerExportCandidateExists");
        IRenderFrameSessionFactory factory = !imageExport && (sessions is null || metadata.CodecProfile == ProjectRuntimeInputCodec.Profile)
            ? runtimeInput!.CreateFactory(ReplayReason.Initial, renderBackend: renderBackend)
            : imageExport ? new ProjectRenderSessionFactory(prepared.Compiler, new FfmpegVideoFrameProvider(), ReplayReason.Initial,
                imageResolverFactory, renderBackend: renderBackend) : sessions!;
        var strict = new StrictFactory(factory, metadata);
        var probe = new StrictProbe(new FfmpegMediaProbe(metadata.FfprobePath), metadata, plan, snapshot.Composition, decodedAudio);
        var encoder = new StrictEncoder(new FfmpegMediaEncoder(metadata.FfmpegPath), metadata, plan);
        var channel = Channel.CreateBounded<(long Sequence, ExportProgress Progress)>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        long sequence = 0; bool budgetExceeded = false;
        Task pump = PumpAsync(); Task monitor = MonitorAsync(); Exception? primary = null;
        try
        {
            ExportReport original = await new VideoExporter(new NoFallbackPipeline(), probe, encoder, strict).ExportAsync(project,
                new() { OutputPath = metadata.CandidatePath!, IncludeAudio = metadata.IncludeAudio, Overwrite = false,
                    Format = metadata.Format == "opaqueMp4" ? ExportVideoFormat.OpaqueMp4 : ExportVideoFormat.TransparentProRes4444,
                    Quality = Quality(metadata.Quality), RenderQuality = RenderQuality.Final, Timeout = metadata.ResourcePolicy?.OperationTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan,
                    AudioEncodingPlanOverride = compressedAudio ? CompressedAudioEncodingPlan(metadata.Format) : null },
                new InlineProgress(value => channel.Writer.TryWrite((++sequence, value))), operation.Token).ConfigureAwait(false);
            if (metadata.Audio is { } pinned) await ValidateAudioAsync(pinned, plan, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            MediaFileInfo produced = probe.Produced ?? throw Fail("WorkerExportMediaVerificationFailed");
            var video = produced.Video!;
            FileInfo file = new(metadata.CandidatePath!); CheckPath(file.FullName, false, false);
            if (file.Length <= 0 || file.Length > metadata.ResourcePolicy?.MaxMediaBytes) throw Fail("WorkerExportArtifactBudgetExceeded");
            await using FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            string sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            var dto = new ExportReportDto(metadata.Format, metadata.IncludeAudio, plan.TotalFrames, checked((int)video.DecodedFrameCount!.Value),
                original.FramesWritten, original.DroppedFrames, original.FailedFrames, video.Width, video.Height,
                snapshot.Composition.FrameRate.Numerator, snapshot.Composition.FrameRate.Denominator, Text(plan.VideoDurationUs),
                Text(video.DurationUs > 0 ? video.DurationUs : produced.DurationUs), sourceAudioDuration is { } duration ? Text(duration) : null,
                video.CodecName, produced.Audio?.CodecName, video.PixelFormat!, video.HasAlpha, file.Length, sha,
                [.. original.Warnings.Select(Sanitize)], [.. original.DiagnosticOccurrences.Select(MapDiagnostic)], original.IsVerified,
                produced.Audio is { } outputAudio ? Text(outputAudio.DurationUs) : null, true, metadata.Quality,
                original.Quality?.ToString().ToLowerInvariant() ?? throw Fail("WorkerExportQualityMismatch"));
            byte[] payload = WorkerExportContract.EncodeReport(dto, Expectation(snapshot, metadata.Format, metadata.IncludeAudio, plan, metadata.Audio?.AudioProfile, metadata.Quality, metadata.ResourcePolicy));
            channel.Writer.TryComplete(); await pump.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Result(imageExport ? ProjectImageExportWorkerContract.ResultKind : WorkerExportContract.ResultKind, payload, sequence);
        }
        catch (Exception failure)
        {
            primary = failure;
            if (budgetExceeded) throw Fail("WorkerExportArtifactBudgetExceeded", failure);
            throw;
        }
        finally
        {
            operation.Cancel(); channel.Writer.TryComplete();
            try { await Task.WhenAll(pump, monitor).ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerExportPumpCleanupFailure"] = cleanup; }
        }

        WorkerMessage Result(string kind, byte[] payload, long finalSequence) => new(kind, request.RequestId, request.InputSha256, payload,
            outputSha256: WorkerProtocol.ComputeSha256(payload), sourceSha256: request.SourceSha256, taskId: request.TaskId,
            exportFormat: request.ExportFormat, exportIncludeAudio: request.ExportIncludeAudio, progressSequence: finalSequence);
        async Task PumpAsync()
        {
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                    await send(new(imageExport ? ProjectImageExportWorkerContract.ProgressKind : WorkerExportContract.ProgressKind, request.RequestId, request.InputSha256,
                        sourceSha256: request.SourceSha256, taskId: request.TaskId, exportFormat: request.ExportFormat,
                        exportIncludeAudio: request.ExportIncludeAudio, progressSequence: item.Sequence,
                        exportProgress: new(Stage(item.Progress.Stage), item.Progress.FramesWritten, item.Progress.TotalFrames))).ConfigureAwait(false);
            }
            catch { operation.Cancel(); throw; }
        }
        async Task MonitorAsync()
        {
            try
            {
                if (metadata.ResourcePolicy?.MaxMediaBytes is null) return;
                using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(40));
                while (await timer.WaitForNextTickAsync(operation.Token).ConfigureAwait(false))
                    foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(metadata.CandidatePath!)!))
                    {
                        FileInfo file = new(path);
                        if (file.Length > metadata.ResourcePolicy?.MaxMediaBytes) { budgetExceeded = true; operation.Cancel(); return; }
                    }
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        }
        string Sanitize(string value)
        {
            string result = value.Replace(metadata.CandidatePath!, "[candidate]", StringComparison.Ordinal)
                .Replace(metadata.FfmpegPath, "[ffmpeg]", StringComparison.Ordinal).Replace(metadata.FfprobePath, "[ffprobe]", StringComparison.Ordinal);
            if (metadata.Audio is { } audio) result = result.Replace(audio.Path, "[audio]", StringComparison.Ordinal);
            return result;
        }
    }

    private static ExportQuality Quality(string quality) => quality switch
    {
        "draft" => ExportQuality.Draft, "standard" => ExportQuality.Standard, "high" => ExportQuality.High,
        _ => throw Fail("WorkerExportInputRejected"),
    };

    internal static AudioEncodingPlan CompressedAudioEncodingPlan(string format)
        => format == "transparentProRes4444"
            ? new(false, "pcm_s16le", "固定压缩音频重编码为 pcm_s16le。")
            : new(false, AudioEncodingPlan.FallbackCodecName, "固定压缩音频重编码为 aac。");

    internal static CompositionFramePlan ValidatePlan(ProjectSnapshot snapshot, string format, WorkerExportResourcePolicy? policy = null)
    {
        var c = snapshot.Composition;
        if (c.OutputDurationUs is null or <= 0) throw Fail("WorkerExportInputRejected");
        var plan = CompositionFramePlan.Create(c.OutputDurationUs.Value, c.FrameRate);
        WorkerExportContract.ValidatePlanLimits(c.CanvasWidth, c.CanvasHeight, c.FrameRate.Numerator, c.FrameRate.Denominator,
            plan.TotalFrames, plan.VideoDurationUs, format, policy);
        return plan;
    }
    internal static WorkerExportReportExpectation Expectation(ProjectSnapshot snapshot, string format, bool audio, CompositionFramePlan plan,
        string? audioProfile = null, string quality = "standard", WorkerExportResourcePolicy? policy = null)
        => new(format, audio, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight,
            snapshot.Composition.FrameRate.Numerator, snapshot.Composition.FrameRate.Denominator, plan.TotalFrames, plan.VideoDurationUs, audioProfile, quality, policy);
    internal static Project ToProject(ProjectSnapshot value) => ProjectSnapshotSourceProject.Restore(value);
    internal static WorkerDiagnosticsSnapshotDto EncodeDiagnostics(RuntimeDiagnosticSnapshot snapshot)
    {
        if (snapshot.Count > WorkerExportContract.MaximumOccurrences)
            throw new WorkerProtocolException("导出实际诊断次数超过完整报告预算。", "WorkerExportReportBudgetExceeded");
        var result = new WorkerDiagnosticsSnapshotDto(snapshot.ReadAll().Select(item =>
        {
            var value = item.Diagnostic; var diagnostic = value.Diagnostic;
            return new WorkerRuntimeDiagnosticOccurrenceDto(item.Epoch, item.Sequence,
                new(value.InstanceId.Value, value.ArtifactHash, value.Stage, value.TimeUs, diagnostic.Severity.ToString(), diagnostic.Code,
                    diagnostic.Message, diagnostic.Location is null ? null : new(diagnostic.Location.Path, diagnostic.Location.Line, diagnostic.Location.Column),
                    value.PackageIdentity is null ? null : new(value.PackageIdentity.PackageId, value.PackageIdentity.Revision, value.PackageIdentity.ExportId)));
        }).ToArray(), snapshot.HasError);
        WorkerExportContract.ValidateDiagnostics(result); return result;
    }
    private static async Task ValidateAudioAsync(WorkerExportAudioGrant audio, CompositionFramePlan plan, CancellationToken token)
    {
        CheckPath(audio.Path, false, false);
        if (new FileInfo(audio.Path).Length != audio.Size) throw Fail("WorkerExportAudioMismatch");
        FileStream stream = new(audio.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Exception? primary = null;
        try
        {
            if (AudioProfile(audio) == BackendAudioInputProfiles.Pcm16Wave)
            {
                if (WorkerExportPcmWave.Validate(stream, audio.Size) < plan.VideoDurationUs) throw Fail("WorkerExportAudioTooShort");
            }
            else
            {
                BackendAudioInputInfo info;
                try { info = BackendAudioInputClassifier.Classify(stream, audio.Size); }
                catch (WorkerProtocolException failure) { throw Fail("WorkerExportAudioUnsupported", failure); }
                if (info.AudioProfile != AudioProfile(audio)) throw Fail("WorkerExportAudioUnsupported");
            }
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (hash != audio.Sha256) throw Fail("WorkerExportAudioMismatch");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerExportAudioReadCleanupFailure"] = cleanup; }
        }
        CheckPath(audio.Path, false, false);
        if (new FileInfo(audio.Path).Length != audio.Size) throw Fail("WorkerExportAudioMismatch");
    }
    private static string AudioProfile(WorkerExportAudioGrant audio)
        => BackendAudioInputProfiles.TryResolveExportAudioProfile(audio.AudioProfile, out string resolved)
            ? resolved : throw Fail("WorkerExportAudioUnsupported");
    private static string AudioFormat(WorkerExportAudioGrant audio) => BackendAudioInputProfiles.GetInputFormat(AudioProfile(audio));
    internal static void ValidateCompressedProbe(AudioStreamInfo? info, string format)
    {
        bool identity = format switch
        {
            "mp3" => info?.CodecName == "mp3" && info.ContainerName == "mp3",
            "flac" => info?.CodecName == "flac" && info.ContainerName == "flac",
            "aac" => info?.CodecName == "aac" && info.ContainerName == "aac",
            "mov" => info is not null && AllowedMovContainer(info.ContainerName),
            "ogg" => info?.ContainerName == "ogg" && info.CodecName is "vorbis" or "opus",
            _ => false,
        };
        if (!identity || info is null || info.SampleRate is < 1 or > 192000 || info.Channels is not (1 or 2))
            throw Fail("WorkerExportAudioUnsupported");
    }
    private static bool AllowedMovContainer(string container)
        => container.Split(',', StringSplitOptions.None) is { Length: > 0 } members
            && members.All(static member => member is "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2");
    internal static async Task<long> CountCompressedSamplesAsync(IResampledPcmSampleReader reader, string path,
        int sampleRate, CancellationToken token, long? maximumDurationUs = WorkerExportContract.MaximumCompressedAudioDurationUs,
        bool enforceBudget = true)
    {
        if (sampleRate is < 1 or > 192000) throw Fail("WorkerExportAudioUnsupported");
        long samples = 0; bool budgetExceeded = false;
        long? maximum = enforceBudget && maximumDurationUs is { } duration
            ? checked((long)((Int128)duration * sampleRate / 1_000_000)) : null;
        try
        {
            long actual = await reader.ReadMonoSamplesAtRateAsync(path, sampleRate, data =>
            {
                token.ThrowIfCancellationRequested();
                if (maximum is { } limit && data.Length > limit - samples) { budgetExceeded = true; throw Fail("WorkerExportAudioBudgetExceeded"); }
                samples += data.Length;
                return ValueTask.CompletedTask;
            }, token).ConfigureAwait(false);
            if (samples <= 0 || actual != samples) throw Fail("WorkerExportAudioDecodeFailed");
            return samples;
        }
        catch (Exception failure) when (budgetExceeded) { throw Fail("WorkerExportAudioBudgetExceeded", failure); }
        catch (IOException failure) { throw Fail("WorkerExportAudioDecodeFailed", failure); }
    }
    private sealed class RestrictedCompressedReader(string executable, string format) : IResampledPcmSampleReader
    {
        public Task<long> ReadMonoSamplesAsync(string filePath, Func<ReadOnlyMemory<float>, ValueTask> onSamples,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ReadMonoSamplesAtRateAsync(string filePath, int sampleRate, Func<ReadOnlyMemory<float>, ValueTask> onSamples,
            CancellationToken cancellationToken = default)
            => new FfmpegPcmReader(executable).ReadMonoSamplesAtRateAsync(filePath, sampleRate, format, onSamples, cancellationToken);
    }
    private static void CheckPath(string path, bool isDirectory, bool mayBeMissing)
    {
        string full = Path.GetFullPath(path);
        if (new FileInfo(full).LinkTarget is not null || new DirectoryInfo(full).LinkTarget is not null) throw Fail("WorkerExportPathRejected");
        DirectoryInfo? parent = isDirectory ? new(full) : new FileInfo(full).Directory;
        while (parent is not null) { if (parent.LinkTarget is not null) throw Fail("WorkerExportPathRejected"); parent = parent.Parent; }
        if (!mayBeMissing && !(isDirectory ? Directory.Exists(full) : File.Exists(full))) throw Fail("WorkerExportPathRejected");
    }
    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Stage(ExportStage value) => value switch
    { ExportStage.Committing => "preparing-result", ExportStage.Completed => "prepared", _ => value.ToString().ToLowerInvariant() };
    private static RenderSessionFailureException Fail(string code, Exception? inner = null) => new(code, "固定静态导出执行或能力校验失败。", innerException: inner);
    private static BackendDiagnosticDto MapDiagnostic(RuntimeDiagnosticOccurrence occurrence)
    {
        var runtime = occurrence.Diagnostic; var value = runtime.Diagnostic;
        return new(value.Code, value.Severity.ToString().ToLowerInvariant(), value.Message, null,
            new(occurrence.Epoch, Text(occurrence.Sequence), runtime.InstanceId.Value, runtime.ArtifactHash, runtime.Stage, Text(runtime.TimeUs),
                value.Location is null ? null : new(value.Location.Path, value.Location.Line, value.Location.Column),
                runtime.PackageIdentity is null ? null : new(runtime.PackageIdentity.PackageId, runtime.PackageIdentity.Revision, runtime.PackageIdentity.ExportId)), "runtime");
    }
    private sealed class NoFallbackPipeline : IRenderPipeline
    { public string BackendName => "WorkerOwnedSession"; public RenderFrame Render(RenderRequest request) => throw Fail("WorkerExportSessionRequired"); }
    private sealed class InlineProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => report(value); }
    private sealed class StrictEncoder(IMediaEncoder inner, WorkerExportMetadata metadata, CompositionFramePlan plan) : IMediaEncoder
    {
        public Task<IVideoEncodingSession> StartAsync(VideoEncodingRequest request, CancellationToken cancellationToken = default)
        {
            if (request.TotalFrames != plan.TotalFrames || request.OutputPath != metadata.CandidatePath || request.AudioPath != metadata.Audio?.Path)
                throw Fail("WorkerExportSourceMismatch");
            return inner.StartAsync(request with { AudioInputFormat = metadata.Audio is { } audio ? AudioFormat(audio) : null,
                MaximumOutputBytes = metadata.ResourcePolicy?.MaxMediaBytes }, cancellationToken);
        }
    }
    private sealed class StrictProbe(FfmpegMediaProbe inner, WorkerExportMetadata metadata, CompositionFramePlan plan, Composition composition,
        AudioStreamInfo? decodedAudio) : IMediaProbe
    {
        public MediaFileInfo? Produced { get; private set; }
        public Task<AudioStreamInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!metadata.IncludeAudio || metadata.Audio?.Path != filePath) throw Fail("WorkerExportAudioMismatch");
            cancellationToken.ThrowIfCancellationRequested();
            return decodedAudio is not null ? Task.FromResult<AudioStreamInfo?>(decodedAudio) : inner.ProbeAsync(filePath, "wav", cancellationToken);
        }
        public async Task<MediaFileInfo?> ProbeFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var value = await inner.ProbeFileAsync(filePath, true, "mov", cancellationToken).ConfigureAwait(false);
            var video = value?.Video; var audio = value?.Audio;
            bool alpha = metadata.Format == "transparentProRes4444";
            if (video is null || video.DecodedFrameCount != plan.TotalFrames || video.HasAlpha != alpha
                || video.CodecName != (alpha ? "prores" : "h264") || video.Width != composition.CanvasWidth || video.Height != composition.CanvasHeight
                || video.FrameRateNumerator < 1 || video.FrameRateDenominator < 1
                || (long)video.FrameRateNumerator * composition.FrameRate.Denominator != (long)composition.FrameRate.Numerator * video.FrameRateDenominator
                || Math.Abs((video.DurationUs > 0 ? video.DurationUs : value!.DurationUs) - plan.VideoDurationUs) > 200_000
                || metadata.IncludeAudio && (audio is null || audio.CodecName != (alpha ? "pcm_s16le" : "aac")
                    || audio.DurationUs <= 0 || Math.Abs(audio.DurationUs - plan.VideoDurationUs) > 50_000)
                || !metadata.IncludeAudio && audio is not null) throw Fail("WorkerExportMediaVerificationFailed");
            Produced = value; return value;
        }
    }
    private sealed class StrictFactory(IRenderFrameSessionFactory inner, WorkerExportMetadata metadata) : IRenderFrameSessionFactory
    {
        public async ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
            => new Session(await inner.CreateAsync(snapshot, cancellationToken).ConfigureAwait(false), metadata);
        private sealed class Session(IRenderFrameSession inner, WorkerExportMetadata metadata) : IRenderFrameSession
        {
            private readonly RuntimeDiagnosticCursor _cursor = new(); private int _occurrences, _bytes;
            public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                RenderFrame? frame = null;
                try
                {
                    frame = await inner.RenderAsync(request, cancellationToken).ConfigureAwait(false);
                    if (frame.DiagnosticSnapshot.HasError) throw new RenderSessionFailureException("WorkerRuntimeDiagnosticError", "实际Error禁止导出。", frame.DiagnosticSnapshot);
                    if (!frame.MaterialDiagnostics.IsEmpty || !frame.ImageEffectDiagnostics.IsEmpty) throw Fail("WorkerExportDiagnosticsUnsupported");
                    foreach (var occurrence in _cursor.ReadNew(frame.DiagnosticSnapshot))
                    {
                        if (++_occurrences > WorkerExportContract.MaximumOccurrences) throw Fail("WorkerExportReportBudgetExceeded");
                        _bytes += WorkerExportContract.EncodeJson(MapDiagnostic(occurrence), WorkerExportContract.MaximumReportBytes).Length + 1;
                        if (_bytes > WorkerExportContract.MaximumReportBytes - 4096) throw Fail("WorkerExportReportBudgetExceeded");
                    }
                    if (frame.Format != PixelFormat.Bgra8888Premultiplied || frame.Width != request.ResolveTarget().PixelWidth
                        || frame.Height != request.ResolveTarget().PixelHeight || frame.TimeUs != request.TimeUs
                        || request.Quality != RenderQuality.Final || request.TransparentBackground != (metadata.Format == "transparentProRes4444"))
                        throw Fail("WorkerExportFrameMismatch");
                    return frame;
                }
                catch (Exception failure) { frame?.Dispose(); if (failure is OperationCanceledException or RenderSessionFailureException) throw;
                    throw Fail("WorkerExportRenderFailed", failure); }
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
    private static async Task<WorkerExportCapabilitiesDto> ProbeCapabilitiesAsync(WorkerExportMetadata metadata, CancellationToken token)
    {
        try
        {
            string encoders = await ToolAsync(metadata.FfmpegPath, ["-hide_banner", "-encoders"], token).ConfigureAwait(false);
            StaticAudioAnalysisTask.RequireTools(await ToolAsync(metadata.FfmpegPath, ["-hide_banner", "-demuxers"], token).ConfigureAwait(false), ["wav", "mp3", "flac", "aac", "mov", "ogg"]);
            StaticAudioAnalysisTask.RequireTools(await ToolAsync(metadata.FfmpegPath, ["-hide_banner", "-decoders"], token).ConfigureAwait(false), ["pcm_s16le", "mp3", "flac", "aac", "vorbis", "opus"], decoders: true);
            StaticAudioAnalysisTask.RequireTools(await ToolAsync(metadata.FfprobePath, ["-hide_banner", "-demuxers"], token).ConfigureAwait(false), ["wav", "mp3", "flac", "aac", "mov", "ogg"]);
            bool pcm = encoders.Contains("pcm_s16le", StringComparison.Ordinal);
            return new(true, pcm && encoders.Contains("libx264", StringComparison.Ordinal) && encoders.Contains(" aac ", StringComparison.Ordinal),
                pcm && encoders.Contains("prores_ks", StringComparison.Ordinal), EngineIdentity: StaticProjectSnapshotCodec.EngineIdentity,
                SourceProfiles: [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets,
                    BackendSnapshotProfiles.PackageLyricsScene, BackendSnapshotProfiles.PackageLyricsScenePresets, ProjectRuntimeInputCodec.Profile],
                AudioProfiles: [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                    BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg],
                ExecutionProfiles: [WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile]);
        }
        catch (Exception failure) when (failure is IOException or System.ComponentModel.Win32Exception)
        { return new(true, false, false, "WorkerExportToolUnavailable", StaticProjectSnapshotCodec.EngineIdentity,
            [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets,
                BackendSnapshotProfiles.PackageLyricsScene, BackendSnapshotProfiles.PackageLyricsScenePresets, ProjectRuntimeInputCodec.Profile],
            [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg],
            [WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile]); }
    }
    private static async Task<string> ToolAsync(string path, string[] arguments, CancellationToken token)
    {
        string output = "";
        var result = await Cli.Wrap(path).WithArguments(arguments).WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Create(async (stream, cancellation) => { using StreamReader reader = new(stream);
                output = await BoundedTextReader.ReadToEndAsync(reader, 256 * 1024, true, cancellation).ConfigureAwait(false); }))
            .WithStandardErrorPipe(PipeTarget.Create(async (stream, cancellation) => { using StreamReader reader = new(stream);
                await BoundedTextReader.ReadToEndAsync(reader, 16 * 1024, false, cancellation).ConfigureAwait(false); }))
            .ExecuteAsync(token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("固定媒体工具能力探测失败。");
        return output;
    }
}
