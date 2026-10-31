namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 背景源的编辑函数（roadmap `EXPORT-04`、`EXPORT-04` 的视频部分）。
/// </summary>
/// <remarks>
/// 与其它编辑函数同规矩：<b>没有实际变化必须返回原实例</b>，否则界面会多出
/// 一次没有内容的撤销步骤，自动保存也会误以为"有改动"（ADR-0025）。
/// </remarks>
public static class ProjectBackgroundEditor
{
    /// <summary>设置背景图片；设为同一张图时返回原实例。</summary>
    /// <param name="project">项目。</param>
    /// <param name="path">图片路径，不可为空白。</param>
    /// <returns>新的项目实例，或原实例（未变化）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空白时抛出。</exception>
    public static Project SetImage(Project project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (project.Background is { Kind: BackgroundKind.Image } current
            && string.Equals(current.Path, path, StringComparison.Ordinal))
        {
            return project;
        }

        // 换图时保留用户已经选过的适配方式与播放方式：换的只是画面，不是观看方式。
        // 播放方式对图片没有意义，但留着它，用户在图片与视频之间来回换时偏好不会被抹掉。
        return project with
        {
            Background = BackgroundSource.ForImage(path, project.Background.Fit)
                with { Playback = project.Background.Playback },
        };
    }

    /// <summary>设置背景视频；同一段视频再次设置时返回原实例。</summary>
    /// <param name="project">项目。</param>
    /// <param name="path">视频路径，不可为空白。</param>
    /// <returns>新的项目实例，或原实例（未变化）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空白时抛出。</exception>
    public static Project SetVideo(Project project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (project.Background is { Kind: BackgroundKind.Video } current
            && string.Equals(current.Path, path, StringComparison.Ordinal))
        {
            return project;
        }

        // 与换图同理：换的只是画面，适配方式与时间策略都是用户已经表达过的偏好，一并保留。
        return project with
        {
            Background = BackgroundSource.ForVideo(path, project.Background.Fit, project.Background.Playback),
        };
    }

    /// <summary>设置视频短于音频时的时间策略；值不变时返回原实例。</summary>
    /// <param name="project">项目。</param>
    /// <param name="playback">时间策略。</param>
    /// <returns>新的项目实例，或原实例（未变化）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project SetPlaybackMode(Project project, BackgroundPlaybackMode playback)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Background.Playback == playback
            ? project
            : project with { Background = project.Background with { Playback = playback } };
    }

    /// <summary>清除背景媒体，回到只有背景色。</summary>
    /// <param name="project">项目。</param>
    /// <returns>新的项目实例，或原实例（本来就没有媒体）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project Clear(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.Background.Kind == BackgroundKind.Color)
        {
            return project;
        }

        // 适配方式与时间策略一并回到默认：没有画面时它们没有意义，
        // 留着会让"再导入一个"的初始状态变得不可预期。
        return project with { Background = BackgroundSource.Color };
    }

    /// <summary>设置适配方式；值不变时返回原实例。</summary>
    /// <param name="project">项目。</param>
    /// <param name="fit">适配方式。</param>
    /// <returns>新的项目实例，或原实例（未变化）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project SetFit(Project project, BackgroundFit fit)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Background.Fit == fit
            ? project
            : project with { Background = project.Background with { Fit = fit } };
    }
}
