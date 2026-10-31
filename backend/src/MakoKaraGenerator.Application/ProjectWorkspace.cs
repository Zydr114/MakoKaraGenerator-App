using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application;

/// <summary>
/// 一个打开着的工程：当前数据、所在目录、以及"有没有未保存改动"。
/// </summary>
/// <remarks>
/// <para>
/// 界面只说"新建 / 打开 / 保存 / 另存为"，具体怎么做由这里决定（roadmap EDITOR-02 的验收：
/// UI 操作调用用例层）。这样"保存了什么、什么时候算脏"是可可测试的，
/// 不需要点开窗口才能验证。
/// </para>
/// <para>
/// <b>脏判断按引用比较</b>：Core 的 <see cref="Project" /> 是不可变记录，
/// 界面每次编辑都会换成新实例，因此"当前实例 != 最后落盘的实例"就是"有改动"。
/// 按值比较需要遍历全部歌词行，编辑时逐次做是浪费。
/// </para>
/// <para>
/// 本类<b>不做</b>"要不要保存"的判断，也不弹任何窗口：策略属于界面
/// （docs/development.md §6.3 把校验时机限定在打开、导入与导出；保存不做拦截）。
/// </para>
/// </remarks>
public sealed class ProjectWorkspace
{
    private Project? _saved;
    private bool _ownsWriteLease;
    private ProjectWriteLease? _writeLease;

    /// <summary>由桌面或后端生命周期所有者启用目录租约；旧非参与者不在此合同内。</summary>
    public void EnableWriteLeaseOwnership() => _ownsWriteLease = true;

    /// <summary>打开候选在发布前将同一媒体内容固定到宿主闭包；保留原保存基线及恢复脏状态。</summary>
    public bool PinPreparedMediaPaths(Project expectedProject, string? audioPath, string? backgroundPath)
    {
        ArgumentNullException.ThrowIfNull(expectedProject);
        if (!ReferenceEquals(Project, expectedProject) || History.UndoCount != 0 || History.RedoCount != 0) return false;
        bool clean = ReferenceEquals(_saved, expectedProject);
        Project = expectedProject with { AudioPath = audioPath, Background = expectedProject.Background with { Path = backgroundPath } };
        if (clean) _saved = Project;
        return true;
    }

    /// <summary>取得保存候选目录；完整 IO 完成之后用 AcceptSave 转交，失败则释放候选。</summary>
    public ProjectWriteLeaseReservation PrepareSaveLease(string directoryPath)
    {
        string path = ProjectWriteLease.ResolvePhysicalPath(directoryPath);
        bool current = _writeLease is { } held && string.Equals(held.DirectoryPath, path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return new(this, Generation, path, _ownsWriteLease && !current ? ProjectWriteLease.Acquire(path) : null,
            current ? _writeLease!.Retain() : null, current ? _writeLease : null);
    }

    /// <summary>仅在保存事务已排空、工作区关闭或成功换工程后释放。</summary>
    public void ReleaseWriteLease() { _writeLease?.Dispose(); _writeLease = null; }

    private void AdoptWriteLease(ProjectWriteLease? lease)
    {
        if (lease is null) return;
        ProjectWriteLease? previous = _writeLease;
        _writeLease = lease;
        previous?.Dispose();
    }

    private ProjectWorkspace(Project project, string? directoryPath, bool asSaved)
    {
        Project = project;
        DirectoryPath = directoryPath;
        _saved = asSaved ? project : null;
    }

    /// <summary>当前工程数据。</summary>
    public Project Project { get; private set; }

    /// <summary>新建、打开或恢复时递增；普通编辑和保存不改变工作区身份。</summary>
    public long Generation { get; private set; } = 1;

    /// <summary>在工作区所有者线程捕获保存所需的完整不可变输入。</summary>
    public ProjectSaveSnapshot CaptureSave(DateTimeOffset now) => new(Project, DirectoryPath, Generation, IsDirty, now);

    /// <summary>仅将实际写出的快照标为已保存；期间的新编辑继续保持 dirty。</summary>
    public bool AcceptSave(ProjectSaveSnapshot snapshot, string directoryPath, IReadOnlyList<ProjectIssue> issues,
        ProjectWriteLeaseReservation? reservation = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(issues);
        if (snapshot.Generation != Generation) return false;
        if (reservation is not null)
        {
            if (!ReferenceEquals(reservation.Workspace, this) || reservation.Generation != Generation
                || !string.Equals(reservation.DirectoryPath, ProjectWriteLease.ResolvePhysicalPath(directoryPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            AdoptWriteLease(reservation.Take());
        }
        DirectoryPath = Path.GetFullPath(directoryPath); _saved = snapshot.Project; LastSaveIssues = issues;
        return true;
    }

    /// <summary>上一次保存产生的诊断（资源收拢的提示、缺失资源的警告）。</summary>
    /// <remarks>
    /// 保存会顺带做事（把背景图片复制进工程包的 <c>assets/</c>、发现原图已被删除），
    /// 这些都必须能被界面说出来——"文件里悄悄多了一个路径"是不可接受的（docs §19）。
    /// </remarks>
    public IReadOnlyList<ProjectIssue> LastSaveIssues { get; private set; } = [];

    /// <summary>工程所在目录；从未落盘时为 <see langword="null"/>。</summary>
    public string? DirectoryPath { get; private set; }

    /// <summary>是否有未保存的改动。</summary>
    public bool IsDirty => !ReferenceEquals(Project, _saved);

    /// <summary>是否已经有落盘位置。</summary>
    public bool HasPath => DirectoryPath is not null;

    /// <summary>撤销/重做栈。</summary>
    /// <remarks>
    /// 历史属于"这次会话"，不属于工程数据：打开或新建工程都会清空它。
    /// 保存<b>不</b>清空——保存后接着撤销回去，工程就重新变成"有未保存改动"，
    /// 这是正确语义，而不是需要特殊处理的例外。
    /// </remarks>
    public ProjectHistory History { get; } = new();

    /// <summary>是否可以撤销。</summary>
    public bool CanUndo => History.CanUndo;

    /// <summary>是否可以重做。</summary>
    public bool CanRedo => History.CanRedo;

    /// <summary>下一步撤销的操作标识。</summary>
    public string? UndoName => History.UndoName;

    /// <summary>下一步重做的操作标识。</summary>
    public string? RedoName => History.RedoName;

    /// <summary>创造一个全新工程（尚未落盘，且不算"有改动"）。</summary>
    /// <param name="name">工程名。</param>
    /// <param name="composition">合成设置；留空时使用 1920×1080 / 30fps 默认值。</param>
    /// <returns>工程会话。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空白时抛出。</exception>
    public static ProjectWorkspace CreateNew(string name, Composition? composition = null)
    {
        ProjectWorkspace workspace = new(CreateDefaultProject(name, composition), directoryPath: null, asSaved: true);

        workspace.History.Clear();
        return workspace;
    }

    /// <summary>接管一个已经存在的工程实例。</summary>
    /// <param name="project">工程数据。</param>
    /// <param name="directoryPath">工程所在目录；不落盘时留空。</param>
    /// <param name="isSaved">是否视为"已经落盘、没有改动"。</param>
    /// <returns>工程会话。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 用于两条路：从磁盘读回来的工程（<paramref name="isSaved"/> 为 <see langword="true"/>），
    /// 以及启动时装载的内置演示工程（没有目录，但也不该一打开就提示"未保存"）。
    /// </remarks>
    public static ProjectWorkspace Adopt(Project project, string? directoryPath = null, bool isSaved = true)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new ProjectWorkspace(project, directoryPath, isSaved);
    }

    /// <summary>按新建工程的默认值造一个工程。</summary>
    /// <param name="name">工程名。</param>
    /// <param name="composition">合成设置；留空时使用 1920×1080 / 30fps 默认值。</param>
    /// <returns>新工程。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空白时抛出。</exception>
    /// <remarks>
    /// 默认画布 1920×1080 / 30fps、背景纯黑、内容时长未定（<c>null</c>），歌词为空。
    /// 时长不预设：工程里还没有音频，写一个假时长会让时间轴显示不存在的区间。
    /// 工程基线直接使用 <see cref="LyricStyle.Default"/>，不继承旧样式预设。
    /// </remarks>
    public static Project CreateDefaultProject(string name, Composition? composition = null) => Project.Create(
        name,
        composition ?? Composition.Create(1920, 1080, FrameRate.Fps30));

    /// <summary>在当前会话中开始一个全新的工程。</summary>
    /// <param name="project">新工程。</param>
    /// <param name="isSaved">默认内容可作为干净基线；已导入作者输入的新工程传 false，要求保存并参与备份。</param>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// <para>
    /// 工程编辑器里的视图模型会持有本对象。新建时必须重用这一个实例，
    /// 否则窗口使用的脏判断与视图模型使用的撤销栈会落到两份不同的会话上。
    /// </para>
    /// <para>
    /// 新工程还没有写入磁盘，所以目录为空；但刚创建的默认内容本身不算用户改动。
    /// 用户第一次编辑后，引用比较会自然地把它标记为有未保存改动。
    /// </para>
    /// </remarks>
    public void StartNew(Project project, bool isSaved = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ReleaseWriteLease();

        Generation++;
        Project = project;
        DirectoryPath = null;
        _saved = isSaved ? project : null;
        LastSaveIssues = [];
        History.Clear();
    }

    /// <summary>把当前工程作为启动初始化后的干净基线。</summary>
    /// <remarks>
    /// 仅用于启动时补齐运行环境数据（例如内置演示工程的临时音频路径）。
    /// 用户编辑后的工程必须通过保存来更新基线，不能用此方法绕过未保存确认。
    /// </remarks>
    public void AcceptCurrentAsSaved()
    {
        _saved = Project;
        LastSaveIssues = [];
        History.Clear();
    }

    /// <summary>接管一份"从自动保存恢复出来的"工程。</summary>
    /// <param name="project">恢复出来的工程数据。</param>
    /// <param name="directoryPath">这份备份对应的工程目录；新工程为 <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 与 <see cref="Adopt" /> 的区别只有一点，但很关键：恢复出来的内容与盘上的文件
    /// <b>并不相同</b>，因此必须算"有未保存改动"（<c>asSaved: false</c>）——
    /// 恢复完就显示"已保存"，用户一关窗口就真的丢了。历史上也一并清掉：
    /// 撤销栈属于原会话，跨会话回溯没有意义。
    /// </remarks>
    public void Recover(Project project, string? directoryPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        using ProjectWriteLeaseReservation? reservation = !string.IsNullOrWhiteSpace(directoryPath)
            ? PrepareSaveLease(directoryPath) : null;
        if (reservation?.Lease is { }) AdoptWriteLease(reservation.Take());
        else if (reservation is null) ReleaseWriteLease();

        Generation++;
        Project = project;
        DirectoryPath = string.IsNullOrWhiteSpace(directoryPath) ? null : Path.GetFullPath(directoryPath);
        _saved = null;

        History.Clear();
    }

    /// <summary>用新的工程实例替换当前数据（界面每次编辑后调用）。</summary>
    /// <param name="project">新的工程实例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public void Replace(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!ReferenceEquals(Project, project)) History.InvalidateEditContinuity();
        Project = project;
    }

    /// <summary>执行一条可撤销的命令，并把它记进历史。</summary>
    /// <param name="command">要执行的命令。</param>
    /// <returns>执行后的工程。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> 为 <see langword="null"/> 时抛出。</exception>
    public Project Execute(IProjectCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        Project = History.Execute(command, Project);
        return Project;
    }

    internal Project ExecuteEdit(IProjectCommand command, ProjectEditMergeIdentity identity)
    {
        Project = History.ExecuteEdit(command, Project, identity);
        return Project;
    }

    /// <summary>撤销一步。</summary>
    /// <returns>撤销后的工程。</returns>
    /// <exception cref="InvalidOperationException">没有可撤销项时抛出。</exception>
    public Project Undo()
    {
        Project = History.Undo(Project);
        return Project;
    }

    /// <summary>重做一步。</summary>
    /// <returns>重做后的工程。</returns>
    /// <exception cref="InvalidOperationException">没有可重做项时抛出。</exception>
    public Project Redo()
    {
        Project = History.Redo(Project);
        return Project;
    }

    /// <summary>开始一个合并组：组内的多次改写合成一步撤销。</summary>
    /// <param name="name">这一组显示的操作标识。</param>
    /// <returns>结束该组时释放。</returns>
    public IDisposable BeginGroup(string name) => History.BeginGroup(name);

    /// <summary>打开指定目录里的工程；成功时替换当前数据。</summary>
    /// <param name="directoryPath">项目包目录。</param>
    /// <returns>读取与校验结果。</returns>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> 为空白时抛出。</exception>
    /// <remarks>
    /// 读取失败（结构层面）时<b>不动</b>当前工程：用户的现有工作不该因为点错目录就消失。
    /// 读到了工程但带校验错误时照常打开，让用户看到并修正问题（docs/development.md §6.3）。
    /// </remarks>
    public ProjectLoadResult Open(string directoryPath)
    {
        using ProjectWriteLeaseReservation reservation = PrepareSaveLease(directoryPath);
        ProjectLoadResult result = ProjectStore.Load(directoryPath);

        if (result.Project is { } loaded)
        {
            Generation++;
            AdoptWriteLease(reservation.Take());
            Project = loaded;
            DirectoryPath = Path.GetFullPath(directoryPath);
            _saved = loaded;

            // 历史属于上一个工程，不能跨工程回溯。
            History.Clear();
        }

        return result;
    }

    /// <summary>把当前工程写回原目录。</summary>
    /// <exception cref="InvalidOperationException">工程还没有落盘位置时抛出。</exception>
    public void Save()
    {
        if (DirectoryPath is not { } directoryPath)
        {
            throw new InvalidOperationException("这个工程还没有保存位置，请先用「另存为」。");
        }

        SaveAs(directoryPath);
    }

    /// <summary>把当前工程写到指定目录，并把该目录记为新的落盘位置。</summary>
    /// <param name="directoryPath">目标项目包目录。</param>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> 为空白时抛出。</exception>
    /// <remarks>
    /// 写盘由 <see cref="ProjectStore.Save" /> 负责：目录不存在会创建，
    /// 歌词内容地址先发布，根入口最后原子替换；成功之后才更新路径及保存引用。
    /// </remarks>
    public void SaveAs(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        // 所有保存发布完整媒体／分析／包闭包；成功后才更新保存引用。
        List<ProjectIssue> diagnostics = [];
        ProjectSaveSnapshot snapshot = CaptureSave(DateTimeOffset.UtcNow);
        using ProjectWriteLeaseReservation reservation = PrepareSaveLease(directoryPath);
        ProjectStore.Save(snapshot.Project, directoryPath, diagnostics);
        AcceptSave(snapshot, directoryPath, diagnostics.ToArray(), reservation);
    }
}
