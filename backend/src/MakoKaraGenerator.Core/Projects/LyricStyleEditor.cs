using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 对工程样式的编辑操作。
/// </summary>
/// <remarks>
/// <para>
/// 放在 Core 而不是界面里，是因为这些规则<b>不涉及界面</b>但很容易写错，
/// 而且错了以后很难发现：
/// </para>
/// <list type="bullet">
///   <item>改某一项时只写这一项的覆盖——把当前生效的整份样式都写成覆盖，
///         等于让这一行"脱离继承"，以后改工程基线它就不跟着变了（STYLE-02 的底线）；</item>
///   <item>手工改动之后预设标识不再代表当前外观，必须清掉；</item>
///   <item>覆盖清空后要连同覆盖对象一起置为 <see langword="null"/>，
///         否则文件里会留一个空对象。</item>
/// </list>
/// <para>所有方法都是纯函数：返回新的 <see cref="Project"/>，不修改传入的实例。</para>
/// </remarks>
public static class LyricStyleEditor
{
    /// <summary>改写工程样式基线。</summary>
    /// <param name="project">工程。</param>
    /// <param name="style">新的基线样式。</param>
    /// <returns>修改后的工程。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 手工改动会让"当前预设"失去意义，因此预设标识被清空。
    /// 样式与预设标识都没有变化时<b>返回原实例</b>：界面的"有没有未保存改动"
    /// 是按实例比较的，凭空造一个新实例会让刚打开的项目立刻显示成已修改。
    /// </remarks>
    public static Project SetBaseline(Project project, LyricStyle style)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(style);

        return style == project.LyricStyle && project.LyricStylePresetId is null
            ? project
            : project with { LyricStyle = style, LyricStylePresetId = null };
    }

    /// <summary>把预设应用到工程（写入样式本身，而不是只记名字）。</summary>
    /// <param name="project">工程。</param>
    /// <param name="preset">预设。</param>
    /// <returns>修改后的工程。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>已经等于该预设且标识一致时返回原实例，理由同 <see cref="SetBaseline"/>。</remarks>
    public static Project ApplyPreset(Project project, LyricStylePreset preset)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(preset);

        return preset.Style == project.LyricStyle && preset.Id == project.LyricStylePresetId
            ? project
            : project with { LyricStyle = preset.Style, LyricStylePresetId = preset.Id };
    }

    /// <summary>改写某一行的样式覆盖。</summary>
    /// <param name="project">工程。</param>
    /// <param name="lineIndex">行下标。</param>
    /// <param name="update">接收当前覆盖（可能为 <see langword="null"/>），返回新的覆盖。</param>
    /// <returns>修改后的工程；行下标越界或没有歌词时原样返回。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    public static Project UpdateLineOverride(
        Project project,
        int lineIndex,
        Func<LyricStyleOverride?, LyricStyleOverride?> update)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(update);

        if (lineIndex < 0 || lineIndex >= project.Lyrics.Lines.Length)
        {
            return project;
        }

        LyricLine line = project.Lyrics.Lines[lineIndex];
        LyricStyleOverride? updated = update(line.StyleOverride);

        return SetLineOverride(project, lineIndex, updated);
    }

    /// <summary>设置或清除某一行的样式覆盖。</summary>
    /// <param name="project">工程。</param>
    /// <param name="lineIndex">行下标。</param>
    /// <param name="styleOverride">新的覆盖；为 <see langword="null"/> 或空覆盖表示恢复完全继承。</param>
    /// <returns>修改后的工程；行下标越界时原样返回。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project SetLineOverride(Project project, int lineIndex, LyricStyleOverride? styleOverride)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (lineIndex < 0 || lineIndex >= project.Lyrics.Lines.Length)
        {
            return project;
        }

        LyricLine line = project.Lyrics.Lines[lineIndex];

        // 空覆盖与"没有覆盖"在语义上相同：统一收敛成 null，文件里不会留下空对象。
        LyricStyleOverride? normalized = styleOverride is { IsEmpty: false } ? styleOverride : null;

        if (line.StyleOverride == normalized)
        {
            return project;
        }

        return project with
        {
            Lyrics = project.Lyrics with
            {
                Lines = project.Lyrics.Lines.SetItem(lineIndex, line with { StyleOverride = normalized }),
            },
        };
    }

    /// <summary>改写某个元素的样式覆盖。</summary>
    /// <param name="project">工程。</param>
    /// <param name="lineIndex">行下标。</param>
    /// <param name="tokenIndex">元素下标。</param>
    /// <param name="update">接收当前覆盖（可能为 <see langword="null"/>），返回新的覆盖。</param>
    /// <returns>修改后的工程；下标越界时原样返回。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    public static Project UpdateTokenOverride(
        Project project,
        int lineIndex,
        int tokenIndex,
        Func<LyricStyleOverride?, LyricStyleOverride?> update)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(update);

        if (!TryGetToken(project, lineIndex, tokenIndex, out LyricLine line))
        {
            return project;
        }

        return SetTokenOverride(project, lineIndex, tokenIndex, update(line.Tokens[tokenIndex].StyleOverride));
    }

    /// <summary>设置或清除某个元素的样式覆盖。</summary>
    /// <param name="project">工程。</param>
    /// <param name="lineIndex">行下标。</param>
    /// <param name="tokenIndex">元素下标。</param>
    /// <param name="styleOverride">新的覆盖；为 <see langword="null"/> 或空覆盖表示恢复完全继承。</param>
    /// <returns>修改后的工程；下标越界时原样返回。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 与行级同样的两条规矩：空覆盖收敛成 <see langword="null"/>（文件里不留空对象），
    /// 没有实际变化时返回原实例（ADR-0025）。
    /// </remarks>
    public static Project SetTokenOverride(
        Project project,
        int lineIndex,
        int tokenIndex,
        LyricStyleOverride? styleOverride)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!TryGetToken(project, lineIndex, tokenIndex, out LyricLine line))
        {
            return project;
        }

        LyricStyleOverride? normalized = styleOverride is { IsEmpty: false } ? styleOverride : null;
        LyricToken token = line.Tokens[tokenIndex];

        if (token.StyleOverride == normalized)
        {
            return project;
        }

        return project with
        {
            Lyrics = project.Lyrics with
            {
                Lines = project.Lyrics.Lines.SetItem(
                    lineIndex,
                    line with { Tokens = line.Tokens.SetItem(tokenIndex, token with { StyleOverride = normalized }) }),
            },
        };
    }

    /// <summary>清除某个元素的样式覆盖（恢复完全继承）。</summary>
    /// <param name="project">工程。</param>
    /// <param name="lineIndex">行下标。</param>
    /// <param name="tokenIndex">元素下标。</param>
    /// <returns>修改后的工程。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project ClearTokenOverride(Project project, int lineIndex, int tokenIndex) =>
        SetTokenOverride(project, lineIndex, tokenIndex, styleOverride: null);

    private static bool TryGetToken(Project project, int lineIndex, int tokenIndex, out LyricLine line)
    {
        line = null!;

        if (lineIndex < 0 || lineIndex >= project.Lyrics.Lines.Length)
        {
            return false;
        }

        LyricLine candidate = project.Lyrics.Lines[lineIndex];

        if (tokenIndex < 0 || tokenIndex >= candidate.Tokens.Length)
        {
            return false;
        }

        line = candidate;

        return true;
    }

    /// <summary>清除某一行的样式覆盖（恢复完全继承）。</summary>
    /// <param name="project">工程。</param>
    /// <param name="lineIndex">行下标。</param>
    /// <returns>修改后的工程。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project ClearLineOverride(Project project, int lineIndex) =>
        SetLineOverride(project, lineIndex, styleOverride: null);

    /// <summary>
    /// 只覆盖一个属性。
    /// </summary>
    /// <param name="project">工程。</param>
    /// <param name="lineIndex">行下标。</param>
    /// <param name="update">在现有覆盖上改写某个属性；返回 <see langword="null"/> 表示清除覆盖。</param>
    /// <returns>修改后的工程。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 这是"改一项就只写一项"的入口：界面上的字号滑块、字体下拉都走它，
    /// 其余属性保持未覆盖状态，从而继续继承基线。
    /// </remarks>
    public static Project UpdateLineOverrideProperty(
        Project project,
        int lineIndex,
        Func<LyricStyleOverride, LyricStyleOverride?> update)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(update);

        return UpdateLineOverride(
            project,
            lineIndex,
            current => update(current ?? new LyricStyleOverride()));
    }
}
