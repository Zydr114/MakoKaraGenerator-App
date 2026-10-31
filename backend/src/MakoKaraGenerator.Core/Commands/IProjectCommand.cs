using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Commands;

/// <summary>
/// 对工程的一次可撤销修改。
/// </summary>
/// <remarks>
/// <para>
/// 这是 roadmap CORE-07 要求的"命令"接口。之所以不做成传统的 <c>Do</c>/<c>Undo</c> 成对方法，
/// 是因为 <see cref="Project" /> 与整个歌词模型都是<b>不可变</b>的：
/// 撤销只需要把旧实例换回来，"反向操作"根本不必写。
/// 传统写法的 Undo 是每个操作各自实现一遍、最容易写成不对称 bug 的地方，
/// 这里从设计上就不存在。
/// </para>
/// <para>
/// 实现必须是<b>纯函数</b>：不修改传入的工程，只返回新实例；不做 I/O，
/// 不依赖界面状态。返回<b>同一个实例</b>表示"什么也没改"，历史层会直接丢弃它。
/// </para>
/// <para>
/// 新增一种操作只需要实现这个接口（或直接用 <see cref="ProjectCommand" /> 的工厂），
/// 撤销/重做引擎不需要任何改动。
/// </para>
/// </remarks>
public interface IProjectCommand
{
    /// <summary>稳定标识，用于合并判断与界面文案查表。</summary>
    /// <remarks>
    /// 不是给用户看的文本（面向用户的文案必须资源化，ADR-0001）。
    /// 建议用 <c>域.动作</c> 形式，例如 <c>token.time</c>、<c>line.split</c>。
    /// </remarks>
    string Name { get; }

    /// <summary>连续同类手势是否允许与上一条合并成一步撤销。</summary>
    /// <remarks>
    /// 拖拽会以每帧一次的频率产生命令，逐条入栈等于"撤销 60 次才退回到起点"。
    /// 标记为 <see langword="true"/> 的命令在时间窗内与同名命令合成一步；
    /// 打点、拆分这类一次性动作应当保持 <see langword="false"/>。
    /// </remarks>
    bool Mergeable { get; }

    /// <summary>把命令应用到工程上。</summary>
    /// <param name="project">应用前的工程。</param>
    /// <returns>应用后的工程；没有变化时应当返回原实例。</returns>
    Project Apply(Project project);
}
