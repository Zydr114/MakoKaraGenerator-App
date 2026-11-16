using System.Globalization;

namespace MakoKaraGenerator.Export;

/// <summary>
/// 把强类型请求翻译成 FFmpeg 参数（docs/roadmap.md §10 的 <c>EXPORT-02</c>、<c>EXPORT-03</c>、
/// <c>EXPORT-05</c>、<c>EXPORT-06</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 单独抽成一层是为了<b>可测</b>：参数拼错是这类代码最常见的故障，而它的期望值是一串确定的字符串，
/// 不需要真的跑一遍编码器就能断言（见 <c>FfmpegCommandLineTests</c>）。
/// </para>
/// <para>
/// 输入路径固定为 <c>pipe:0</c> 的裸 BGRA 帧。渲染管线产出的是<b>预乘</b> BGRA（ADR-0021），
/// 而 ffmpeg 的 <c>bgra</c> 指的是<b>直乘</b>（straight）Alpha，两者在全不透明时数值相同、
/// 一旦有半透明就不同。因此透明输出在写帧前必须反预乘（<c>AlphaFrameConverter</c>），
/// 否则半透明歌词会被二次压暗。不透明成片里每像素 Alpha 都是 255，这一步是恒等变换。
/// </para>
/// </remarks>
internal static class FfmpegCommandLine
{
    /// <summary>裸像素输入的像素格式名。</summary>
    internal const string RawPixelFormat = "bgra";

    /// <summary>重编码音频时的目标码率。</summary>
    internal const string AudioBitrate = "192k";

    /// <summary>拼出一次视频编码的完整参数表。</summary>
    /// <param name="request">编码请求。</param>
    /// <returns>参数表（不含可执行文件名）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 <see langword="null"/> 时抛出。</exception>
    internal static IReadOnlyList<string> BuildVideoEncoding(VideoEncodingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool withAudio = !string.IsNullOrWhiteSpace(request.AudioPath);
        ExportQualityPreset preset = request.Quality;

        List<string> arguments =
        [
            "-hide_banner",

            // 我们只用 stdin 喂帧：不让 ffmpeg 去抢终端的标准输入。
            "-nostdin",
            "-loglevel",
            "warning",

            // EXPORT-03：固定帧率的裸帧输入。
            "-f",
            "rawvideo",
            "-pix_fmt",
            RawPixelFormat,
            "-s",
            string.Create(CultureInfo.InvariantCulture, $"{request.Width}x{request.Height}"),
            "-r",
            request.FrameRate.ToString(),
            "-i",
            "pipe:0",
        ];

        if (withAudio && request.AudioInputFormat is "mp3" or "flac") arguments.Insert(0, "-xerror");

        if (withAudio)
        {
            if (request.AudioInputFormat is { } inputFormat)
            {
                if (inputFormat is "mp3" or "flac") arguments.AddRange(["-err_detect", "explode"]);
                arguments.Add("-protocol_whitelist"); arguments.Add("file,pipe");
                arguments.Add("-f"); arguments.Add(inputFormat);
            }
            arguments.Add("-i");
            arguments.Add(request.AudioPath!);
        }

        arguments.Add("-map");
        arguments.Add("0:v:0");

        if (withAudio)
        {
            arguments.Add("-map");
            arguments.Add("1:a:0");
        }

        if (request.Format.SupportsAlpha())
        {
            // EXPORT-07：ProRes 4444 是首期唯一的透明档（ADR-0031）。
            // 显式指定 yuva444p10le：不指定的话 ffmpeg 会按"不透明"处理，
            // 而我们宁可在像素格式不受支持时**报错失败**，也不要静默出一份不透明的成片。
            arguments.Add("-c:v");
            arguments.Add("prores_ks");
            arguments.Add("-profile:v");
            arguments.Add("4444");
            arguments.Add("-pix_fmt");
            arguments.Add("yuva444p10le");
            arguments.Add("-vendor");
            arguments.Add("apl0");
        }
        else
        {
            // EXPORT-06：不透明 MP4。yuv420p 是兼容性最好的像素格式，
            // 代价是宽高必须是偶数（调用方在开始前就会拒绝奇数尺寸）。
            arguments.Add("-c:v");
            arguments.Add("libx264");
            arguments.Add("-pix_fmt");
            arguments.Add("yuv420p");
            arguments.Add("-crf");
            arguments.Add(preset.Crf.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-preset");
            arguments.Add(preset.EncoderPreset);
        }

        if (withAudio)
        {
            AudioEncodingPlan plan = request.AudioPlan ?? AudioEncodingPlan.Decide(null);

            if (request.Format.SupportsAlpha())
            {
                // MOV 里放无损 PCM：透明档的产物通常要进剪辑软件再加工，
                // 那里对 PCM 的支持比 AAC 更稳，而且 ProRes 4444 的体积本来就不在意这点音频。
                arguments.Add("-c:a");
                arguments.Add("pcm_s16le");
            }
            else
            {
                arguments.Add("-c:a");

                if (plan.Copy)
                {
                    arguments.Add("copy");
                }
                else
                {
                    arguments.Add(plan.CodecName);
                    arguments.Add("-b:a");
                    arguments.Add(AudioBitrate);
                }
            }

            // Desktop keeps its original shortest-input policy. Managed
            // tasks already verify source audio covers the complete frame plan;
            // -shortest can otherwise drop the last NTSC frame when PCM sample
            // rounding makes the trimmed audio a few microseconds shorter.
            if (request.AudioInputFormat is null) arguments.Add("-shortest");

            // PCM交错队列可能先读到视频EOF之后的音频；单靠-shortest会留下半秒尾音。
            // 按实际请求帧数给输出设置上限，分数帧率也不通过double换算。
            decimal durationSeconds = (decimal)request.TotalFrames * request.FrameRate.Denominator
                / request.FrameRate.Numerator;
            arguments.Add("-t");
            arguments.Add(durationSeconds.ToString("0.#########", CultureInfo.InvariantCulture));
        }

        // 边写边能播放（对"导出完就能直接看"很重要）。
        arguments.Add("-movflags");
        arguments.Add("+faststart");

        // 机器可读的进度写到 stdout：界面用它报丢帧，日志不掺噪声。
        arguments.Add("-progress");
        arguments.Add("pipe:1");
        arguments.Add("-nostats");

        if (request.MaximumOutputBytes is { } maximum)
        {
            arguments.Add("-fs"); arguments.Add(checked(maximum + 64L * 1024L).ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add(request.Overwrite ? "-y" : "-n");
        arguments.Add(request.OutputPath);

        return arguments;
    }

    /// <summary>把参数表拼成一行可读文本，用于日志。</summary>
    /// <param name="executablePath">可执行文件路径。</param>
    /// <param name="arguments">参数表。</param>
    /// <returns>安全摘要：只含参数，不含任何环境变量或凭证。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    internal static string Describe(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        return string.Join(' ', [executablePath, .. arguments]);
    }
}
