using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

/// <summary>撤销/重做栈的测试。</summary>
public sealed class ProjectHistoryTests
{
    [Fact]
    public void 执行后可以撤销回到原实例()
    {
        Project origin = Build("A");
        ProjectHistory history = new();

        Project after = history.Execute(Rename("B"), origin);

        Assert.Equal("B", after.Name);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal("rename", history.UndoName);

        // 撤销要还原成**同一个实例**：按引用比较的脏判断与渲染快照缓存都依赖这一点。
        Assert.Same(origin, history.Undo(after));
    }

    [Fact]
    public void 撤销之后可以重做()
    {
        Project origin = Build("A");
        ProjectHistory history = new();

        Project after = history.Execute(Rename("B"), origin);
        Project undone = history.Undo(after);
        Project redone = history.Redo(undone);

        Assert.Equal("B", redone.Name);
        Assert.Same(after, redone);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void 撤销之后又改动会清空重做栈()
    {
        ProjectHistory history = new();
        Project origin = Build("A");

        Project b = history.Execute(Rename("B"), origin);
        Project a = history.Undo(b);
        Project c = history.Execute(Rename("C"), a);

        Assert.Equal("C", c.Name);
        Assert.False(history.CanRedo);
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void 没有产生变化的命令不入栈()
    {
        // 否则会出现"连按撤销却什么都不发生"的空步骤。
        Project origin = Build("A");
        ProjectHistory history = new();

        Project result = history.Execute(
            ProjectCommand.Create("noop", static project => project),
            origin);

        Assert.Same(origin, result);
        Assert.False(history.CanUndo);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void 时间窗内的同名可合并命令合成一步()
    {
        FakeClock clock = new();
        ProjectHistory history = new(clockMs: clock.Now);
        Project origin = Build("A");

        Project first = history.Execute(Rename("B", mergeable: true), origin);
        clock.Advance(100);
        Project second = history.Execute(Rename("C", mergeable: true), first);

        Assert.Equal(1, history.UndoCount);
        Assert.Same(origin, history.Undo(second));
    }

    [Fact]
    public void 超过时间窗的同名命令分成两步()
    {
        FakeClock clock = new();
        ProjectHistory history = new(mergeWindowMs: 500, clockMs: clock.Now);
        Project origin = Build("A");

        Project first = history.Execute(Rename("B", mergeable: true), origin);
        clock.Advance(600);
        Project second = history.Execute(Rename("C", mergeable: true), first);

        Assert.Equal(2, history.UndoCount);

        Project back = history.Undo(second);

        Assert.Equal("B", back.Name);
    }

    [Fact]
    public void 明确断开合并后不再并入上一步()
    {
        FakeClock clock = new();
        ProjectHistory history = new(clockMs: clock.Now);
        Project origin = Build("A");

        Project first = history.Execute(Rename("B", mergeable: true), origin);
        history.BreakMergeGroup();
        Project second = history.Execute(Rename("C", mergeable: true), first);

        Assert.Equal(2, history.UndoCount);

        // 撤销的是"改成 C"这一步，因此回到的是 B 的状态。
        Project back = history.Undo(second);

        Assert.Equal("B", back.Name);
        Assert.Equal("A", history.Undo(back).Name);
    }

    [Fact]
    public void 不可合并的命令即使同名也各占一步()
    {
        ProjectHistory history = new();
        Project origin = Build("A");

        Project first = history.Execute(Rename("B"), origin);
        Project second = history.Execute(Rename("C"), first);

        Assert.Equal(2, history.UndoCount);
    }

    [Fact]
    public void 合并组内的多次改写合成一步()
    {
        // 打点一次会写两次数据（写时间 + 把占位终点标成估算），必须只占一步撤销。
        ProjectHistory history = new();
        Project origin = Build("A");

        Project result;

        using (history.BeginGroup("stamp"))
        {
            Project step1 = history.Execute(Rename("B"), origin);
            result = history.Execute(SetComposition(step1), step1);
        }

        Assert.Equal(1, history.UndoCount);
        Assert.Equal("stamp", history.UndoName);
        Assert.Same(origin, history.Undo(result));
    }

    [Fact]
    public void 合并组里没有变化时不留痕迹()
    {
        ProjectHistory history = new();
        Project origin = Build("A");

        using (history.BeginGroup("stamp"))
        {
            history.Execute(ProjectCommand.Create("noop", static project => project), origin);
        }

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void 组内第一条命令不会并进组之前的旧步骤()
    {
        // 组刚开始就并入旧步骤，会静默吃掉一个与本次动作无关的撤销步。
        ProjectHistory history = new();
        Project origin = Build("A");

        Project before = history.Execute(Rename("B"), origin);
        Project result;

        using (history.BeginGroup("stamp"))
        {
            history.Execute(ProjectCommand.Create("noop", static project => project), before);
            result = history.Execute(Rename("C"), before);
        }

        Assert.Equal(2, history.UndoCount);
        Assert.Equal("stamp", history.UndoName);
        Assert.Equal("B", history.Undo(result).Name);
    }

    [Fact]
    public void 嵌套合并组只在最外层结束时收官()
    {
        ProjectHistory history = new();
        Project origin = Build("A");

        Project result;

        using (history.BeginGroup("outer"))
        {
            using (history.BeginGroup("inner"))
            {
                Project step = history.Execute(Rename("B"), origin);
                result = history.Execute(SetComposition(step), step);
            }

            Assert.True(history.IsInGroup);
        }

        Assert.False(history.IsInGroup);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal("outer", history.UndoName);
    }

    [Fact]
    public void 撤销栈超过容量时丢掉最旧的()
    {
        ProjectHistory history = new(capacity: 3);
        Project current = Build("A");

        for (int i = 0; i < 5; i++)
        {
            current = history.Execute(Rename($"n{i}"), current);
        }

        Assert.Equal(3, history.UndoCount);

        // 只能退回三步，最旧的两步已经被丢弃。
        current = history.Undo(current);
        current = history.Undo(current);
        current = history.Undo(current);

        Assert.Equal("n1", current.Name);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void 当前工程与栈顶不一致时拒绝撤销()
    {
        // 有人绕过历史直接改了工程：继续撤销会把那些改动一起卷走。
        // 宁可立刻报错，也不要静默丢数据。
        ProjectHistory history = new();
        Project origin = Build("A");
        Project after = history.Execute(Rename("B"), origin);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Undo(Build("别的实例")));

        Assert.Contains("不一致", exception.Message, StringComparison.Ordinal);

        // 报错没有副作用：拿正确的实例照样能撤销，回到最初的状态。
        Assert.Same(origin, history.Undo(after));
    }

    [Fact]
    public void 没有可撤销项时撤销会报错()
    {
        ProjectHistory history = new();

        Assert.Throws<InvalidOperationException>(() => history.Undo(Build("A")));
        Assert.Throws<InvalidOperationException>(() => history.Redo(Build("A")));
    }

    [Fact]
    public void 合并组未结束时不能撤销()
    {
        ProjectHistory history = new();
        Project origin = Build("A");
        Project current = history.Execute(Rename("B"), origin);

        using (history.BeginGroup("stamp"))
        {
            Assert.Throws<InvalidOperationException>(() => history.Undo(current));
        }
    }

    [Fact]
    public void 清空后两端都空()
    {
        ProjectHistory history = new();
        Project origin = Build("A");
        Project after = history.Execute(Rename("B"), origin);
        history.Undo(after);

        int changed = 0;
        history.Changed += (_, _) => changed++;

        history.Clear();

        Assert.Equal(1, changed);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void 每一步变化都会通知界面()
    {
        ProjectHistory history = new();
        Project origin = Build("A");
        int changed = 0;
        history.Changed += (_, _) => changed++;

        Project after = history.Execute(Rename("B"), origin);
        history.Undo(after);

        Assert.Equal(2, changed);
    }

    private static IProjectCommand Rename(string name, bool mergeable = false) =>
        ProjectCommand.Create("rename", project => project with { Name = name }, mergeable);

    private static IProjectCommand SetComposition(Project project) =>
        ProjectCommand.Create(
            "composition",
            _ => project with { Composition = project.Composition with { CanvasWidth = 1280 } });

    private static Project Build(string name) => Project.Create(name);

    /// <summary>可控时钟，让"时间窗"这类规则不依赖真实等待。</summary>
    private sealed class FakeClock
    {
        private long _ms;

        public long Now() => _ms;

        public void Advance(long milliseconds) => _ms += milliseconds;
    }
}
