namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 进入动画的编辑与解析（roadmap `STYLE-04`）。
/// </summary>
/// <remarks>
/// 与其它编辑函数同规矩：<b>没有实际变化必须返回原实例</b>（ADR-0025）。
/// 继承规则只有一处实现（<see cref="Resolve(Project, Lyrics.LyricLine)"/>）：
/// 行级 <see langword="null"/> 表示"跟随工程默认"，因此把工程默认改成关闭，所有继承的行一起关掉。
/// </remarks>
public static class LineAnimationEditor
{
    /// <summary>设置工程级默认进入动画；<see langword="null"/> 表示关闭。</summary>
    /// <param name="project">工程。</param>
    /// <param name="animation">动画；<see langword="null"/> 为关闭。</param>
    /// <returns>新的工程实例，或原实例（未变化）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project SetProjectAnimation(Project project, LineAnimation? animation)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.EnterAnimation == animation
            ? project
            : project with { EnterAnimation = animation };
    }

    /// <summary>设置某一行的进入动画。</summary>
    /// <param name="project">工程。</param>
    /// <param name="lineIndex">行下标。</param>
    /// <param name="animation">动画；<see langword="null"/> 表示跟随工程默认。</param>
    /// <returns>修改后的工程；行下标越界时原样返回。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project SetLineAnimation(Project project, int lineIndex, LineAnimation? animation)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (lineIndex < 0 || lineIndex >= project.Lyrics.Lines.Length)
        {
            return project;
        }

        return UpdateLine(project, lineIndex, animation);
    }

    /// <summary>让某一行重新跟随工程默认。</summary>
    /// <param name="project">工程。</param>
    /// <param name="lineIndex">行下标。</param>
    /// <returns>修改后的工程；行下标越界或本来就在跟随默认时返回原实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project ClearLineAnimation(Project project, int lineIndex) =>
        SetLineAnimation(project, lineIndex, animation: null);

    /// <summary>解析出一行<b>实际生效</b>的进入动画。</summary>
    /// <param name="project">工程。</param>
    /// <param name="line">歌词行。</param>
    /// <returns>生效的动画；没有动画时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException">参数为 <see langword="null"/> 时抛出。</exception>
    public static LineAnimation? Resolve(Project project, Lyrics.LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(project);

        return Resolve(project.EnterAnimation, project.LyricPresentation, line);
    }

    /// <summary>用一份"工程级默认"解析某一行实际生效的动画。</summary>
    /// <param name="projectDefault">工程级默认；渲染侧从快照里取的就是它。</param>
    /// <param name="presentation">展示定义。</param>
    /// <param name="line">歌词行。</param>
    /// <returns>生效的动画；没有动画时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 渲染器只有快照、没有工程对象，因此继承规则必须能脱离 <see cref="Project"/> 表达；
    /// 但它仍然只有这一处实现——上面那个重载只是把工程的默认值递过来。
    /// </remarks>
    public static LineAnimation? Resolve(LineAnimation? projectDefault, LyricPresentationDefinition presentation, Lyrics.LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return presentation.FindLine(line.Id)?.EnterAnimation ?? projectDefault;
    }

    private static Project UpdateLine(Project project, int lineIndex, LineAnimation? animation)
    {
        Lyrics.LyricLine line = project.Lyrics.Lines[lineIndex];

        var branch = project.LyricPresentation.Resolve(project.Lyrics.PrimaryBranchId);
        var current = branch.FindLine(line.Id) ?? new() { LineId = line.Id };
        return PresentationEditor.SetBranch(project, PresentationEditor.SetLine(branch, line.Id,
            current with { EnterAnimation = animation }));
    }
}
