namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 工程里媒体引用的编辑操作。
/// </summary>
/// <remarks>
/// 与 <see cref="LyricStyleEditor" /> 同样的规矩：<b>没有实际变化就返回原实例</b>。
/// 界面的"有没有未保存改动"按引用比较，如果这里凭空造一个新实例，
/// "打开工程后按记录恢复音频"就会把刚打开的工程立刻标成已修改。
/// </remarks>
public static class ProjectMediaEditor
{
    /// <summary>设置或清除工程使用的音频路径。</summary>
    /// <param name="project">工程。</param>
    /// <param name="audioPath">音频文件路径；<see langword="null"/> 表示清除。</param>
    /// <returns>修改后的工程；没有变化时是原实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project SetAudioPath(Project project, string? audioPath)
    {
        ArgumentNullException.ThrowIfNull(project);

        return string.Equals(project.AudioPath, audioPath, StringComparison.Ordinal)
            ? project
            : project with { AudioPath = audioPath, AudioMedia = null, AudioAnalysis = null };
    }
}
