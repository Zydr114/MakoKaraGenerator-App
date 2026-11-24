using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 项目聚合根：一个可保存、可重新打开的完整的编辑单元。
/// </summary>
/// <remarks>
/// <para>
/// 本类型是内存中的源数据模型，对应项目包目录（docs/development.md §6.1）中的
/// <c>project.json</c> 与其引用的 <c>data/lyrics/&lt;hash&gt;.json</c>。<c>cache/</c> 与 <c>backups/</c> 不属于源数据，
/// 也不出现在本模型中。
/// </para>
/// <para>
/// 本类型不可变：编辑产生新的 <see cref="Project"/> 实例。
/// 这样渲染器可以持有稳定的快照（docs/development.md §10.2），
/// 撤销/重做也可以直接保存旧实例（roadmap CORE-07）。
/// </para>
/// </remarks>
public sealed record Project
{
    /// <summary>当前实现支持的 <c>project.json</c> schema 版本。</summary>
    /// <remarks>
    /// v2 相对 v1 增加歌词样式字段（基线样式、预设标识、行/元素样式覆盖）；
    /// v3 相对 v2 增加音频路径；v4 相对 v3 给样式补上渐变、描边、阴影、不透明度与基础缩放；
    /// v5 相对 v4 增加歌词水平对齐；v6 相对 v5 增加背景源（背景图片与适配方式）；
    /// v7 移除独立的当前元素颜色字段；v11 增加可选的完整渐变描述，旧起止色字段继续保留；
    /// v12 增加可选的外部描边与方向阴影描述；v16 统一为规范嵌套样式对象。
    /// v18 集中保存歌词展示定义，v19 改为通用容器树与显示程序；v22 引用不可变歌词修订，
    /// v23 保存效果闭包，v24 增加完整媒体与分析修订，v25 增加模板来源与图元层；
    /// v26 统一源材质，v27 保存固定组合预设应用，v28 保存片段调度上下文，v29 增加辉光，
    /// v30 以效果合同 2 明确保存开发期 v1／v2 包身份；v31 样式改为两态外观（已唱可跟随未唱）、多重描边与扫字样式，
    /// 文字标记外观同样两态；v33 对应歌词 schema v21（PhraseEndHint）；v34 移除布局级进出场动画（SlotAnimation 仅保留调度，移除 EnterAnimation）。仅支持当前程序版本，不转换旧工程。
    /// </remarks>
    public const int CurrentSchemaVersion = 35;

    /// <summary>项目的 schema 版本。当前只接受当前程序版本和结构，不转换旧工程。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>项目名称。用于窗口标题与自动保存备份的文件名。</summary>
    public required string Name { get; init; }

    /// <summary>合成设置（画布、帧率、背景、输出时长）。</summary>
    public Composition Composition { get; init; } = new();

    /// <summary>歌词文档。</summary>
    public LyricsDocument Lyrics { get; init; } = LyricsDocument.Create();

    /// <summary>不可变效果包、实例、顺序和预算；编译及运行状态不进入源数据。</summary>
    public MakoKaraGenerator.Core.Effects.ProjectEffects Effects { get; init; } = MakoKaraGenerator.Core.Effects.ProjectEffects.Empty;

    /// <summary>每个具体对象唯一的固定组合预设；样式与效果由统一投影执行。</summary>
    public PresetApplications PresetApplications { get; init; } = PresetApplications.Empty;

    /// <summary>歌词展示定义，独立于源歌词和样式模板。</summary>
    public LyricPresentationDefinition LyricPresentation { get; init; } = LyricPresentationDefinition.Empty;

    /// <summary>
    /// 歌词样式<b>基线</b>：行与元素的覆盖都是在它之上逐项生效。
    /// </summary>
    /// <remarks>
    /// 注意这里存的是样式<b>本身</b>，而不是"预设的名称"：预设库日后可以改，
    /// 但已经定稿的项目外观不能因此跟着变。<see cref="LyricStylePresetId" /> 只作为来源信息保留。
    /// </remarks>
    public LyricStyle LyricStyle { get; init; } = LyricStyle.Default;

    /// <summary>
    /// 基线样式来自哪个预设（<see cref="LyricStylePreset.Id" />）；为 <see langword="null"/> 表示手工样式。
    /// </summary>
    /// <remarks>
    /// 仅用于界面显示"当前预设"与把用户改回预设状态；渲染一律使用 <see cref="LyricStyle" />。
    /// </remarks>
    public string? LyricStylePresetId { get; init; }

    /// <summary>
    /// 工程使用的音频文件路径；没有音频时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内存中通常是<b>绝对路径</b>（打开时由 <c>ProjectStore</c> 把相对路径解析成绝对路径），
    /// 保存时完整发布音频内容地址，写盘只保存受管相对路径，工程目录可以独立搬走。
    /// </para>
    /// <para>
    /// 文件是否存在<b>不在这里判断</b>：领域模型允许"引用了当前打不开的文件"，
    /// 保存时缺失拒绝发布，加载时严格校验实际媒体字节；领域校验器只做语义检查。
    /// </para>
    /// </remarks>
    public string? AudioPath { get; init; }

    /// <summary>固定音频身份；新选择的外部源尚未发布时为空。</summary>
    public MediaFileRevision? AudioMedia { get; init; }

    /// <summary>已接受的完整分析或失败原因；更换音频时清除。</summary>
    public ProjectAudioAnalysis? AudioAnalysis { get; init; }

    /// <summary>
    /// 背景源：铺在 <see cref="Composition.BackgroundColor" /> 之上的一层画面。
    /// </summary>
    /// <remarks>
    /// 默认为"只有背景色"。背景图片的路径规则与 <see cref="AudioPath" /> 相同：
    /// 内存中为绝对路径，<c>ProjectAssetCollector</c> 在保存时将完整媒体
    /// 流式发布到工程包的 <c>data/media/</c> 内容地址。
    /// </remarks>
    public BackgroundSource Background { get; init; } = BackgroundSource.Color;

    /// <summary>创建一个已校验的项目。</summary>
    /// <param name="name">项目名称，不可为空白。</param>
    /// <param name="composition">合成设置，默认为默认值。</param>
    /// <param name="lyrics">歌词文档，默认为空文档。</param>
    /// <param name="schemaVersion">schema 版本，默认使用当前版本。</param>
    /// <param name="lyricStyle">歌词样式基线；为 <see langword="null"/> 时使用默认样式。</param>
    /// <param name="lyricStylePresetId">基线样式来自的预设标识。</param>
    /// <param name="audioPath">音频文件路径。</param>
    /// <param name="background">背景源；为 <see langword="null"/> 时使用"只有背景色"。</param>
    /// <returns>新的项目实例。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空白时抛出。</exception>
    public static Project Create(
        string name,
        Composition? composition = null,
        LyricsDocument? lyrics = null,
        int schemaVersion = CurrentSchemaVersion,
        LyricStyle? lyricStyle = null,
        string? lyricStylePresetId = null,
        string? audioPath = null,
        BackgroundSource? background = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var sourceLyrics = lyrics ?? LyricsDocument.Create();
        return new Project
        {
            SchemaVersion = schemaVersion,
            Name = name,
            Composition = composition ?? new Composition(),
            Lyrics = sourceLyrics,
            LyricPresentation = LyricPresentationDefinition.Empty.WithDefaultPrograms(sourceLyrics),
            LyricStyle = lyricStyle ?? LyricStyle.Default,
            LyricStylePresetId = lyricStylePresetId,
            AudioPath = audioPath,
            Background = background ?? BackgroundSource.Color,
        };
    }
}
