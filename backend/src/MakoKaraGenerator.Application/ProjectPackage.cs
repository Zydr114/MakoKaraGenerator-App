using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Application;

/// <summary>
/// 项目包（一个目录）的命名与定位规则。
/// </summary>
/// <remarks>
/// <para>
/// 项目包结构见 docs/development.md §6.1：一个可读的目录，入口是 <c>project.json</c>，
/// 歌词由入口的 <c>lyricsFile</c> 指向不可变内容地址。这里只负责"叫什么、在哪"，
/// 读写本身属于 <see cref="ProjectStore" />。
/// </para>
/// <para>
/// 这些规则放在用例层而不是界面里，是因为它们会被"新建/打开/另存为"三处共用，
/// 散在界面里迟早出现"另存为允许的名字、新建却不允许"这类不一致。
/// </para>
/// </remarks>
public static class ProjectPackage
{
    /// <summary>项目包目录的推荐后缀。</summary>
    /// <remarks>
    /// 只是<b>推荐</b>：打开时不检查后缀，用户选中任何含 <c>project.json</c> 的目录都算项目。
    /// 后缀的作用是让文件管理器里一眼看出"这是一整个项目，不是零散文件"。
    /// </remarks>
    public const string DirectoryExtension = ".mkgproj";

    /// <summary>检查工程名是否可用。</summary>
    /// <param name="name">候选名字。</param>
    /// <returns>不可用时的原因；可用时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 名字会同时成为目录名与项目显示名，因此按文件名规则校验。
    /// <c>.</c> 与 <c>..</c> 被单独拒绝：它们能让"选择父目录 + 名字"拼到父目录之外。
    /// </remarks>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "工程名不能为空。";
        }

        string trimmed = name.Trim();

        if (trimmed is "." or "..")
        {
            return "工程名不能是 . 或 ..。";
        }

        if (trimmed.EndsWith(DirectoryExtension, StringComparison.OrdinalIgnoreCase))
        {
            // 不算错误也能用，但会让目录变成 song.mkgproj.mkgproj，属于明确的用户意图不明。
            return $"工程名不要再带 {DirectoryExtension} 后缀。";
        }

        // 两个分隔符都显式拒绝：Linux 的 GetInvalidFileNameChars 不含 '\'，
        // 只靠它会让 "a\b" 通过校验，而这个名字拿到 Windows 上就是路径穿越。
        if (trimmed.IndexOfAny(['/', '\\']) >= 0 || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "工程名不能包含路径分隔符或系统保留字符。";
        }

        // 目录名长度上限在常见文件系统上约 255 字节；中文按 3 字节算也够用。
        if (trimmed.Length > 80)
        {
            return "工程名太长了（最多 80 个字符）。";
        }

        return null;
    }

    /// <summary>把"父目录 + 工程名"解析成项目包目录的完整路径。</summary>
    /// <param name="parentDirectory">父目录。</param>
    /// <param name="name">工程名。</param>
    /// <returns>项目包目录路径。</returns>
    /// <exception cref="ArgumentException">父目录为空、或工程名不合规时抛出。</exception>
    public static string ResolveDirectory(string parentDirectory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);

        if (ValidateName(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        return Path.Combine(parentDirectory, name.Trim() + DirectoryExtension);
    }

    /// <summary>判断目录里是否已经有一个项目包。</summary>
    /// <param name="directoryPath">待检查的目录。</param>
    /// <returns>存在 <c>project.json</c> 时为 <see langword="true"/>。</returns>
    public static bool Exists(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        return File.Exists(Path.Combine(directoryPath, ProjectJson.ProjectFileName));
    }

    /// <summary>从项目包目录推出工程显示名（去掉推荐后缀）。</summary>
    /// <param name="directoryPath">项目包目录。</param>
    /// <returns>目录名去掉 <see cref="DirectoryExtension" /> 后缀后的结果。</returns>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> 为空白时抛出。</exception>
    public static string NameFromDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));

        return name.EndsWith(DirectoryExtension, StringComparison.OrdinalIgnoreCase)
            ? name[..^DirectoryExtension.Length]
            : name;
    }
}
