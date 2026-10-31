using System.Globalization;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 自动保存备份的位置策略与读写（roadmap `EDITOR-10`，docs §6.1 的 <c>backups/</c>）。
/// </summary>
/// <remarks>
/// <para>
/// docs §6.1 把 <c>backups/</c> 定义为项目包里的一部分，并明确"缓存和备份不参与渲染语义，
/// 删除后可以重新生成"。因此：
/// </para>
/// <list type="bullet">
///   <item>已落盘的工程 → 备份写在 <c>&lt;工程目录&gt;/backups/</c>，跟着工程走；</item>
///   <item>还没另存为的新工程 → 没有工程目录可写，退到应用状态目录下的 <c>autosave/</c>。
///     这一档不能省：否则"还没保存过"恰恰等于完全没有保护。</item>
/// </list>
/// <para>
/// 备份目录名里带 UTC 时间戳，于是<b>按名字排序就是按时间排序</b>——
/// 不依赖文件系统 mtime（那玩意会被备份工具、同步盘、解压操作改掉）。
/// </para>
/// </remarks>
public static class AutoSaveStore
{
    private static readonly object PublicationGate = new();
    /// <summary>工程目录里存放备份的子目录名。</summary>
    public const string BackupRootDirectoryName = "backups";

    /// <summary>单份备份目录的前缀。</summary>
    public const string BackupDirectoryPrefix = "autosave-";

    /// <summary>默认保留的备份份数。</summary>
    public const int DefaultRetainCount = 10;

    private const string TimestampFormat = "yyyyMMdd'-'HHmmss";

    /// <summary>算出备份应该写在哪个根目录下。</summary>
    /// <param name="projectDirectoryPath">工程目录；工程还没落盘时为 <see langword="null"/> 或空白。</param>
    /// <param name="stateRoot">应用状态目录（新工程时的兜底位置）。</param>
    /// <returns>备份根目录的完整路径。</returns>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> 为空白时抛出。</exception>
    public static string ResolveRoot(string? projectDirectoryPath, string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        return string.IsNullOrWhiteSpace(projectDirectoryPath)
            ? Path.Combine(stateRoot, "autosave")
            : Path.Combine(projectDirectoryPath, BackupRootDirectoryName);
    }

    /// <summary>单份备份的目录名。</summary>
    /// <param name="timestamp">时间戳。</param>
    /// <returns>形如 <c>autosave-20260925-184012Z</c> 的目录名。</returns>
    public static string BackupDirectoryName(DateTimeOffset timestamp) => string.Create(
        CultureInfo.InvariantCulture,
        $"{BackupDirectoryPrefix}{timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}Z");

    /// <summary>写一份备份，并按保留份数剪掉最旧的。</summary>
    /// <param name="project">要备份的工程数据。</param>
    /// <param name="root">备份根目录（来自 <see cref="ResolveRoot"/>）。</param>
    /// <param name="timestamp">时间戳。</param>
    /// <param name="retainCount">保留份数。</param>
    /// <returns>这一份备份的目录路径。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentException"><paramref name="root"/> 为空白时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retainCount"/> 非正时抛出。</exception>
    /// <remarks>
    /// 写盘复用 <see cref="ProjectStore.Save" />：完整闭包先写入唯一暂存目录，
    /// 成功后整体发布为独立备份。同秒名称冲突追加递增序号，不覆写已有备份；
    /// 发布后才裁剪旧备份，且始终保留本次发布。
    /// </remarks>
    public static string Save(Project project, string root, DateTimeOffset timestamp, int retainCount = DefaultRetainCount)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainCount);

        Directory.CreateDirectory(root);
        string temporary = Path.Combine(root, ".autosave-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            ProjectStore.Save(project, temporary);
            lock (PublicationGate)
            {
                string published = Publish(temporary, root, BackupDirectoryName(timestamp));
                PruneCore(root, retainCount, published);
                return published;
            }
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static string Publish(string temporary, string root, string name)
    {
        while (true)
        {
            // 取仍存在的最大序号，不复用已裁剪的空位，否则新备份会排到旧备份之前。
            long highest = -1;
            foreach (string entry in Directory.EnumerateFileSystemEntries(root, name + "*"))
            {
                string existing = Path.GetFileName(entry);
                if (existing == name) highest = Math.Max(highest, 0);
                else if (existing.StartsWith(name + "-", StringComparison.Ordinal)
                    && long.TryParse(existing.AsSpan(name.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long sequence))
                    highest = Math.Max(highest, sequence);
            }
            string candidate = Path.Combine(root, highest < 0 ? name
                : name + "-" + checked(highest + 1).ToString("D20", CultureInfo.InvariantCulture));
            try { Directory.Move(temporary, candidate); return candidate; }
            catch (IOException) when (Path.Exists(candidate)) { /* 其他进程先发布了同名备份，重新选取序号。 */ }
        }
    }

    /// <summary>列出备份目录，从新到旧。</summary>
    /// <param name="root">备份根目录。</param>
    /// <returns>备份目录的完整路径列表；目录不存在时为空。</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> 为空白时抛出。</exception>
    public static IReadOnlyList<string> List(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return [];
        }

        return
        [
            .. Directory
                .EnumerateDirectories(root)
                .Where(static path => Path.GetFileName(path).StartsWith(BackupDirectoryPrefix, StringComparison.Ordinal)
                    && File.Exists(Path.Combine(path, ProjectJson.ProjectFileName)))
                .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal),
        ];
    }

    /// <summary>删掉超出保留份数的旧备份。</summary>
    /// <param name="root">备份根目录。</param>
    /// <param name="retainCount">保留份数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retainCount"/> 非正时抛出。</exception>
    public static void Prune(string root, int retainCount = DefaultRetainCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainCount);

        lock (PublicationGate) PruneCore(root, retainCount, null);
    }

    private static void PruneCore(string root, int retainCount, string? published)
    {
        foreach (string stale in List(root).Where(path => path != published).Skip(retainCount - (published is null ? 0 : 1)))
            Directory.Delete(stale, recursive: true);
    }

    /// <summary>这份备份是否比工程目录里那份更新。</summary>
    /// <param name="backupDirectoryPath">备份目录。</param>
    /// <param name="projectDirectoryPath">工程目录。</param>
    /// <returns>备份更新（或工程文件不存在）时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 用<b>实际文件的修改时间</b>比较：这是"盘上那份是什么时候写的"最直接的证据。
    /// 工程文件不存在时也算"备份更新"——那说明工程从来没落盘过，而备份里有内容。
    /// </remarks>
    public static bool IsNewerThanProject(string backupDirectoryPath, string projectDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectoryPath);

        string backupFile = Path.Combine(backupDirectoryPath, ProjectJson.ProjectFileName);
        string projectFile = Path.Combine(projectDirectoryPath, ProjectJson.ProjectFileName);

        if (!File.Exists(projectFile))
        {
            return true;
        }

        return File.Exists(backupFile)
            && File.GetLastWriteTimeUtc(backupFile) > File.GetLastWriteTimeUtc(projectFile);
    }
}
