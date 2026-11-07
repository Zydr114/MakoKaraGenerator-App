namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>
/// 一帧的只读检查投影（调试叠加层与成本），与帧像素出自同一次准备。
/// 坐标为工程画布坐标；多边形按 x0,y0,x1,y1… 平铺。只在帧请求 inspect=true 时生成，寿命与帧相同。
/// </summary>
public sealed record PreviewFrameInspectionResponse(
    string FrameId,
    string PreviewId,
    string PreviewGeneration,
    string SourceRevision,
    string TimeUs,
    int Width,
    int Height,
    string RenderBackend,
    PreviewInspectionDto Inspection,
    PreviewInspectionHostCostDto Host,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>Worker 生成的检查投影正文；Prepared=false 表示本帧没有效果准备结果。</summary>
public sealed record PreviewInspectionDto(
    PreviewInspectionCanvasDto Canvas,
    bool Prepared,
    PreviewInspectionTokenDto[] Tokens,
    PreviewInspectionLineDto[] Lines,
    PreviewInspectionCursorDto[] Cursors,
    PreviewInspectionParticlesDto[] Particles,
    PreviewInspectionEffectDto[] Effects,
    PreviewInspectionCostDto Cost,
    PreviewInspectionTruncationDto Truncated);

/// <summary>工程画布尺寸。</summary>
public sealed record PreviewInspectionCanvasDto(int Width, int Height);

/// <summary>基础字族的最终墨迹、可否独立变形、字簇和注音。</summary>
public sealed record PreviewInspectionTokenDto(
    string TokenId,
    string BindingId,
    string LineId,
    bool Independent,
    string? DiagnosticCode,
    double[]? Ink,
    PreviewInspectionClusterDto[] Clusters,
    PreviewInspectionRubyDto[] Ruby,
    int OmittedClusters);

/// <summary>真实整形单元；跨元素单元 Independent=false。</summary>
public sealed record PreviewInspectionClusterDto(
    int UnitIndex,
    string[] OwnerTokenIds,
    bool Independent,
    string? DiagnosticCode,
    double[]? Ink);

/// <summary>一段注音的墨迹；归属为所在的基础字。</summary>
public sealed record PreviewInspectionRubyDto(string RubyId, double[]? Ink);

/// <summary>行装载的最终 Advance 外框。</summary>
public sealed record PreviewInspectionLineDto(string LineId, string BindingId, double[]? Bounds);

/// <summary>演唱光标：当前位置、进度路径起止和进度来源（token、rubySegment 或 line）。</summary>
public sealed record PreviewInspectionCursorDto(
    string LineId,
    string BindingId,
    string? TokenId,
    double Progress,
    string ProgressSource,
    double[] Point,
    double[] Start,
    double[] End);

/// <summary>一个实例本帧出生窗内的粒子数量与出生锚点。</summary>
public sealed record PreviewInspectionParticlesDto(
    string InstanceId,
    int Births,
    int Alive,
    int TrailSamples,
    double[] BirthPoints,
    int OmittedBirthPoints);

/// <summary>效果实例的来源、绑定种类与绑定的源行；ScriptId 对应成本中的脚本条目。</summary>
public sealed record PreviewInspectionEffectDto(
    string InstanceId,
    string? PackageId,
    string? PackageRevision,
    string? ExportId,
    int SdkMajor,
    string BindingKind,
    string[] LineIds,
    string? ScriptId,
    int OmittedLines);

/// <summary>Worker 内单帧成本。阶段为包含式墙钟毫秒，不能相加；分配量为全进程增量。</summary>
public sealed record PreviewInspectionCostDto(
    double WallMs,
    double PrepareMs,
    double ScriptMs,
    double GraphMs,
    double MaterialMs,
    double ParticleMs,
    double RasterMs,
    double ValidationMs,
    double ReadbackMs,
    long ScriptCalls,
    long ReadbackBytes,
    long AllocatedBytes,
    int Gc0,
    int Gc1,
    int Gc2,
    PreviewInspectionScriptDto[] Scripts,
    long ScriptOverflow,
    PreviewInspectionAnchorsDto Anchors,
    double? SessionOpenMs);

/// <summary>按实例与阶段聚合的脚本耗时。</summary>
public sealed record PreviewInspectionScriptDto(string ScriptId, string Stage, long Calls, double WallMs, double MaxMs);

/// <summary>锚点补算工作量与 memo 命中。</summary>
public sealed record PreviewInspectionAnchorsDto(int UniqueTimes, int TextCalls, int FutureEndpoints, int MemoHits, int MemoMisses);

/// <summary>因预算省略的条目数。</summary>
public sealed record PreviewInspectionTruncationDto(int Tokens, int Lines, int Cursors, int Particles, int Effects);

/// <summary>Host 侧成本：Worker 渲染往返与 PNG 编码。</summary>
public sealed record PreviewInspectionHostCostDto(double RenderRoundTripMs, double EncodeMs, long PngBytes);
