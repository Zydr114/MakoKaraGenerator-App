namespace MakoKaraGenerator.Core.Diagnostics;

/// <summary>
/// 诊断项标识。
/// </summary>
/// <remarks>
/// 这些标识是<b>稳定契约</b>：测试、界面文案与导出报告都会引用它们，
/// 因此已使用的成员不得改名或改变含义；需要新语义时新增成员。
/// </remarks>
public enum DiagnosticCode
{
    // ---------- 持久化与结构（由读取层产生）----------

    /// <summary>JSON 语法错误，无法解析。</summary>
    MalformedJson = 0,

    /// <summary>schema 版本不受当前程序支持（来自更新的版本，或数值非法）。</summary>
    UnsupportedSchemaVersion = 1,

    /// <summary>项目目录中缺少 <c>project.json</c>。</summary>
    MissingProjectFile = 2,

    /// <summary>项目入口指向的歌词文件缺失。</summary>
    MissingLyricsFile = 3,

    /// <summary>缺少项目名称。</summary>
    MissingProjectName = 4,

    /// <summary>帧率定义缺失或不合法（分子/分母必须为正）。</summary>
    InvalidFrameRateDefinition = 5,

    /// <summary>背景色文本无法解析。</summary>
    InvalidBackgroundColor = 6,

    /// <summary>时间区间不完整：<c>startUs</c> 与 <c>endUs</c> 必须同时出现。</summary>
    IncompleteTimeRange = 7,

    /// <summary>时间状态文本不是已知的 <c>timingState</c> 取值。</summary>
    UnknownTimingState = 8,

    /// <summary>有时间数据但没有 <c>timingState</c>，状态被保守地当作"导入"。</summary>
    MissingTimingState = 9,

    /// <summary>缺少标识（行或元素）。</summary>
    MissingIdentifier = 10,

    /// <summary>缺少文本字段（行或元素）。</summary>
    MissingText = 11,

    /// <summary>缺少 composition（画布与时间基准）对象。</summary>
    MissingComposition = 12,

    // ---------- 语义（由校验器产生）----------

    /// <summary>标识重复。</summary>
    DuplicateIdentifier = 20,

    /// <summary>时间区间不满足约束（起点非负、终点大于起点）。</summary>
    InvalidTimeRange = 21,

    /// <summary>时间缺失。</summary>
    MissingTime = 22,

    /// <summary>时间数据与时间状态不相符。</summary>
    TimeStatusMismatch = 23,

    /// <summary>元素时间越出所属行的时间范围。</summary>
    ElementOutsideLine = 24,

    /// <summary>相邻元素的时间区间重叠。</summary>
    OverlappingElements = 25,

    /// <summary>歌词行未按起始时间排序。</summary>
    LinesOutOfOrder = 26,

    /// <summary>元素文本拼接结果与行文本不一致。</summary>
    TokenTextMismatch = 27,

    /// <summary>文本为空（行或元素）。</summary>
    EmptyText = 28,

    /// <summary>歌词文档没有任何行。</summary>
    EmptyLyrics = 29,

    /// <summary>歌词行尚未拆分为文本元素。</summary>
    UntokenizedLine = 30,

    /// <summary>仍有未被用户确认的时间（缺失、仅导入或仅估算）。</summary>
    UnconfirmedTiming = 31,

    /// <summary>画布尺寸非法。</summary>
    InvalidCanvasSize = 32,

    /// <summary>输出时长非法（已给出但不是正数）。</summary>
    InvalidOutputDuration = 33,

    /// <summary>输出时长未确定。</summary>
    MissingOutputDuration = 34,

    /// <summary>样式值非法（字号非正、纵向位置越界或颜色无法解析）。</summary>
    InvalidStyleValue = 35,

    /// <summary>元素级样式覆盖设置了当前渲染不支持的属性（只有颜色是元素级可用的）。</summary>
    UnsupportedTokenStyleOverride = 36,

    /// <summary>旧版本项目文件已经过迁移。</summary>
    ProjectSchemaMigrated = 37,

    /// <summary>媒体路径是空白文本。</summary>
    InvalidMediaPath = 38,

    /// <summary>背景图的种类或路径写法无法识别（不认识的种类、图片却没有路径）。</summary>
    InvalidBackgroundSource = 39,

    /// <summary>背景图片找不到，或无法复制进工程包。</summary>
    MissingBackgroundAsset = 40,

    /// <summary>背景图片已复制进工程包的 <c>assets/</c>。</summary>
    BackgroundAssetCopied = 41,

    /// <summary>进入动画的取值不合法（时长、起始不透明度、缩放或纵向偏移越界）。</summary>
    InvalidLineAnimation = 42,

    /// <summary>工程的程序版本与当前程序不一致，或未标注有效版本。</summary>
    ApplicationVersionMismatch = 43,

    /// <summary>独立行位置的坐标、锚点、角度或缩放无效。</summary>
    InvalidLinePlacement = 44,

    /// <summary>歌词分支角色、时间、布局或动画配置无效。</summary>
    InvalidLyricBranch = 45,

    /// <summary>关联的主歌词行或基础字已经不存在。</summary>
    MissingLyricAssociation = 46,

    /// <summary>注音参数无效，或者一个基础字有多个活动注音。</summary>
    InvalidRubyAnnotation = 47,

    /// <summary>通用容器树或显示绑定无效。</summary>
    InvalidDisplayProgram = 48,

    /// <summary>独占片段与其他演唱区间冲突。</summary>
    PresentationTimeConflict = 49,

    /// <summary>歌词修订引用缺失、不符合规范内容地址或经过链接。</summary>
    InvalidLyricsReference = 50,

    /// <summary>歌词修订的实际内容与路径中的 SHA-256 不符。</summary>
    LyricsContentMismatch = 51,

    /// <summary>工程效果结构或包字节闭包缺失、损坏或身份不符。</summary>
    InvalidEffectData = 52,

    /// <summary>持久效果实例的导出、参数或源／场景绑定失效。</summary>
    InvalidEffectInstance = 53,

    /// <summary>受管媒体描述、路径、hash、长度或字节闭包无效。</summary>
    InvalidMediaResource = 54,

    /// <summary>分析状态、内容地址、源身份或实际分析值无效。</summary>
    InvalidAudioAnalysis = 55,

    /// <summary>音频已发布到工程独立内容地址。</summary>
    AudioAssetCopied = 56,

    /// <summary>独立注音片段时间超出其基础元素或行；保留时间并遵守行显示窗口。</summary>
    RubySegmentOutsideParent = 57,
}
