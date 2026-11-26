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
        Func<WorkerMessage, Task> send, CancellationToken token, string? renderBackend = null, long? enforcedMediaLimitBytes = null)
        => ExecuteAsync(request, guards, send, null, token, renderBackend: renderBackend, enforcedMediaLimitBytes: enforcedMediaLimitBytes);

    internal static async Task<WorkerMessage> ExecuteAsync(WorkerMessage request, bool guards,
        Func<WorkerMessage, Task> send, IRenderFrameSessionFactory? sessions, CancellationToken token,
        string? renderBackend = null, long? enforcedMediaLimitBytes = null)
    {
        bool capabilities = request.Kind == WorkerExportContract.CapabilitiesKind;
        WorkerExportInput input = WorkerExportContract.DecodeInput(request.Payload, capabilities);
        WorkerExportMetadata metadata = input.Metadata;
        ReadOnlyMemory<byte> inputSnapshot = input.SnapshotBytes;
        string inputSourceSha = input.SourceSha256;
        Guid taskId = input.TaskId;
        WorkerExportContract.ValidateCorrelation(request, request);
        if (request.TaskId != taskId.ToString("N") || request.SourceSha256 != inputSourceSha
            || request.ExportFormat != metadata.Format || request.ExportIncludeAudio != metadata.IncludeAudio)
            throw Fail("WorkerExportProtocolInvalid");
        if (!guards) throw Fail("WorkerExportUnavailable");
        if (capabilities)
        {
            var supported = await ProbeCapabilitiesAsync(metadata, token).ConfigureAwait(false);
            return Result(WorkerExportContract.CapabilitiesResultKind, WorkerExportContract.EncodeJson(supported, 4096), 0);
        }
        bool compressedAudio = metadata.ExecutionProfile == WorkerExportContract.CompressedExecutionProfile;
        var prepared = WorkerScriptInputs.Read(inputSnapshot, token); inputSnapshot = prepared.Source;
        using ProjectRuntimeInput runtimeInput = await ProjectRuntimeInput.OpenAsync(inputSnapshot, token, prepared.Compiler).ConfigureAwait(false);
        ProjectSnapshot snapshot = runtimeInput.Snapshot;
        if (runtimeInput.SourceProfile != metadata.CodecProfile) throw Fail("WorkerExportSourceMismatch");
        CompositionFramePlan plan = ValidatePlan(snapshot, metadata.Format, metadata.ResourcePolicy);
        Project project = ToProject(snapshot);
        ProjectRuntimeInputCodec.ValidateSnapshot(inputSnapshot, ProjectSnapshot.FromProject(project));
        if (metadata.IncludeAudio && (snapshot.AudioPath is null || snapshot.AudioMedia is { } media
            && (media.Hash != metadata.Audio!.Sha256 || media.ByteLength != metadata.Audio.Size))) throw Fail("WorkerExportAudioMismatch");
        long? sourceAudioDuration = null;
        AudioStreamInfo? decodedAudio = null;
        if (metadata.Audio is { } audio)
        {
            await ValidateAudioAsync(audio, token).ConfigureAwait(false);
            string format = AudioFormat(audio);
            var actual = await new FfmpegMediaProbe(metadata.FfprobePath).ProbeAsync(audio.Path, format, token).ConfigureAwait(false);
            if (compressedAudio)
            {
                ValidateCompressedProbe(actual, format);
                long samples = await CountCompressedSamplesAsync(new RestrictedCompressedReader(metadata.FfmpegPath, format), audio.Path,
                    actual!.SampleRate, token, metadata.ResourcePolicy?.MaxCompressedAudioDurationUs,
                    enforceBudget: metadata.ResourcePolicy is not null).ConfigureAwait(false);
                sourceAudioDuration = checked((long)((Int128)samples * 1_000_000 / actual.SampleRate));
                decodedAudio = actual with { DurationUs = sourceAudioDuration.Value };
                await ValidateAudioAsync(audio, token).ConfigureAwait(false);
            }
            else
            {
                // Keep the original precise WAV path and its report values.
                if (actual is null || actual.CodecName != "pcm_s16le" || actual.Channels is not (1 or 2) || actual.DurationUs <= 0)
                    throw Fail("WorkerExportAudioUnsupported");
                sourceAudioDuration = actual.DurationUs;
            }
            project = project with { AudioPath = audio.Path, AudioMedia = project.AudioMedia ?? new(audio.Sha256, audio.Size, "audio.wav") };
        }
        CheckPath(metadata.CandidatePath!, isDirectory: false, mayBeMissing: true);
        if (File.Exists(metadata.CandidatePath)) throw Fail("WorkerExportCandidateExists");
        IRenderFrameSessionFactory factory = sessions is null || metadata.CodecProfile == ProjectRuntimeInputCodec.Profile
            ? runtimeInput.CreateFactory(renderBackend: renderBackend, graphPixelChecks: false)
            : sessions;
        long? effectiveMaxMediaBytes = (metadata.ResourcePolicy?.MaxMediaBytes, enforcedMediaLimitBytes) switch
        {
            (long policyMax, long envMax) => Math.Min(policyMax, envMax),
            (long policyMax, null) => policyMax,
            (null, long envMax) => envMax,
            _ => null,
        };
        BackendFallbackDiagnosticCollector fallback = new();
        ExportRuntimeDiagnosticCollector runtimeDiagnostics = new();
        var strict = new StrictFactory(factory, metadata, snapshot.MediaFeatureInput, fallback, runtimeDiagnostics);
        var probe = new StrictProbe(new FfmpegMediaProbe(metadata.FfprobePath), metadata, plan, snapshot.Composition, decodedAudio);
        var encoder = new StrictEncoder(new FfmpegMediaEncoder(metadata.FfmpegPath), metadata, plan, effectiveMaxMediaBytes);
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
                    AudioEncodingPlanOverride = compressedAudio ? CompressedAudioEncodingPlan(metadata.Format) : null,
                    NormalizeAudioDuration = metadata.IncludeAudio, FallbackWarnings = false },
                new InlineProgress(value => channel.Writer.TryWrite((++sequence, value))), operation.Token).ConfigureAwait(false);
            if (metadata.Audio is { } pinned) await ValidateAudioAsync(pinned, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            MediaFileInfo produced = probe.Produced ?? throw Fail("WorkerExportMediaVerificationFailed");
            var video = produced.Video!;
            FileInfo file = new(metadata.CandidatePath!); CheckPath(file.FullName, false, false);
            long actualLength = ReadActualFileLength(file.FullName);
            if (actualLength <= 0 || actualLength > effectiveMaxMediaBytes) throw Fail("WorkerExportArtifactBudgetExceeded");
            await using FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            string sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            var dto = new ExportReportDto(metadata.Format, metadata.IncludeAudio, plan.TotalFrames, checked((int)video.DecodedFrameCount!.Value),
                original.FramesWritten, original.DroppedFrames, original.FailedFrames, video.Width, video.Height,
                snapshot.Composition.FrameRate.Numerator, snapshot.Composition.FrameRate.Denominator, Text(plan.VideoDurationUs),
                Text(video.DurationUs > 0 ? video.DurationUs : produced.DurationUs), sourceAudioDuration is { } duration ? Text(duration) : null,
                video.CodecName, produced.Audio?.CodecName, video.PixelFormat!, video.HasAlpha, actualLength, sha,
                [.. original.Warnings.Take(WorkerExportContract.MaximumWarnings).Select(Sanitize)], runtimeDiagnostics.ToArray(), original.IsVerified,
                produced.Audio is { } outputAudio ? Text(outputAudio.DurationUs) : null, true, metadata.Quality,
                original.Quality?.ToString().ToLowerInvariant() ?? throw Fail("WorkerExportQualityMismatch"),
                FallbackDiagnostics: fallback.ToArray(), FallbackFrameCount: fallback.FrameCount, FallbackDiagnosticsTruncated: fallback.Truncated);
            dto = FitReportBudget(dto);
            byte[] payload = WorkerExportContract.EncodeReport(dto, Expectation(snapshot, metadata.Format, metadata.IncludeAudio, plan, metadata.Audio?.AudioProfile, metadata.Quality, metadata.ResourcePolicy));
            channel.Writer.TryComplete(); await pump.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Result(WorkerExportContract.ResultKind, payload, sequence);
        }
        catch (Exception failure)
        {
            primary = failure;
            if (budgetExceeded || failure is OutputSizeLimitExceededException) throw Fail("WorkerExportArtifactBudgetExceeded", failure);
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
                    await send(new(WorkerExportContract.ProgressKind, request.RequestId, request.InputSha256,
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
                if (effectiveMaxMediaBytes is null) return;
                using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(40));
                while (await timer.WaitForNextTickAsync(operation.Token).ConfigureAwait(false))
                    foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(metadata.CandidatePath!)!))
                    {
                        if (TryReadActualFileLength(path) > effectiveMaxMediaBytes) { budgetExceeded = true; operation.Cancel(); return; }
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

    /// <summary>兜底诊断、告警或非错误运行时诊断让完整报告超出预算时从末尾裁剪；非错误诊断不让导出失败。</summary>
    internal static ExportReportDto FitReportBudget(ExportReportDto report)
    {
        ExportReportDto current = report;
        int size = WorkerExportContract.EncodeJson(current, int.MaxValue).Length;
        if (size <= WorkerExportContract.MaximumReportBytes) return current;
        if (current.FallbackDiagnostics.Length > 0)
        {
            int count = current.FallbackDiagnostics.Length;
            while (size > WorkerExportContract.MaximumReportBytes && count > 0)
            {
                count--;
                size -= WorkerExportContract.EncodeJson(current.FallbackDiagnostics[count], int.MaxValue).Length + (count > 0 ? 1 : 0);
            }
            current = current with { FallbackDiagnostics = current.FallbackDiagnostics[..count], FallbackDiagnosticsTruncated = true };
            while (current.FallbackDiagnostics.Length > 0
                && WorkerExportContract.EncodeJson(current, int.MaxValue).Length > WorkerExportContract.MaximumReportBytes)
                current = current with { FallbackDiagnostics = current.FallbackDiagnostics[..^1], FallbackDiagnosticsTruncated = true };
            size = WorkerExportContract.EncodeJson(current, int.MaxValue).Length;
            if (size <= WorkerExportContract.MaximumReportBytes) return current;
        }
        if (current.Warnings.Length > 0)
        {
            int count = current.Warnings.Length;
            while (size > WorkerExportContract.MaximumReportBytes && count > 0)
            {
                count--;
                size -= WorkerExportContract.EncodeJson(current.Warnings[count], int.MaxValue).Length + (count > 0 ? 1 : 0);
            }
            current = current with { Warnings = current.Warnings[..count] };
            while (current.Warnings.Length > 0
                && WorkerExportContract.EncodeJson(current, int.MaxValue).Length > WorkerExportContract.MaximumReportBytes)
                current = current with { Warnings = current.Warnings[..^1] };
            size = WorkerExportContract.EncodeJson(current, int.MaxValue).Length;
            if (size <= WorkerExportContract.MaximumReportBytes) return current;
        }
        if (current.Diagnostics.Length > 0)
        {
            // 从末尾裁剪仅移除各自代次的最高序号，保留其余每代次 1..k 连续性。
            int count = current.Diagnostics.Length;
            while (size > WorkerExportContract.MaximumReportBytes && count > 0)
            {
                count--;
                size -= WorkerExportContract.EncodeJson(current.Diagnostics[count], int.MaxValue).Length + (count > 0 ? 1 : 0);
            }
            current = current with { Diagnostics = current.Diagnostics[..count] };
            while (current.Diagnostics.Length > 0
                && WorkerExportContract.EncodeJson(current, int.MaxValue).Length > WorkerExportContract.MaximumReportBytes)
                current = current with { Diagnostics = current.Diagnostics[..^1] };
        }
        return current;
    }

    internal static (string Code, RuntimeDiagnosticSnapshot? Diagnostics, RenderSourceMaterialFailure? SourceMaterial) ClassifyFailure(
        Exception failure, string fallbackCode)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is RenderSessionFailureException session)
            return (session.Code, session.DiagnosticSnapshot is { Count: > 0 } snap ? snap : null, session.SourceMaterialFailure);
        if (RenderFailureDiagnostics.ExtractSourceMaterial(failure) is { } source)
            return (source.Code, null, source);
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is EffectFailureException effect)
                return (effect.Code, effect.DiagnosticSnapshot is { Count: > 0 } snap ? snap : null, null);
        }
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            string? code = current switch
            {
                WorkerProtocolException protocol => protocol.Code,
                SnapshotContractException snapshot => snapshot.Code,
                RenderSessionFailureException nested => nested.Code,
                PreparedFrameException prepared => prepared.Code,
                ImageGraphException graph => graph.Code,
                ImageSurfaceBudgetException budget => budget.Code,
                ShaderProgramException shader => shader.Code,
                EffectPackageException package => package.Code,
                ImageBackgroundDecodeException bg => bg.Code,
                EffectOutputException output => output.Code,
                GpuPreviewException gpu => gpu.Code,
                WorkerResourceMediaException media => media.Code,
                ProjectEffectCompilationException => "WorkerExportCompilationFailed",
                OutputSizeLimitExceededException => "WorkerExportArtifactBudgetExceeded",
                TimeoutException => "WorkerExportTimedOut",
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(code)) return (code, null, null);
        }
        return (fallbackCode, null, null);
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
    private static async Task ValidateAudioAsync(WorkerExportAudioGrant audio, CancellationToken token)
    {
        CheckPath(audio.Path, false, false);
        if (new FileInfo(audio.Path).Length != audio.Size) throw Fail("WorkerExportAudioMismatch");
        FileStream stream = new(audio.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Exception? primary = null;
        try
        {
            if (AudioProfile(audio) == BackendAudioInputProfiles.Pcm16Wave)
            {
                _ = WorkerExportPcmWave.Validate(stream, audio.Size);
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
    private sealed class StrictEncoder(IMediaEncoder inner, WorkerExportMetadata metadata, CompositionFramePlan plan, long? effectiveMaxMediaBytes) : IMediaEncoder
    {
        public Task<IVideoEncodingSession> StartAsync(VideoEncodingRequest request, CancellationToken cancellationToken = default)
        {
            if (request.TotalFrames != plan.TotalFrames || request.OutputPath != metadata.CandidatePath || request.AudioPath != metadata.Audio?.Path)
                throw Fail("WorkerExportSourceMismatch");
            return inner.StartAsync(request with { AudioInputFormat = metadata.Audio is { } audio ? AudioFormat(audio) : null,
                MaximumOutputBytes = effectiveMaxMediaBytes }, cancellationToken);
        }
    }
    private static long TryReadActualFileLength(string path)
    {
        try { return ReadActualFileLength(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException) { return 0L; }
    }
    private static long ReadActualFileLength(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
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
    private sealed class ExportRuntimeDiagnosticCollector
    {
        private const int MaximumRemembered = 2048;
        private const int MaximumDiagnosticsBytes = WorkerExportContract.MaximumReportBytes - 16 * 1024;
        private readonly RuntimeDiagnosticCursor _cursor = new();
        private readonly HashSet<DiagnosticKey> _seen = [];
        private readonly Dictionary<Guid, long> _epochSequences = [];
        private readonly List<BackendDiagnosticDto> _items = [];
        private int _bytes = 2;

        public void Add(RuntimeDiagnosticSnapshot snapshot)
        {
            foreach (var occurrence in _cursor.ReadNew(snapshot))
            {
                if (occurrence.Diagnostic.Diagnostic.Severity == DiagnosticSeverity.Error) continue;
                if (_items.Count >= WorkerExportContract.MaximumOccurrences) continue;
                DiagnosticKey key = DiagnosticKey.From(occurrence.Diagnostic);
                if (_seen.Contains(key)) continue;
                if (_seen.Count >= MaximumRemembered) continue;
                _seen.Add(key);
                long nextSequence = _epochSequences.TryGetValue(occurrence.Epoch, out long previous) ? previous + 1 : 1;
                BackendDiagnosticDto dto = MapDiagnostic(occurrence with { Sequence = nextSequence });
                int itemBytes = WorkerExportContract.EncodeJson(dto, int.MaxValue).Length + (_items.Count == 0 ? 0 : 1);
                if (_bytes + itemBytes > MaximumDiagnosticsBytes) continue;
                _epochSequences[occurrence.Epoch] = nextSequence;
                _items.Add(dto);
                _bytes += itemBytes;
            }
        }

        public BackendDiagnosticDto[] ToArray() => [.. _items];

        private readonly record struct DiagnosticKey(EffectInstanceId InstanceId, string ArtifactHash, string Stage,
            DiagnosticSeverity Severity, string Code, string Message, SourceLocation? Location, EffectPackageIdentity? PackageIdentity)
        {
            public static DiagnosticKey From(RuntimeDiagnostic value) => new(value.InstanceId, value.ArtifactHash, value.Stage,
                value.Diagnostic.Severity, value.Diagnostic.Code, value.Diagnostic.Message, value.Diagnostic.Location, value.PackageIdentity);
        }
    }

    private sealed class StrictFactory(IRenderFrameSessionFactory inner, WorkerExportMetadata metadata,
        MakoKaraGenerator.Core.Media.MediaFeatureInput? media, BackendFallbackDiagnosticCollector fallback,
        ExportRuntimeDiagnosticCollector runtimeDiagnostics) : IRenderFrameSessionFactory
    {
        public async ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            // 原导出器从 Project 重建快照，固定媒体输入只存在于运行输入快照；按同一固定输入补回，
            // 不同输入仍由会话准入按宿主绑定拒绝。逐帧请求携带原导出器快照，同样换成补回后的实例。
            ProjectSnapshot exporter = snapshot;
            if (media is not null && snapshot.MediaFeatureInput is null) snapshot = snapshot with { MediaFeatureInput = media };
            try
            {
                return new Session(await inner.CreateAsync(snapshot, cancellationToken).ConfigureAwait(false), metadata, exporter, snapshot, fallback, runtimeDiagnostics);
            }
            catch (Exception failure) when (failure is not OperationCanceledException and not RenderSessionFailureException)
            {
                var (code, diagnostics, source) = ClassifyFailure(failure, "WorkerExportRenderFailed");
                throw new RenderSessionFailureException(code, source is not null ? "固定源材质未能完成导出。" : "固定静态导出渲染会话初始化失败。",
                    diagnostics, failure)
                { SourceMaterialFailure = source };
            }
        }
        private sealed class Session(IRenderFrameSession inner, WorkerExportMetadata metadata,
            ProjectSnapshot exporter, ProjectSnapshot fixedInput, BackendFallbackDiagnosticCollector fallback,
            ExportRuntimeDiagnosticCollector runtimeDiagnostics) : IRenderFrameSession
        {
            public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                RenderFrame? frame = null;
                try
                {
                    if (!ReferenceEquals(exporter, fixedInput) && ReferenceEquals(request.Project, exporter)) request = request with { Project = fixedInput };
                    frame = await inner.RenderAsync(request, cancellationToken).ConfigureAwait(false);
                    if (frame.DiagnosticSnapshot.HasError) throw new RenderSessionFailureException("WorkerRuntimeDiagnosticError", "实际Error禁止导出。", frame.DiagnosticSnapshot);
                    // 非 Error 诊断（Info/Warning，如逐帧编排提示）跨帧去重并封顶，不因长视频帧数超过 512 而中断导出。
                    runtimeDiagnostics.Add(frame.DiagnosticSnapshot);
                    if (frame.Format != PixelFormat.Bgra8888Premultiplied || frame.Width != request.ResolveTarget().PixelWidth
                        || frame.Height != request.ResolveTarget().PixelHeight || frame.TimeUs != request.TimeUs
                        || request.Quality != RenderQuality.Final || request.TransparentBackground != (metadata.Format == "transparentProRes4444"))
                        throw Fail("WorkerExportFrameMismatch");
                    // 缺背景的图效果／材质已按声明兜底，照预览继续导出；去重后随报告返回。
                    fallback.Add(frame);
                    return frame;
                }
                catch (Exception failure)
                {
                    frame?.Dispose();
                    if (failure is OperationCanceledException or RenderSessionFailureException) throw;
                    var (code, diagnostics, source) = ClassifyFailure(failure, "WorkerExportRenderFailed");
                    throw new RenderSessionFailureException(code, source is not null ? "固定源材质未能完成导出。" : "固定静态导出渲染失败。",
                        diagnostics, failure)
                    { SourceMaterialFailure = source };
                }
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
