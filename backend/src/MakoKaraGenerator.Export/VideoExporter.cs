using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Export;

/// <summary>
/// 把工程导出成一段带音轨的成片（docs/development.md §12.4 的流程）。
/// </summary>
/// <remarks>
/// <para>
/// 流程刻意与文档里的顺序一致：校验 → 快照 → 探测 → 建编码器 → 按帧渲染写入 → 合并音频 →
/// 完成暂存封装 → 校验产物及报告 → 释放运行资源 → 最后取消检查 → 发布最终文件。
/// 发布前能取消，暂存产物的所有权由编码会话转交导出事务；发布成功即如实返回成功。
/// </para>
/// <para>
/// <b>画面来自与预览完全相同的渲染管线</b>（docs/development.md §3.2 的底线）：
/// 同一个 <see cref="IRenderPipeline"/>、同一个 <see cref="ProjectSnapshot"/>、
/// 同一套时间采样，因此"预览什么样、成片就什么样"不是靠两边对着改出来的，而是结构上就只能一致。
/// </para>
/// <para>
/// 普通单帧后端错误沿用占位并报告；效果执行、工作进程中立失败或准备帧协议错误必须中断导出，
/// 不能用无效果原画／背景占位伪装成成功作品。
/// </para>
/// </remarks>
public sealed class VideoExporter
{
    private readonly IRenderPipeline _pipeline;
    private readonly IMediaProbe _probe;
    private readonly IMediaEncoder _encoder;
    private readonly IRenderFrameSessionFactory? _sessionFactory;

    /// <summary>创建一个导出器。</summary>
    /// <param name="pipeline">渲染管线（与预览共用的那一个后端）。</param>
    /// <param name="probe">媒体探测器，用于读音频信息与校验产物。</param>
    /// <param name="encoder">编码器。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    /// <param name="sessionFactory">可选的固定资源／准备／绘制工厂；每次导出建立独立会话。</param>
    public VideoExporter(IRenderPipeline pipeline, IMediaProbe probe, IMediaEncoder encoder, IRenderFrameSessionFactory? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(encoder);

        _pipeline = pipeline;
        _probe = probe;
        _encoder = encoder;
        _sessionFactory = sessionFactory;
    }

    /// <summary>导出一次。</summary>
    /// <param name="project">工程。</param>
    /// <param name="options">导出选项。</param>
    /// <param name="progress">进度回调；可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>导出报告。</returns>
    /// <exception cref="ArgumentNullException">工程或选项为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="InvalidOperationException">工程未通过导出校验时抛出（消息里列出原因）。</exception>
    /// <exception cref="IOException">编码或探测失败时抛出。</exception>
    /// <exception cref="OperationCanceledException">被取消时抛出。</exception>
    public async Task<ExportReport> ExportAsync(
        Project project,
        VideoExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        Stopwatch stopwatch = Stopwatch.StartNew();
        ImmutableArray<string>.Builder warnings = ImmutableArray.CreateBuilder<string>();
        ImmutableArray<string>.Builder missing = ImmutableArray.CreateBuilder<string>();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ExportProgress(ExportStage.Validating, 0, 0, stopwatch.Elapsed));

        EnsureExportable(project);

        Composition composition = project.Composition;
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);

        progress?.Report(new ExportProgress(ExportStage.Probing, 0, 0, stopwatch.Elapsed));

        (string? audioPath, AudioStreamInfo? audioInfo) = await ResolveAudioAsync(
            project,
            options,
            missing,
            warnings,
            cancellationToken).ConfigureAwait(false);

        (long durationUs, FrameRate frameRate) = ResolveTiming(composition, audioInfo);
        RenderTarget target = RenderTarget.FromComposition(composition);
        var renderSession = _sessionFactory is null ? null : await _sessionFactory.CreateAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await using var renderLifetime = new RenderFrameSessionScope(renderSession);
        int totalFrames;
        AudioEncodingPlan? audioPlan;
        VideoEncodingRequest request;
        IVideoEncodingSession session;
        try
        {
            totalFrames = ResolveTotalFrames(durationUs, frameRate);
            if (!options.Overwrite && File.Exists(options.OutputPath))
                throw new IOException($"输出文件已存在：{options.OutputPath}");
            audioPlan = audioPath is null ? null
                : options.AudioEncodingPlanOverride ?? AudioEncodingPlan.Decide(audioInfo?.CodecName);
            request = new()
            {
                OutputPath = options.OutputPath, Width = target.PixelWidth, Height = target.PixelHeight,
                FrameRate = frameRate, TotalFrames = totalFrames,
                Quality = ExportQualityPreset.For(options.Quality), Format = options.Format,
                AudioPath = audioPath, AudioPlan = audioPlan, NormalizeAudioDuration = options.NormalizeAudioDuration, Overwrite = options.Overwrite, Timeout = options.Timeout,
            };
            session = await _encoder.StartAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            renderLifetime.PrimaryFailure = failure;
            throw;
        }

        int failedFrames = 0;
        VideoEncodingArtifact? artifact = null;
        bool encodingDisposed = false;
        Exception? primaryFailure = null;
        HashSet<RenderImageEffectDiagnostic> imageWarnings = [];
        HashSet<RenderMaterialDiagnostic> materialWarnings = [];
        HashSet<(Scripting.EffectInstanceId Instance, string Artifact, string Code, string Message)> sceneWarnings = [];
        RuntimeDiagnosticCursor sceneCursor = new();
        List<RuntimeDiagnosticOccurrence> sceneOccurrences = [];

        try
        {
            int reportEvery = Math.Max(1, totalFrames / 200);

            for (int index = 0; index < totalFrames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long timeUs = FrameTimeUs(index, frameRate);

                RenderFrame frame;

                try
                {
                    var frameRequest = new RenderRequest
                    {
                        Project = snapshot,
                        TimeUs = timeUs,
                        Target = target,
                        Quality = options.RenderQuality,

                        // 透明输出：画布从全透明开始且不画背景层（EXPORT-07）。
                        TransparentBackground = options.Format.SupportsAlpha(),
                        CancellationToken = cancellationToken,
                    };
                    frame = renderSession is null ? _pipeline.Render(frameRequest) : await renderSession.RenderAsync(frameRequest, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (snapshot.EffectInputsFingerprint is null && exception is not OperationCanceledException
                    and not EffectFailureException and not PreparedFrameException and not SourceMaterialFailureException
                    and not RenderSessionFailureException)
                {
                    // 单帧渲染失败不该毁掉整部片子：记一笔，用背景色顶上，时间轴不受影响。
                    failedFrames++;
                    frame = CreateFallbackFrame(target, composition.BackgroundColor, timeUs);

                    if (failedFrames <= MaxFrameWarnings)
                    {
                        warnings.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"第 {index} 帧渲染失败（{timeUs} μs）：{exception.Message}"));
                    }
                }

                if (options.FallbackWarnings)
                {
                    foreach (var diagnostic in frame.ImageEffectDiagnostics)
                        if (imageWarnings.Add(diagnostic)) warnings.Add($"效果 {diagnostic.InstanceId.Value} 的图 {diagnostic.Graph.Identity.ExportId} 已跳过背景依赖输入 {diagnostic.InputId}（{diagnostic.Code}），使用原输入。");
                    foreach (var diagnostic in frame.MaterialDiagnostics)
                        if (materialWarnings.Add(diagnostic)) warnings.Add($"{diagnostic.OwnerDescription} 的材质 {diagnostic.Material.Identity.ExportId} 已跳过背景依赖输入 {diagnostic.InputId}（{diagnostic.Code}），使用包内基础外观。");
                }
                foreach (var occurrence in sceneCursor.ReadNew(frame.DiagnosticSnapshot))
                {
                    sceneOccurrences.Add(occurrence); var diagnostic = occurrence.Diagnostic;
                    if (sceneWarnings.Add((diagnostic.InstanceId, diagnostic.ArtifactHash, diagnostic.Diagnostic.Code, diagnostic.Diagnostic.Message)))
                        warnings.Add($"效果 {diagnostic.InstanceId.Value}（{diagnostic.Diagnostic.Code}）：{diagnostic.Diagnostic.Message}");
                }

                // 预乘 → 直乘的换算在编码器写管道那一步做（见 AlphaFrameConverter）：
                // 那里本来就要拷一遍像素，顺手换算不额外花钱。
                await session.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);

                if (index % reportEvery == 0 || index == totalFrames - 1)
                {
                    progress?.Report(new ExportProgress(
                        ExportStage.Rendering,
                        index + 1,
                        totalFrames,
                        stopwatch.Elapsed));
                }
            }

            progress?.Report(new ExportProgress(
                ExportStage.Muxing,
                totalFrames,
                totalFrames,
                stopwatch.Elapsed));

            VideoEncodingResult encoding = await session
                .CompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            artifact = encoding.Artifact;

            if (audioPlan is not null)
            {
                warnings.Add(request.Format.SupportsAlpha()
                    ? "透明 MOV 使用 pcm_s16le 音轨编码，不直接复制源音频流。"
                    : audioPlan.Reason);
            }

            progress?.Report(new ExportProgress(
                ExportStage.Verifying,
                totalFrames,
                totalFrames,
                stopwatch.Elapsed));

            ExportReport report = await BuildReportAsync(
                options,
                request,
                encoding,
                composition,
                snapshot,
                audioPath,
                audioInfo,
                audioPlan,
                frameRate,
                totalFrames,
                durationUs,
                failedFrames,
                warnings,
                missing,
                stopwatch,
                cancellationToken).ConfigureAwait(false);
            report = report with { DiagnosticOccurrences = [.. sceneOccurrences] };

            // 任何可能失败的运行资源释放也属于提交前事务。
            await renderLifetime.DisposeAsync().ConfigureAwait(false);
            encodingDisposed = true;
            await session.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ExportProgress(ExportStage.Committing, totalFrames, totalFrames, stopwatch.Elapsed));
            artifact.Publish(options.OutputPath, options.Overwrite);

            // 发布后仅通知；通知异常或迟到取消不能把已发生的提交改报为失败。
            try { progress?.Report(new ExportProgress(ExportStage.Completed, totalFrames, totalFrames, stopwatch.Elapsed)); }
            catch (Exception notificationError) { Console.Error.WriteLine("[VideoExporter] 成片已发布，完成通知失败：" + notificationError); }

            return report;
        }
        catch (Exception error)
        {
            primaryFailure = error;
            renderLifetime.PrimaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                if (!encodingDisposed) await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError) when (primaryFailure is not null)
            {
                primaryFailure.Data["EncodingSessionCleanupFailure"] = cleanupError;
            }
            finally
            {
                try { artifact?.Dispose(); }
                catch (Exception cleanupError) when (primaryFailure is not null)
                {
                    primaryFailure.Data["EncodingArtifactCleanupFailure"] = cleanupError;
                }
            }
        }
    }

    /// <summary>导出前的校验：不通过就不要开始写文件。</summary>
    /// <exception cref="InvalidOperationException">存在阻断性错误时抛出。</exception>
    private static void EnsureExportable(Project project)
    {
        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Export);

        if (!report.HasErrors)
        {
            return;
        }

        IEnumerable<string> reasons = report.Issues
            .Where(static issue => issue.Severity == IssueSeverity.Error)
            .Take(5)
            .Select(static issue => $"- {issue.Message}");

        throw new InvalidOperationException(
            "工程还有阻断性错误，无法导出：" + Environment.NewLine + string.Join(Environment.NewLine, reasons));
    }

    /// <summary>找出可用的音频源。</summary>
    private async Task<(string? Path, AudioStreamInfo? Info)> ResolveAudioAsync(
        Project project,
        VideoExportOptions options,
        ImmutableArray<string>.Builder missing,
        ImmutableArray<string>.Builder warnings,
        CancellationToken cancellationToken)
    {
        if (!options.IncludeAudio)
        {
            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(project.AudioPath))
        {
            warnings.Add("工程没有音频：本次导出只有画面，成片没有音轨。");

            return (null, null);
        }

        if (!File.Exists(project.AudioPath))
        {
            missing.Add(project.AudioPath);
            warnings.Add($"工程记录的音频找不到，本次导出只有画面：{project.AudioPath}");

            return (null, null);
        }

        AudioStreamInfo? info = await _probe
            .ProbeAsync(project.AudioPath, cancellationToken)
            .ConfigureAwait(false);

        if (info is null)
        {
            missing.Add(project.AudioPath);
            warnings.Add($"音频文件里没有音频流，本次导出只有画面：{project.AudioPath}");

            return (null, null);
        }

        return (project.AudioPath, info);
    }

    /// <summary>确定时长与帧率。</summary>
    /// <remarks>
    /// 时长以 <c>Composition.OutputDurationUs</c> 为准——它在导出上下文里是必填项（校验器已经把
    /// "仍为 null" 报成阻断性错误），所以这里不会再退到"猜一个"。
    /// 音频时长只用来在报告里说明差值。
    /// </remarks>
    private static (long DurationUs, FrameRate FrameRate) ResolveTiming(
        Composition composition,
        AudioStreamInfo? audioInfo)
    {
        long durationUs = composition.OutputDurationUs
            ?? audioInfo?.DurationUs
            ?? throw new InvalidOperationException(
                "输出时长尚未确定：请在合成设置里指定输出时长，或先导入音频。");

        if (durationUs <= 0)
        {
            throw new InvalidOperationException("输出时长必须为正。");
        }

        return (durationUs, composition.FrameRate);
    }

    /// <summary>由时长与帧率算总帧数。</summary>
    private static int ResolveTotalFrames(long durationUs, FrameRate frameRate)
        => CompositionFramePlan.Create(durationUs, frameRate).TotalFrames;

    /// <summary>第 n 帧对应的时刻，用整数运算避免累积误差。</summary>
    private static long FrameTimeUs(int index, FrameRate frameRate) =>
        (long)index * 1_000_000L * frameRate.Denominator / frameRate.Numerator;

    /// <summary>渲染失败时顶上的背景帧。</summary>
    /// <remarks>不是快路径——它只在渲染出错时跑一次，所以宁可写清楚的逐像素循环。</remarks>
    private static RenderFrame CreateFallbackFrame(RenderTarget target, RgbaColor background, long timeUs)
    {
        byte[] buffer = new byte[target.PixelWidth * target.PixelHeight * 4];

        for (int offset = 0; offset < buffer.Length; offset += 4)
        {
            buffer[offset] = background.Blue;
            buffer[offset + 1] = background.Green;
            buffer[offset + 2] = background.Red;
            buffer[offset + 3] = background.Alpha;
        }

        return new RenderFrame(buffer, target, PixelFormat.Bgra8888Premultiplied, timeUs);
    }

    /// <summary>探测产物并汇总报告。</summary>
    private async Task<ExportReport> BuildReportAsync(
        VideoExportOptions options,
        VideoEncodingRequest request,
        VideoEncodingResult encoding,
        Composition composition,
        ProjectSnapshot snapshot,
        string? audioPath,
        AudioStreamInfo? audioInfo,
        AudioEncodingPlan? audioPlan,
        FrameRate frameRate,
        int totalFrames,
        long durationUs,
        int failedFrames,
        ImmutableArray<string>.Builder warnings,
        ImmutableArray<string>.Builder missing,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        MediaFileInfo? produced = null;

        try
        {
            produced = await _probe.ProbeFileAsync(encoding.Artifact.TemporaryPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            // 探测告警不改变原严重性；暂存文件仍可发布，报告解释未验证的原因。
            string message = exception.Message.Replace(encoding.Artifact.TemporaryPath, options.OutputPath, StringComparison.Ordinal);
            warnings.Add($"产物校验失败（文件可能仍可播放）：{message}");
        }

        if (produced is null)
        {
            warnings.Add("产物里没有解析出任何媒体流。");
        }

        if (options.Format.SupportsAlpha())
        {
            // docs §12.3：透明输出必须验证**实际** Alpha，不能只看扩展名
            // （本机 libvpx-vp9 就是"扩展名看着对、Alpha 悄悄丢了"的现成例子，见 ADR-0037）。
            warnings.Add(produced?.Video switch
            {
                { HasAlpha: true } video => $"透明输出已确认：产物像素格式 {video.PixelFormat}，带 Alpha 通道。",
                { } video => $"透明输出没有生效：产物像素格式是 {video.PixelFormat ?? "未知"}，不含 Alpha 通道。",
                null when produced is not null => "透明输出无法确认：产物里没有可识别的视频流。",
                _ => "透明输出无法确认：产物探测失败。",
            });
        }

        long videoDurationUs = totalFrames * 1_000_000L * frameRate.Denominator / frameRate.Numerator;
        long? audioDurationUs = audioInfo?.DurationUs;
        long? deltaUs = audioDurationUs is { } audio ? videoDurationUs - audio : null;

        if (deltaUs is { } delta && Math.Abs(delta) > SignificantDurationDeltaUs)
        {
            warnings.Add(options.NormalizeAudioDuration
                ? delta > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"音轨比画面短 {delta / 1000.0:F0} ms，尾部已补静音。")
                    : string.Create(CultureInfo.InvariantCulture, $"音轨比画面长 {-delta / 1000.0:F0} ms，尾部已截断。")
                : options.AudioEncodingPlanOverride is null
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"音视频时长相差 {delta / 1000.0:F0} ms，成片会以较短的一方为准（-shortest）。")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"音视频时长相差 {delta / 1000.0:F0} ms，音轨按计划视频时长裁切。"));
        }

        if (encoding.DroppedFrames > 0)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"编码器报告丢了 {encoding.DroppedFrames} 帧。"));
        }

        if (failedFrames > MaxFrameWarnings)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"另有 {failedFrames - MaxFrameWarnings} 帧渲染失败（仅列出前 {MaxFrameWarnings} 条）。"));
        }

        (int total, int unconfirmed) = CountTokens(snapshot.Lyrics);

        if (unconfirmed > 0)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{unconfirmed} / {total} 个逐字元素的时间仍是估算或未确认，成片里的高亮位置可能不准。"));
        }

        return new ExportReport
        {
            OutputPath = options.OutputPath,
            ContainerName = produced?.ContainerName,
            VideoCodecName = produced?.Video?.CodecName,
            AudioCodecName = produced?.Audio?.CodecName,
            Width = composition.CanvasWidth,
            Height = composition.CanvasHeight,
            FrameRate = frameRate.Value,
            VideoDurationUs = videoDurationUs,
            AudioDurationUs = audioDurationUs,
            OutputDurationUs = produced?.DurationUs,
            AudioVideoDeltaUs = deltaUs,
            FramesWritten = encoding.FramesWritten,
            DroppedFrames = encoding.DroppedFrames,
            FailedFrames = failedFrames,
            UnconfirmedTokenCount = unconfirmed,
            TokenCount = total,
            Quality = options.Quality,
            EncoderSettings = request.Format.SupportsAlpha()
                ? "prores_ks / profile 4444 / yuva444p10le / vendor apl0"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"libx264 CRF {request.Quality.Crf} / preset {request.Quality.EncoderPreset}"),
            AudioCopied = !request.Format.SupportsAlpha() && (audioPlan?.Copy ?? false),
            AudioSourcePath = audioPath,
            MissingResources = [.. missing],
            Warnings = [.. warnings],
            Elapsed = stopwatch.Elapsed,
            OutputByteSize = encoding.OutputByteSize,
            IsVerified = produced is not null
                && produced.HasVideo
                && Math.Abs(produced.DurationUs - durationUs) <= VerificationToleranceUs,
        };
    }

    /// <summary>统计元素总数与其中"仍未确认"的数量。</summary>
    private static (int Total, int Unconfirmed) CountTokens(LyricsDocument lyrics)
    {
        int total = 0;
        int unconfirmed = 0;

        foreach (LyricLine line in lyrics.AllLines)
        {
            foreach (LyricToken token in line.Tokens)
            {
                total++;

                if (token.TimingStatus.IsUnconfirmed())
                {
                    unconfirmed++;
                }
            }
        }

        return (total, unconfirmed);
    }

    /// <summary>逐帧失败最多列出多少条。</summary>
    private const int MaxFrameWarnings = 5;

    /// <summary>音视频时长差超过这个值就值得在报告里提一句（200 ms）。</summary>
    private const long SignificantDurationDeltaUs = 200_000L;

    /// <summary>产物时长与预期相差多少以内算校验通过（约两帧）。</summary>
    private const long VerificationToleranceUs = 200_000L;
}
