using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>
/// 渲染管线的唯一入口。
/// </summary>
/// <remarks>
/// <para>
/// 对应 docs/development.md §10.2。实现方（例如 Skia 后端）只依赖本程序集与 Core，
/// <b>不得</b>依赖 Avalonia 控件：预览与离屏导出共用同一实现，
/// 二者的差异只体现在 <see cref="RenderRequest.Target" /> 与
/// <see cref="RenderRequest.Quality" /> 上（docs/development.md §10.3）。
/// </para>
/// <para>
/// 实现不共享可变帧状态：工程异步会话在后台串行调用，导出持有独立会话；静态试样可在 UI 调用。
/// 调用方各自持有一个实例，管线内部不得保存"当前帧"之类的可变状态。
/// </para>
/// </remarks>
public interface IRenderPipeline
{
    /// <summary>该后端用于诊断展示的名称。</summary>
    string BackendName { get; }

    /// <summary>渲染一帧。</summary>
    /// <param name="request">渲染请求；其中的项目快照在本次调用期间不会被改变。</param>
    /// <returns>渲染结果。调用方负责 <see cref="RenderFrame.Dispose" />。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="OperationCanceledException"><see cref="RenderRequest.CancellationToken"/> 已被取消时抛出。</exception>
    RenderFrame Render(RenderRequest request);
}

/// <summary>
/// 一次渲染请求的全部输入。
/// </summary>
/// <remarks>
/// 本类型是不可变值：所有输入（项目快照、时间、目标尺寸、质量档位）在一次渲染中都不变，
/// 因此渲染过程中界面上的编辑不会影响正在绘制的这一帧
/// （docs/development.md §10.2，roadmap RENDER-01 验收项）。
/// </remarks>
public sealed record RenderRequest
{
    /// <summary>渲染所依据的项目快照。</summary>
    public required ProjectSnapshot Project { get; init; }

    /// <summary>要渲染的时刻（微秒）。</summary>
    public required long TimeUs { get; init; }

    /// <summary>
    /// 渲染目标尺寸；<see langword="null"/> 表示按合成设置的画布尺寸输出。
    /// </summary>
    /// <remarks>
    /// 预览可以给一个更小的目标以降低开销，但管线必须按同一套布局规则等比绘制，
    /// 否则"草稿模式"会改变画面语义（docs/development.md §10.3、roadmap RENDER-04）。
    /// </remarks>
    public RenderTarget? Target { get; init; }

    /// <summary>渲染质量档位。</summary>
    public RenderQuality Quality { get; init; } = RenderQuality.Standard;

    /// <summary>
    /// 是否输出<b>透明画布</b>：画布从全透明开始，并且不画背景层（roadmap `EXPORT-07`）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是<b>导出选项</b>而不是工程数据：同一次编辑可以既导不透明成片又导透明歌词层，
    /// 工程里一个字段都不用改（docs/development.md §4.2 的"透明歌词视频"）。
    /// </para>
    /// <para>
    /// 置为 <see langword="true"/> 时背景色与背景图<b>都不画</b>：这一帧的用途是当叠加层，
    /// 任何底色都会让它失去意义。<see cref="ProjectSnapshot.Lyrics" /> 里的样式照常生效，
    /// 只是它们变成"带 Alpha 的像素"。
    /// </para>
    /// </remarks>
    public bool TransparentBackground { get; init; }

    /// <summary>布局编辑静态试样的行 ID；空值按正常时间采样，导出不设置此值。</summary>
    public string? LayoutLineId { get; init; }

    /// <summary>已求值的中立效果计划；启用效果不得缺失，也不得在绘制时重新执行脚本。</summary>
    public PreparedRenderFrame? PreparedFrame { get; init; }

    /// <summary>取消令牌。</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>取得本次渲染实际使用的目标尺寸。</summary>
    /// <returns>请求指定的目标，或按合成设置的整画布目标。</returns>
    public RenderTarget ResolveTarget() => Target ?? RenderTarget.FromComposition(Project.Composition);

    /// <summary>为快照与时刻构造一个请求。</summary>
    /// <param name="project">项目快照。</param>
    /// <param name="timeUs">渲染时刻（微秒）。</param>
    /// <returns>新的渲染请求。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static RenderRequest At(ProjectSnapshot project, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new RenderRequest { Project = project, TimeUs = timeUs };
    }
}

/// <summary>
/// 渲染质量档位。
/// </summary>
/// <remarks>
/// <para>
/// 质量只允许影响<b>画质成本</b>，不允许影响语义。具体来说：
/// </para>
/// <list type="bullet">
///   <item>可以改变：抗锯齿开关、阴影/模糊的采样质量、是否复用帧缓存；</item>
///   <item><b>不得</b>改变：文字布局与元素边界、时间采样结果、颜色取值、图层顺序。</item>
/// </list>
/// <para>
/// 因此同一快照与时刻在不同档位下，"哪一段高亮到哪里"必须完全一致。
/// 界面上必须显示当前档位，避免用户误以为最终输出也缺少某效果（docs/development.md §10.3）。
/// </para>
/// </remarks>
public enum RenderQuality
{
    /// <summary>草稿：允许关闭抗锯齿，用于即时预览；不得跳过已启用的效果。</summary>
    Draft,

    /// <summary>标准：预览默认档位，画质与开销折中。</summary>
    Standard,

    /// <summary>最终：导出使用的档位，允许使用最贵的画质选项。</summary>
    Final,
}
