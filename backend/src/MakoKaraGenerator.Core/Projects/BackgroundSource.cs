namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 背景源：铺在背景色之上的图片或视频画面。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="Composition.BackgroundColor" /> 的分工：背景色是画布的底色（永远铺满），
/// 本类型是它<b>上面</b>的一层。图片带透明度时会与底色叠加——这正是把两者分开的理由，
/// 视频与底色使用同一套结构。
/// </para>
/// <para>
/// <see cref="Path" /> 在内存中是<b>绝对路径</b>（打开工程时由 <c>ProjectStore</c> 解析），
/// 保存时媒体完整发布到内容地址；加载严格校验字节后解析为受管绝对路径。
/// 新选择的外部源尚未发布时没有 <see cref="Media"/>。
/// </para>
/// </remarks>
public sealed record BackgroundSource
{
    /// <summary>没有额外背景画面，只有背景色。</summary>
    public static BackgroundSource Color { get; } = new();

    /// <summary>背景源的种类。</summary>
    public BackgroundKind Kind { get; init; } = BackgroundKind.Color;

    /// <summary>背景图片路径（内存中为绝对路径）；<see cref="Kind" /> 为纯色时为 <see langword="null"/>。</summary>
    public string? Path { get; init; }

    /// <summary>固定背景媒体身份；新选择的外部源尚未发布时为空。</summary>
    public MediaFileRevision? Media { get; init; }

    /// <summary>图片与画布比例不一致时的适配方式。</summary>
    public BackgroundFit Fit { get; init; } = BackgroundFit.Contain;

    /// <summary>视频短于音频时的时间策略；<see cref="Kind" /> 不是视频时该值不参与渲染。</summary>
    public BackgroundPlaybackMode Playback { get; init; } = BackgroundPlaybackMode.Loop;

    /// <summary>是否真的有一张图要画。</summary>
    public bool HasImage => Kind == BackgroundKind.Image && !string.IsNullOrWhiteSpace(Path);

    /// <summary>是否真的有一段视频要画。</summary>
    public bool HasVideo => Kind == BackgroundKind.Video && !string.IsNullOrWhiteSpace(Path);

    /// <summary>是否有图片或视频要画；只有背景色时为 <see langword="false"/>。</summary>
    public bool HasMedia => HasImage || HasVideo;

    /// <summary>创建一个图片背景源。</summary>
    /// <param name="path">图片路径，不可为空白。</param>
    /// <param name="fit">适配方式。</param>
    /// <returns>新的背景源实例。</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空白时抛出。</exception>
    public static BackgroundSource ForImage(string path, BackgroundFit fit = BackgroundFit.Contain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new BackgroundSource { Kind = BackgroundKind.Image, Path = path, Fit = fit };
    }

    /// <summary>创建一段视频背景。</summary>
    /// <param name="path">视频路径，不可为空白。</param>
    /// <param name="fit">适配方式。</param>
    /// <param name="playback">短于音频时的时间策略。</param>
    /// <returns>新的背景源实例。</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空白时抛出。</exception>
    public static BackgroundSource ForVideo(
        string path,
        BackgroundFit fit = BackgroundFit.Contain,
        BackgroundPlaybackMode playback = BackgroundPlaybackMode.Loop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new BackgroundSource
        {
            Kind = BackgroundKind.Video,
            Path = path,
            Fit = fit,
            Playback = playback,
        };
    }
}
