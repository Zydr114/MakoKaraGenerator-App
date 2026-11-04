using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>SDK v2 的文字纯求值入口；固定准备结果由宿主持有，任意时刻求值不推进共享状态。</summary>
public interface IPureKaraokeEffect
{
    /// <summary>从一次固定输入建立递归只读准备结果；不得保留阶段句柄或可变外部状态。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "SDK v2 明确提供 C# 作者入口，Declare 为已固定生命周期名称。")]
    PreparedEffect Declare(DeclareContext source);

    /// <summary>只读取固定输入、准备结果和当前时刻，向本次调用的临时输出写入文字属性。</summary>
    void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output);
}

/// <summary>作者可派生的准备记录；派生记录及其全部成员必须满足编译器的递归只读检查。</summary>
/// <remarks>宿主按固定输入身份保留本值。不可在其中保存帧、输出、委托、可变集合或原生资源。</remarks>
public abstract record PreparedEffect
{
    /// <summary>本次固定输入的声明式发射计划；宿主验证后固定，不逐帧修改。</summary>
    public ParticleSchedule Particles { get; init; } = ParticleSchedule.Empty;
    /// <summary>本实例全部文字输出的连续性声明；未证明连续的区段精确求值。</summary>
    public GeometrySamplingPolicy GeometrySampling { get; init; } = GeometrySamplingPolicy.Exact;
    /// <summary>本实例允许写入的具名晚期图元；自动发射器占用独立的同名保留输出。</summary>
    public ImmutableArray<VisualOutputDeclaration> VisualOutputs { get; init; } = [];

    /// <summary>没有准备数据时的唯一默认值。</summary>
    public static PreparedEffect Empty { get; } = new EmptyPreparedEffect();

    private sealed record EmptyPreparedEffect : PreparedEffect;
}

/// <summary>一次固定输入的只读准备上下文；没有历史推进、文件路径、设备或输出句柄。</summary>
public sealed class DeclareContext
{
    /// <summary>固定宿主已经解析的目录、参数、分析、资源、种子和源事件索引。</summary>
    public DeclareContext(ContentCatalog content, SceneCatalog scene, ParameterValues parameters,
        AudioFeatures audio, ResourceCatalog resources, ulong seed, TimelineIndex timeline, Vector2 canvasSize,
        EffectInstanceId instanceId, SourceIndexes? indexes = null)
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(parameters); ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(resources); ArgumentNullException.ThrowIfNull(timeline);
        if (canvasSize.X <= 0 || canvasSize.Y <= 0) throw new ArgumentOutOfRangeException(nameof(canvasSize));
        parameters.ValidateResources(resources);
        Content = content; Scene = scene; Parameters = parameters; Audio = audio; Resources = resources;
        Seed = seed; Timeline = timeline; CanvasSize = canvasSize; InstanceId = instanceId;
        Random = new DeterministicRandom(seed, instanceId);
        Indexes = indexes ?? new(timeline);
        if (!ReferenceEquals(Indexes.Timeline, timeline)) throw new ArgumentException("源索引必须共用固定事件表。", nameof(indexes));
    }

    /// <summary>完整源歌词目录，不按实例输出范围裁剪。</summary>
    public ContentCatalog Content { get; }
    /// <summary>完整真实场景目录。</summary>
    public SceneCatalog Scene { get; }
    /// <summary>固定类型化参数；参数改变需要重新准备。</summary>
    public ParameterValues Parameters { get; }
    /// <summary>固定分析修订和明确的不可用状态。</summary>
    public AudioFeatures Audio { get; }
    /// <summary>固定包修订的逻辑资源目录。</summary>
    public ResourceCatalog Resources { get; }
    /// <summary>固定宿主种子。</summary>
    public ulong Seed { get; }
    /// <summary>固定实例身份，参与计数式随机分流。</summary>
    public EffectInstanceId InstanceId { get; }
    /// <summary>计数式纯随机服务，调用顺序不消耗游标。</summary>
    public DeterministicRandom Random { get; }
    /// <summary>包含未来源事件的固定索引；查询不重放作者回调。</summary>
    public TimelineIndex Timeline { get; }
    /// <summary>同一固定输入和 subscription 的时间、装载、基础布局与播放头索引。</summary>
    public SourceIndexes Indexes { get; }
    /// <summary>画布逻辑像素宽高。</summary>
    public Vector2 CanvasSize { get; }
}
