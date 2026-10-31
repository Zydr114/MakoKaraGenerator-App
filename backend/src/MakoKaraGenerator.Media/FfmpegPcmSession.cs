using System.Diagnostics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Media;

/// <summary>
/// 一次 FFmpeg 解码会话：把音频解成单声道 f32le 并暴露 stdout 流。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="FfmpegPcmReader" /> 的区别是"生命周期"：那个是"跑到结束再返回"的批量解码，
/// 用 CliWrap 很合适；播放需要的是一个<b>一直开着、边读边播</b>的管道，
/// 因此这里直接管理 <see cref="Process" />。
/// </para>
/// <para>
/// 参数与波形解码保持一致（单声道、源采样率、f32le），使播放与波形看到的是同一种采样数据。
/// </para>
/// </remarks>
internal sealed class FfmpegPcmSession : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    private FfmpegPcmSession(Process process)
    {
        _process = process;
        Output = process.StandardOutput.BaseStream;
    }

    /// <summary>解码输出的字节流（f32le 单声道）。</summary>
    public Stream Output { get; }

    /// <summary>启动一次解码。</summary>
    /// <param name="executablePath">ffmpeg 可执行文件。</param>
    /// <param name="filePath">音频文件路径。</param>
    /// <param name="startUs">起始位置（微秒）；大于 0 时用输入侧定位。</param>
    /// <returns>解码会话。</returns>
    /// <exception cref="IOException">无法启动 FFmpeg 时抛出。</exception>
    public static FfmpegPcmSession Start(string executablePath, string filePath, long startUs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        ProcessStartInfo info = new(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        info.ArgumentList.Add("-v");
        info.ArgumentList.Add("error");

        if (startUs > 0L)
        {
            // 放在 -i 之前是"输入侧定位"：FFmpeg 会跳到该时刻再开始解码，
            // 对音频来说是精确的，且不用把前面的数据解出来再丢掉。
            info.ArgumentList.Add("-ss");
            info.ArgumentList.Add(
                (startUs / (double)TimeUs.PerSecond).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        }

        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(filePath);
        info.ArgumentList.Add("-vn");
        info.ArgumentList.Add("-ac");
        info.ArgumentList.Add("1");
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add("f32le");
        info.ArgumentList.Add("-acodec");
        info.ArgumentList.Add("pcm_f32le");
        info.ArgumentList.Add("-");

        Process process = new() { StartInfo = info };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new IOException($"无法启动 {executablePath}：{exception.Message}", exception);
        }

        // stderr 必须持续排空，否则 FFmpeg 写日志写满管道缓冲区后会卡住。
        _ = process.StandardError.ReadToEndAsync();

        return new FfmpegPcmSession(process);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // 进程已经退出：什么都不用做。
        }

        _process.Dispose();
    }
}
