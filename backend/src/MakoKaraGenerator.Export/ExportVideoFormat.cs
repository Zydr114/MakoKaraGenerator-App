namespace MakoKaraGenerator.Export;

/// <summary>导出成什么格式（决定有没有 Alpha 通道）。</summary>
/// <remarks>
/// <para>
/// docs/development.md §12.3 把输出分成"不透明成片"与"透明歌词视频"两类；
/// 它们的区别不只是编码器，还包括<b>画布是否从全透明开始</b>——
/// 透明输出这一档会跳过背景色与背景图（见 <c>RenderRequest.TransparentBackground</c>）。
/// </para>
/// <para>
/// 首期只提供这两档。透明档选择 ProRes 4444（ADR-0031 的决定），
/// 实测见 docs/decisions.md ADR-0037 的探测结果：本机 <c>libvpx-vp9</c> 无论如何参数
/// 都会静默丢掉 Alpha，所以 webm 这一档干脆不上架——宁可少一个选项，
/// 也不要给出一个"看起来透明、其实不透明"的产物。
/// </para>
/// </remarks>
public enum ExportVideoFormat
{
    /// <summary>不透明成片：MP4 容器 + H.264，兼容性最好。</summary>
    OpaqueMp4 = 0,

    /// <summary>透明歌词视频：MOV 容器 + ProRes 4444，画布与背景层全透明。</summary>
    TransparentProRes4444 = 1,
}

/// <summary><see cref="ExportVideoFormat"/> 的取值辅助。</summary>
public static class ExportVideoFormatExtensions
{
    /// <summary>该格式是否需要 Alpha 通道（即是否为透明输出）。</summary>
    /// <param name="format">格式。</param>
    /// <returns>需要 Alpha 时为 <see langword="true"/>。</returns>
    public static bool SupportsAlpha(this ExportVideoFormat format) =>
        format == ExportVideoFormat.TransparentProRes4444;

    /// <summary>该格式的默认文件扩展名（含点）。</summary>
    /// <param name="format">格式。</param>
    /// <returns>默认扩展名。</returns>
    public static string DefaultExtension(this ExportVideoFormat format) =>
        format == ExportVideoFormat.TransparentProRes4444 ? ".mov" : ".mp4";
}
