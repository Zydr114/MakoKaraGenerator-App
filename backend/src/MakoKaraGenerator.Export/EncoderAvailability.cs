using System.ComponentModel;
using System.Globalization;
using CliWrap;
using CliWrap.Buffered;

namespace MakoKaraGenerator.Export;

/// <summary>
/// 本机 FFmpeg 的可用性探测（ADR-0031：不随应用分发 FFmpeg，改为启动时探测 + 缺失时给可读提示）。
/// </summary>
/// <param name="IsAvailable">可执行文件能否启动。</param>
/// <param name="ExecutablePath">使用的可执行文件名或路径。</param>
/// <param name="Version">探测到的版本行首；探测不到时为 <see langword="null"/>。</param>
/// <param name="HasH264Encoder">编码器列表里是否有 <c>libx264</c>。</param>
/// <param name="Problem">不可用或不完整时的可读说明。</param>
public sealed record EncoderAvailability(
    bool IsAvailable,
    string ExecutablePath,
    string? Version,
    bool HasH264Encoder,
    string? Problem)
{
    /// <summary>能否开始导出。</summary>
    public bool CanExport => IsAvailable && HasH264Encoder;

    /// <summary>探测本机 FFmpeg。</summary>
    /// <param name="executablePath">可执行文件名或路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>探测结果；任何异常都折算成"不可用 + 说明"，不会抛出。</returns>
    /// <remarks>
    /// <para>
    /// 刻意不抛异常：调用方在启动时要的是"能不能用、不能用时该说什么"，
    /// 而不是一个需要自己包装的底层异常。
    /// </para>
    /// <para>
    /// 分两步探测：<c>-version</c> 确认能启动；<c>-encoders</c> 确认 H.264 编码器在
    /// ——装了 FFmpeg 但没编进 libx264 的发行版确实存在（某些精简构建）。
    /// </para>
    /// </remarks>
    public static async Task<EncoderAvailability> ProbeAsync(
        string executablePath = FfmpegMediaEncoder.DefaultExecutableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string version;

        try
        {
            BufferedCommandResult versionResult = await Cli.Wrap(executablePath)
                .WithArguments(["-hide_banner", "-version"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken)
                .ConfigureAwait(false);

            if (versionResult.ExitCode != 0)
            {
                return new EncoderAvailability(
                    IsAvailable: false,
                    ExecutablePath: executablePath,
                    Version: null,
                    HasH264Encoder: false,
                    Problem: string.Create(
                        CultureInfo.InvariantCulture,
                        $"执行 {executablePath} -version 失败（退出码 {versionResult.ExitCode}）：{versionResult.StandardError.Trim()}"));
            }

            version = FirstLine(versionResult.StandardOutput);
        }
        catch (Win32Exception exception)
        {
            return new EncoderAvailability(
                IsAvailable: false,
                ExecutablePath: executablePath,
                Version: null,
                HasH264Encoder: false,
                Problem: $"找不到可执行的 {executablePath}（{exception.Message}）。"
                    + "导出需要本机安装 FFmpeg，安装方式见 README 的说明。");
        }
        catch (IOException exception)
        {
            return new EncoderAvailability(
                IsAvailable: false,
                ExecutablePath: executablePath,
                Version: null,
                HasH264Encoder: false,
                Problem: $"探测 {executablePath} 时出错：{exception.Message}");
        }

        try
        {
            BufferedCommandResult encodersResult = await Cli.Wrap(executablePath)
                .WithArguments(["-hide_banner", "-encoders"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken)
                .ConfigureAwait(false);

            bool hasH264 = encodersResult.StandardOutput.Contains("libx264", StringComparison.Ordinal);

            return new EncoderAvailability(
                IsAvailable: true,
                ExecutablePath: executablePath,
                Version: version,
                HasH264Encoder: hasH264,
                Problem: hasH264
                    ? null
                    : "本机 FFmpeg 没有 libx264 编码器，无法导出 MP4；请改用带 libx264 的构建。");
        }
        catch (Win32Exception exception)
        {
            return new EncoderAvailability(
                IsAvailable: true,
                ExecutablePath: executablePath,
                Version: version,
                HasH264Encoder: false,
                Problem: $"读取编码器列表失败：{exception.Message}");
        }
        catch (IOException exception)
        {
            return new EncoderAvailability(
                IsAvailable: true,
                ExecutablePath: executablePath,
                Version: version,
                HasH264Encoder: false,
                Problem: $"读取编码器列表失败：{exception.Message}");
        }
    }

    /// <summary>给人看的一行说明。</summary>
    /// <returns>可用时是版本信息，不可用时是原因。</returns>
    public string Describe() => CanExport
        ? $"{ExecutablePath}：{Version ?? "已就绪"}"
        : Problem ?? $"找不到可用的 {ExecutablePath}。";

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);

        return (newline < 0 ? text : text[..newline]).Trim();
    }
}
