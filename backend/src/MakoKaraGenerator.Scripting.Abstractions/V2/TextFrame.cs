using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>真实行装载的展示身份；不替代元素的演唱／Missing 状态。</summary>
public enum TextLinePhase
{
    /// <summary>仍由源场景提供当前布局的正常装载。</summary>
    Active,
    /// <summary>隐藏后的副本；几何、父链与源基线在隐藏左极限冻结。</summary>
    ExitReplica,
}

/// <summary>一次真实装载的只读相位；退场副本沿用原 Binding，不制造源演唱 occurrence。</summary>
public sealed record TextLineLoad
{
    /// <summary>建立正常装载或宿主已经冻结几何的退场副本；退场时长上限为三秒。</summary>
    public TextLineLoad(FrameLine line, TextLinePhase phase = TextLinePhase.Active,
        long? hideTimeUs = null, long exitDurationUs = 0, double exitProgress = 0)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.LineId.IsValid || !line.BindingId.IsValid || !Enum.IsDefined(phase)
            || exitDurationUs is < 0 or > 3_000_000 || !double.IsFinite(exitProgress) || exitProgress is < 0 or > 1
            || phase == TextLinePhase.Active && (hideTimeUs is not null || exitProgress != 0)
            || phase == TextLinePhase.ExitReplica && (hideTimeUs is null or < 0 || exitDurationUs == 0 && exitProgress != 1))
            throw new ArgumentException("行装载相位、隐藏边界或退场进度无效。", nameof(line));
        Line = line; Phase = phase; HideTimeUs = hideTimeUs; ExitDurationUs = exitDurationUs; ExitProgress = exitProgress;
    }

    /// <summary>真实源行、原 Binding、父槽位与片段身份。</summary>
    public FrameLine Line { get; }
    /// <summary>真实装载目标。</summary>
    public LineTarget Target => Line.Target;
    /// <summary>正常装载或隐藏后的冻结副本。</summary>
    public TextLinePhase Phase { get; }
    /// <summary>副本的源隐藏时刻；正常装载为空。</summary>
    public long? HideTimeUs { get; }
    /// <summary>本实例声明的退场时长；副本可因其他实例需要而继续存在。</summary>
    public long ExitDurationUs { get; }
    /// <summary>本实例夹到 [0,1] 的退场进度；无声明实例在副本阶段为 1。</summary>
    public double ExitProgress { get; }
}

/// <summary>宿主传递给纯文字帧内部的只读输入封包。</summary>
internal sealed record TextFrameHostInputs(
    long TimeUs,
    BaselineStyles Baseline,
    FramePresentations Presentations,
    ImmutableArray<TokenGeometrySample> Geometry,
    ImmutableArray<LineGeometrySample> LineGeometry,
    ImmutableArray<TextMarkGeometrySample> MarkGeometry,
    ImmutableArray<GroupGeometrySample> GroupGeometry,
    EffectBinding Binding,
    OutputMode OutputMode,
    CancellationToken CancellationToken = default);

/// <summary>SDK v2 文字阶段的只读帧；不公开 v1 历史、状态或诊断。</summary>
public readonly struct TextFrame
{
    /// <summary>投影宿主已经采样的安全只读输入；退场时由宿主显式提供冻结后的装载和几何。</summary>
    internal TextFrame(DeclareContext source, TextFrameHostInputs frame, ImmutableArray<TextLineLoad> lineLoads = default)
        : this(source, frame, lineLoads, UnboundMedia(source, frame)) { }

    internal TextFrame(DeclareContext source, TextFrameHostInputs frame, ImmutableArray<TextLineLoad> lineLoads, MediaFeatures media)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(media);
        media.ValidateCatalog(source.Media, source.CanvasSize);
        if (frame.TimeUs < 0) throw new ArgumentOutOfRangeException(nameof(frame));
        Source = source; Media = media; TimeUs = frame.TimeUs; Baseline = frame.Baseline;
        Presentations = frame.Presentations; Geometry = frame.Geometry; LineGeometry = frame.LineGeometry;
        MarkGeometry = frame.MarkGeometry; GroupGeometry = frame.GroupGeometry; Binding = frame.Binding; OutputMode = frame.OutputMode;
        var presentations = frame.Presentations;
        LineLoads = lineLoads.IsDefault ? [.. presentations.LinesInBinding.Select(static line => new TextLineLoad(line))] : lineLoads;
        if (LineLoads.Any(static load => load is null) || LineLoads.Select(static load => load.Target).Distinct().Count() != LineLoads.Length
            || LineLoads.Any(load => !presentations.LinesInBinding.Any(line => line.Target == load.Target)))
            throw new ArgumentException("行相位需要唯一且属于当前帧的真实装载。", nameof(lineLoads));
    }

    /// <summary>本次准备所属固定输入。</summary>
    public DeclareContext Source { get; }
    /// <summary>目标非负整数微秒时刻。</summary>
    public long TimeUs { get; }
    /// <summary>固定参数。</summary>
    public ParameterValues Parameters => Source.Parameters;
    /// <summary>固定音频分析；Missing 状态不伪造静音。</summary>
    public AudioFeatures Audio => Source.Audio;
    /// <summary>仅本次文字调用有效的媒体采样句柄；使用明确基础几何矩形。</summary>
    public MediaFeatures Media { get; }
    /// <summary>固定资源目录。</summary>
    public ResourceCatalog Resources => Source.Resources;
    /// <summary>计数式随机服务。</summary>
    public DeterministicRandom Random => Source.Random;
    /// <summary>固定源事件索引。</summary>
    public TimelineIndex Timeline => Source.Timeline;
    /// <summary>固定输入的源装载、基础布局与基础播放头查询，不读取任何实例输出。</summary>
    public SourceIndexes Indexes => Source.Indexes;
    /// <summary>逻辑画布宽高。</summary>
    public Vector2 CanvasSize => Source.CanvasSize;
    /// <summary>工程已确定的成片时长；未设置时为 null。</summary>
    public long? ProjectDurationUs => Source.ProjectDurationUs;
    /// <summary>全实例共享的静态继承基线，退场使用宿主冻结源基线；不含任何作者输出。</summary>
    public BaselineStyles Baseline { get; }
    /// <summary>真实当前目标；Token 的 Performance 保留 nullable 进度、LocalTime、Current、Sung 与 ProgressSource。</summary>
    public FramePresentations Presentations { get; }
    /// <summary>真实行装载及其独立退场相位。</summary>
    public ImmutableArray<TextLineLoad> LineLoads { get; }
    /// <summary>基础字族只读几何与整形可独立状态；字簇不按 UTF-16 拆分。</summary>
    public ImmutableArray<TokenGeometrySample> Geometry { get; }
    /// <summary>从同一整行整形结果取得的真实基础字／Ruby 字簇；共享单元按目标身份去重。</summary>
    public ImmutableArray<TextClusterGeometrySample> Clusters => [.. Geometry.SelectMany(static item => item.Clusters).DistinctBy(static item => item.Target)];
    /// <summary>当前或冻结副本的整行基础几何。</summary>
    public ImmutableArray<LineGeometrySample> LineGeometry { get; }
    /// <summary>真实文字标记的基础几何。</summary>
    public ImmutableArray<TextMarkGeometrySample> MarkGeometry { get; }
    /// <summary>真实组的只读几何目录；读取不授予组写权限。</summary>
    public ImmutableArray<GroupGeometrySample> GroupGeometry { get; }
    /// <summary>实例输出绑定；不代表源事件范围。</summary>
    public EffectBinding Binding { get; }
    /// <summary>当前输出的背景语义。</summary>
    public OutputMode OutputMode { get; }

    private static MediaFeatures UnboundMedia(DeclareContext source, TextFrameHostInputs frame)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(frame);
        return new(source.Media, source.CanvasSize, token: frame.CancellationToken);
    }
}
