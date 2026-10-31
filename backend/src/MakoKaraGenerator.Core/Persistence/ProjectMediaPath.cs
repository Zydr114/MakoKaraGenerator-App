namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 工程里保存的媒体路径与磁盘上的实际路径之间的换算。
/// </summary>
/// <remarks>
/// <para>
/// docs/development.md §6.1 要求项目包内的路径用相对路径。这里的策略是保守的：
/// <b>只有媒体文件确实位于工程目录之内时才改存相对路径</b>——那样整个工程目录可以整体搬走；
/// 位于别处（例如 <c>~/Music/xxx.mp3</c>）的一律存绝对路径，
/// 因为跨目录树的相对路径（一串 <c>..</c>）既不美观，也一样搬不走。
/// </para>
/// <para>
/// 内存里的 <see cref="Projects.Project.AudioPath" /> 始终是绝对路径，
/// 相对化只发生在写盘那一刻，由 <see cref="ProjectStore" /> 调用本类完成。
/// </para>
/// </remarks>
public static class ProjectMediaPath
{
    /// <summary>把内存中的路径换算成写盘时用的形式。</summary>
    /// <param name="path">内存中的路径（通常为绝对路径）。</param>
    /// <param name="directoryPath">工程目录。</param>
    /// <returns>写盘用的路径；<paramref name="path"/> 为空时原样返回。</returns>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> 为空白时抛出。</exception>
    public static string? ToStored(string? path, string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            // 已经是相对路径（或空）就不要再动它：用户手写的相对路径应当被尊重。
            return path;
        }

        string relative = Path.GetRelativePath(Path.GetFullPath(directoryPath), Path.GetFullPath(path));

        return IsInside(relative) ? relative : path;
    }

    /// <summary>把写盘的路径换算成打开时用的绝对路径。</summary>
    /// <param name="path">文件里读到的路径。</param>
    /// <param name="directoryPath">工程目录。</param>
    /// <returns>绝对路径；<paramref name="path"/> 为空时原样返回。</returns>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> 为空白时抛出。</exception>
    public static string? ToAbsolute(string? path, string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(directoryPath, path));
    }

    /// <summary>相对路径是否指向工程目录之内。</summary>
    private static bool IsInside(string relative) =>
        !relative.StartsWith("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(relative);
}
