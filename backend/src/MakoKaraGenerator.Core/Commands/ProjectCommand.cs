using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Commands;

/// <summary>
/// 用委托表达的命令，以及把若干命令合成一步的工具。
/// </summary>
/// <remarks>
/// 绝大多数操作就是"换掉工程的一块"，用工厂一行就能表达，不必为每个操作单独写一个类。
/// 需要携带参数、要在多处复用的操作，仍然可以直接实现 <see cref="IProjectCommand" />，
/// 两者在历史里没有区别。
/// </remarks>
public static class ProjectCommand
{
    /// <summary>用"纯函数 + 标识"造一个命令。</summary>
    /// <param name="name">稳定标识。</param>
    /// <param name="apply">应用函数，必须是纯函数。</param>
    /// <param name="mergeable">是否允许与时间窗内的同名命令合并成一步。</param>
    /// <returns>命令。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空白时抛出。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="apply"/> 为 <see langword="null"/> 时抛出。</exception>
    public static IProjectCommand Create(string name, Func<Project, Project> apply, bool mergeable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(apply);

        return new DelegateCommand(name, mergeable, apply);
    }

    /// <summary>把若干命令按顺序合成一个命令，作为一步撤销。</summary>
    /// <param name="name">合成后的稳定标识。</param>
    /// <param name="commands">要按顺序应用的命令。</param>
    /// <returns>命令。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空白、或没有给出任何命令时抛出。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 用于"一次用户动作产生多次数据改写"的场合，例如打点会同时写入时间与把占位终点标成估算。
    /// 合成后的命令<b>不参与合并</b>：它本来就是一次完整的动作。
    /// </remarks>
    public static IProjectCommand Combine(string name, params IProjectCommand[] commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(commands);

        if (commands.Length == 0)
        {
            throw new ArgumentException("至少要有一个命令。", nameof(commands));
        }

        ImmutableArray<IProjectCommand> copy = [.. commands];

        if (copy.Any(static command => command is null))
        {
            throw new ArgumentNullException(nameof(commands), "命令里不能有 null。");
        }

        return new CompositeCommand(name, copy);
    }

    // 用类而不是位置记录：接口成员叫 Apply，位置记录会生成同名属性，两者无法共存。
    private sealed class DelegateCommand(
        string name,
        bool mergeable,
        Func<Project, Project> apply) : IProjectCommand
    {
        public string Name { get; } = name;

        public bool Mergeable { get; } = mergeable;

        public Project Apply(Project project) => apply(project);
    }

    private sealed class CompositeCommand(string name, ImmutableArray<IProjectCommand> commands) : IProjectCommand
    {
        public string Name { get; } = name;

        /// <summary>合成命令永远是完整动作，不参与手势合并。</summary>
        public bool Mergeable => false;

        public Project Apply(Project project)
        {
            Project current = project;

            foreach (IProjectCommand command in commands)
            {
                // 前面的命令若没有产生变化，后面仍要继续应用，因此逐个传入当前实例。
                current = command.Apply(current);
            }

            return current;
        }
    }
}
