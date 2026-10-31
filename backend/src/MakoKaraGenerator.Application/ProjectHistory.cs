using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Application.Editing;

namespace MakoKaraGenerator.Application;

/// <summary>
/// 撤销 / 重做栈。
/// </summary>
/// <remarks>
/// <para>
/// docs/development.md §13 把撤销/重做放在用例层。引擎只做三件事：
/// 执行命令、记住"改动前后的工程实例"、在需要时把实例换回去。
/// 因为工程模型不可变，撤销<b>不</b>需要每个操作自己写反向逻辑——那是这类系统最常见
/// 的 bug 来源（正反两侧不对称）。代价是每个历史项持有两个工程实例；
/// 由于整棵树是结构共享的，实际多占的内存只有被改动的那些节点。
/// </para>
/// <para>
/// <b>手势合并</b>：拖拽每帧都会产生一条命令，逐条入栈等于"撤销 60 次才回到起点"。
/// 同名且允许合并、且距上次执行在时间窗内的命令会合成一步；
/// 手势结束时调用 <see cref="BreakMergeGroup"/> 明确断开，不必依赖时间猜。
/// </para>
/// <para>
/// <b>空操作不入栈</b>：命令返回同一个实例即视为没有变化（例如"把样式设成它本来就有的值"）。
/// </para>
/// </remarks>
public sealed class ProjectHistory
{
    /// <summary>默认保留的历史步数。</summary>
    public const int DefaultCapacity = 200;

    /// <summary>默认的手势合并时间窗（毫秒）。</summary>
    public const long DefaultMergeWindowMs = 900;

    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];
    private readonly int _capacity;
    private readonly long _mergeWindowMs;
    private readonly Func<long> _clockMs;

    private long _lastExecuteMs = long.MinValue;
    private string? _groupName;
    private int _groupDepth;
    private int _groupEntryCount;

    // 仅表示历史连续性，不持有工程状态；即使撤销回同一个工程引用也不能复活旧手势。
    internal object EditContinuityToken { get; private set; } = new();

    /// <summary>创建一个历史栈。</summary>
    /// <param name="capacity">最多保留的步数；超出后从最旧的一端丢弃。</param>
    /// <param name="mergeWindowMs">手势合并时间窗（毫秒）。</param>
    /// <param name="clockMs">单调递增的毫秒时钟；仅供测试注入假时钟。</param>
    /// <exception cref="ArgumentOutOfRangeException">容量或时间窗非正时抛出。</exception>
    public ProjectHistory(
        int capacity = DefaultCapacity,
        long mergeWindowMs = DefaultMergeWindowMs,
        Func<long>? clockMs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mergeWindowMs);

        _capacity = capacity;
        _mergeWindowMs = mergeWindowMs;
        _clockMs = clockMs ?? (static () => Environment.TickCount64);
    }

    /// <summary>历史发生变化时触发（入栈、撤销、重做、清空）。</summary>
    public event EventHandler? Changed;

    /// <summary>是否可以撤销。</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>是否可以重做。</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>下一步撤销对应的操作标识；没有可撤销项时为 <see langword="null"/>。</summary>
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;

    /// <summary>下一步重做对应的操作标识；没有可重做项时为 <see langword="null"/>。</summary>
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    /// <summary>可撤销步数。</summary>
    public int UndoCount => _undo.Count;

    /// <summary>可重做步数。</summary>
    public int RedoCount => _redo.Count;

    /// <summary>当前可撤销/重做项仍持有的不可变工程快照；生命周期所有者用于保留资源。</summary>
    public IEnumerable<Project> RetainedProjects => _undo.Concat(_redo).SelectMany(entry => new[] { entry.Before, entry.After });

    /// <summary>是否正处于某个合并组里。</summary>
    public bool IsInGroup => _groupDepth > 0;

    /// <summary>执行一条命令并记录历史。</summary>
    /// <param name="command">要执行的命令。</param>
    /// <param name="current">当前工程。</param>
    /// <returns>执行后的工程；命令没有产生变化时返回原实例。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>执行新命令会清空重做栈——这正是"撤销之后又改了一笔"的常规语义。</remarks>
    public Project Execute(IProjectCommand command, Project current) => ExecuteCore(command, current, null);

    internal Project ExecuteEdit(IProjectCommand command, Project current, ProjectEditMergeIdentity identity) =>
        ExecuteCore(command, current, identity);

    internal void InvalidateEditContinuity() => EditContinuityToken = new();

    internal void EndEditMerge(ProjectEditMergeIdentity identity, object continuityToken)
    {
        if (ReferenceEquals(EditContinuityToken, continuityToken)
            && _undo.Count > 0 && Equals(_undo[^1].MergeIdentity, identity))
            BreakMergeGroup();
    }

    private Project ExecuteCore(IProjectCommand command, Project current, ProjectEditMergeIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(current);

        Project after = command.Apply(current);

        if (ReferenceEquals(after, current))
        {
            // 没有变化就不必入栈，否则会出现"连按撤销却什么都不发生"的空步骤。
            return current;
        }

        EditContinuityToken = new();
        if (TryMerge(command, current, after, identity))
        {
            return after;
        }

        Push(new Entry(command.Name, current, after, identity));
        _lastExecuteMs = _clockMs();

        Changed?.Invoke(this, EventArgs.Empty);
        return after;
    }

    /// <summary>撤销一步。</summary>
    /// <param name="current">当前工程。</param>
    /// <returns>撤销后的工程。</returns>
    /// <exception cref="InvalidOperationException">没有可撤销项、正处于合并组中、或当前工程与栈顶不一致时抛出。</exception>
    public Project Undo(Project current)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnsureNotInGroup(nameof(Undo));

        if (_undo.Count == 0)
        {
            throw new InvalidOperationException("没有可以撤销的操作。");
        }

        Entry entry = _undo[^1];

        // 当前工程必须正是这一步产生的结果。不一致说明有人绕过历史直接改了工程，
        // 继续撤销会把那些改动一起卷走——宁可立刻报错，也不要静默丢数据。
        if (!ReferenceEquals(entry.After, current))
        {
            throw new InvalidOperationException(
                $"当前工程与撤销栈不一致：栈顶是「{entry.Name}」的结果，但传入的不是它。");
        }

        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        _lastExecuteMs = long.MinValue;
        EditContinuityToken = new();

        Changed?.Invoke(this, EventArgs.Empty);
        return entry.Before;
    }

    /// <summary>重做一步。</summary>
    /// <param name="current">当前工程。</param>
    /// <returns>重做后的工程。</returns>
    /// <exception cref="InvalidOperationException">没有可重做项、正处于合并组中、或当前工程与栈顶不一致时抛出。</exception>
    public Project Redo(Project current)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnsureNotInGroup(nameof(Redo));

        if (_redo.Count == 0)
        {
            throw new InvalidOperationException("没有可以重做的操作。");
        }

        Entry entry = _redo[^1];

        if (!ReferenceEquals(entry.Before, current))
        {
            throw new InvalidOperationException(
                $"当前工程与重做栈不一致：栈顶是「{entry.Name}」之前的状态，但传入的不是它。");
        }

        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        _lastExecuteMs = long.MinValue;
        EditContinuityToken = new();

        Changed?.Invoke(this, EventArgs.Empty);
        return entry.After;
    }

    /// <summary>开始一个合并组：组内执行的命令无论标识都合成一步撤销。</summary>
    /// <param name="name">这一组在界面上显示的操作标识。</param>
    /// <returns>结束该组时释放。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空白时抛出。</exception>
    /// <remarks>
    /// 用于"一次用户动作产生多次数据改写"的场合（打点会同时写时间与标记估算状态）。
    /// 支持嵌套，只有最外层结束时才真正收官；组内没有产生任何变化时不留痕迹。
    /// </remarks>
    public IDisposable BeginGroup(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_groupDepth == 0)
        {
            EditContinuityToken = new();
            _groupName = name;
            _groupEntryCount = _undo.Count;
        }

        _groupDepth++;

        return new GroupScope(this);
    }

    /// <summary>结束当前手势的合并：之后的同名命令不会再并入上一步。</summary>
    /// <remarks>
    /// 拖拽结束时调用。时间窗只是兜底，真正可靠的边界来自"手势结束了"这件事本身。
    /// </remarks>
    public void BreakMergeGroup()
    {
        _lastExecuteMs = long.MinValue;
        EditContinuityToken = new();
    }

    /// <summary>清空历史（打开或新建工程时用）。</summary>
    public void Clear()
    {
        bool had = _undo.Count > 0 || _redo.Count > 0;

        _undo.Clear();
        _redo.Clear();
        _groupDepth = 0;
        _groupName = null;
        _groupEntryCount = 0;
        _lastExecuteMs = long.MinValue;
        EditContinuityToken = new();

        if (had)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryMerge(IProjectCommand command, Project current, Project after, ProjectEditMergeIdentity? identity)
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        if (_groupDepth > 0 && !HasGroupEntry)
        {
            // 组刚开始、还没推过自己的条目：此时栈顶是组之前的旧步骤，
            // 绝不能把组里的改动并进去（那会静默吃掉一个与本次动作无关的步骤）。
            return false;
        }

        Entry previous = _undo[^1];
        bool matchingIdentity = Equals(previous.MergeIdentity, identity);
        bool mergeable = _groupDepth > 0 || (command.Mergeable
            && matchingIdentity
            && (identity is null || ReferenceEquals(previous.After, current))
            && command.Name == previous.Name
            && _lastExecuteMs != long.MinValue
            && (identity?.IgnoreTimeWindow == true || _clockMs() - _lastExecuteMs <= _mergeWindowMs));

        if (!mergeable)
        {
            return false;
        }

        _undo[^1] = _undo[^1] with { After = after };
        _redo.Clear();

        if (_groupDepth == 0)
        {
            // 刷新时间戳：连续拖拽里每一帧都在上一帧的窗口内，因此整段手势合为一步。
            _lastExecuteMs = _clockMs();
        }

        return true;
    }

    /// <summary>当前合并组是否已经推入过自己的条目。</summary>
    private bool HasGroupEntry => _undo.Count > _groupEntryCount;

    private void Push(Entry entry)
    {
        if (_groupDepth > 0)
        {
            // 显式短组沿用原合同（打点的时间与状态命令合为一步），不留下单个命令的身份。
            entry = entry with { Name = _groupName ?? entry.Name, MergeIdentity = null };
        }

        _undo.Add(entry);
        _redo.Clear();

        if (_undo.Count > _capacity)
        {
            // 从最旧的一端丢弃。每项自带 Before/After，截断不会破坏回溯链。
            _undo.RemoveRange(0, _undo.Count - _capacity);
        }
    }

    private void EnsureNotInGroup(string operation)
    {
        if (_groupDepth > 0)
        {
            throw new InvalidOperationException($"合并组未结束，不能执行 {operation}。");
        }
    }

    private void EndGroup()
    {
        if (_groupDepth == 0)
        {
            return;
        }

        _groupDepth--;

        if (_groupDepth == 0)
        {
            _groupName = null;
            _lastExecuteMs = long.MinValue;
            EditContinuityToken = new();
        }
    }

    private sealed record Entry(string Name, Project Before, Project After, ProjectEditMergeIdentity? MergeIdentity);

    private sealed class GroupScope(ProjectHistory history) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            history.EndGroup();
        }
    }
}
