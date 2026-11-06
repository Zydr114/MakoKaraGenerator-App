using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>
/// 渲染用的项目快照：渲染器能看到的<b>全部</b>项目数据。
/// </summary>
/// <remarks>
/// <para>
/// 对应 docs/development.md §10.2。快照存在的意义是切断渲染与编辑状态的耦合：
/// 渲染器只读快照，读不到"当前选中行""正在拖动的边界"这类界面状态，
/// 因此同一快照 + 同一时刻永远得到同一帧，预览与导出也才能真的共用一套规则。
/// </para>
/// <para>
/// 本类型是不可变值。它<b>不做深拷贝</b>：<see cref="MakoKaraGenerator.Core.Projects.Project" />、
/// <see cref="Composition" />、<see cref="LyricsDocument" /> 及其下的
/// <see cref="LyricLine" /> / <see cref="LyricToken" /> 都是不可变记录，
/// 编辑产生新实例而不是就地修改，所以共享引用是安全的，
/// 每次编辑后重建快照的代价也只是几个引用（docs/decisions.md ADR-0006）。
/// </para>
/// </remarks>
public sealed record ProjectSnapshot
{
    /// <summary>冻结效果输入闭包的指纹；非空时渲染必须消费匹配准备帧。R4 由持久实例投影填入。</summary>
    public string? EffectInputsFingerprint { get; init; }

    /// <summary>组合根固定的完整媒体输入值；不包含采样器、租约或媒体资源授权。</summary>
    public MakoKaraGenerator.Core.Media.MediaFeatureInput? MediaFeatureInput { get; init; }

    /// <summary>持久效果源闭包，不包含运行状态。</summary>
    public MakoKaraGenerator.Core.Effects.ProjectEffects Effects { get; init; } = MakoKaraGenerator.Core.Effects.ProjectEffects.Empty;

    /// <summary>固定应用源；渲染不会查询应用库，执行实例由快照创建时统一投影。</summary>
    public PresetApplications PresetApplications { get; init; } = PresetApplications.Empty;

    /// <summary>源音频的固定媒体身份。</summary>
    public MediaFileRevision? AudioMedia { get; init; }

    /// <summary>固定音频源路径，用于区分未分析源和无音频；不由脚本直接访问文件。</summary>
    public string? AudioPath { get; init; }

    /// <summary>已接受的完整分析源数据。</summary>
    public ProjectAudioAnalysis? AudioAnalysis { get; init; }

    /// <summary>项目名称（用于窗口标题、导出文件名与诊断）。</summary>
    public required string Name { get; init; }

    /// <summary>稳定的歌词展示定义。</summary>
    public LyricPresentationDefinition LyricPresentation { get; init; } = LyricPresentationDefinition.Empty;

    /// <summary>项目 schema 版本，随快照一起带入诊断信息。</summary>
    public int SchemaVersion { get; init; } = Project.CurrentSchemaVersion;

    /// <summary>合成设置：画布尺寸、帧率、背景色与输出时长。</summary>
    public Composition Composition { get; init; } = new();

    /// <summary>歌词文档。</summary>
    public LyricsDocument Lyrics { get; init; } = LyricsDocument.Create();

    /// <summary>
    /// 背景源：铺在 <see cref="Composition" /> 的背景色之上的一层画面。
    /// </summary>
    /// <remarks>
    /// 渲染器<b>只看快照里的背景描述</b>，自己决定"这一帧从哪拿到画面"——
    /// 背景帧的来源是渲染侧的可替换接口，视频背景因此不需要改动绘制代码（ADR-0036）。
    /// </remarks>
    public BackgroundSource Background { get; init; } = BackgroundSource.Color;

    /// <summary>
    /// 歌词样式<b>基线</b>：渲染时与行/元素的覆盖逐项合并。
    /// </summary>
    /// <remarks>
    /// 这里存的是样式本身（工程里固化的那份），不是预设名。
    /// 预设只在编辑时把它的样式写进工程，渲染阶段不再查阅预设库——
    /// 否则预设库日后改一次，已经定稿的项目外观会跟着变。
    /// </remarks>
    public LyricStyle LyricStyle { get; init; } = LyricStyle.Default;

    /// <summary>基线样式来自哪个预设。仅作来源信息，<b>不参与渲染</b>。</summary>
    public string? LyricStylePresetId { get; init; }

    /// <summary>
    /// 工程级默认的<b>行进入动画</b>；<see langword="null"/> 表示默认不动画。
    /// </summary>
    /// <remarks>
    /// 行级覆盖在 <c>LyricLine.EnterAnimation</c> 上（快照直接持有歌词文档，
    /// 因此行级数据自动跟着快照走）。渲染时的合并规则见 <c>LineAnimationEditor.Resolve</c>。
    /// </remarks>
    public LineAnimation? EnterAnimation { get; init; }

    /// <summary>歌词行数量。</summary>
    public int LineCount => Lyrics.AllLines.Count();

    /// <summary>从项目数据创建快照。</summary>
    /// <param name="project">项目聚合根。</param>
    /// <returns>包含该项目当前数据的快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static ProjectSnapshot FromProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new ProjectSnapshot
        {
            Name = project.Name,
            Effects = PresetApplicationsProjection.Effects(project),
            PresetApplications = project.PresetApplications,
            EffectInputsFingerprint = MakoKaraGenerator.Core.Effects.ProjectEffectInputs.Fingerprint(project),
            AudioMedia = project.AudioMedia,
            AudioPath = project.AudioPath,
            AudioAnalysis = project.AudioAnalysis,
            LyricPresentation = project.LyricPresentation,
            SchemaVersion = project.SchemaVersion,
            Composition = project.Composition,
            Lyrics = project.Lyrics,
            Background = project.Background,
            LyricStyle = project.LyricStyle,
            LyricStylePresetId = project.LyricStylePresetId,
            EnterAnimation = project.EnterAnimation,
        };
    }

}
