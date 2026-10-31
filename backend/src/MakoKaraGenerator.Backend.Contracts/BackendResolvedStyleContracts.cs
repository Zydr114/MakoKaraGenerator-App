using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>实际来源与片段/容器推导的静态上下文；无装载时不猜测动态槽位。</summary>
public sealed record ResolvedStyleContextDto(string BranchId, string LineId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TokenId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ClipId,
    string ContainerId, string NodeId, string TextNodeId, string[] NodePath, string ContextKind);
/// <summary>Core逐属性的真实持久来源路径。</summary>
public sealed record ResolvedStylePropertySourceDto(string Property, string SourcePath);
/// <summary>固定预设及真实容器/槽位路径下的静态样式；不执行动画、脚本或像素采样。</summary>
public sealed record ProjectResolvedStyleResponse(string ProjectSessionId, string ProjectRevision,
    ResolvedStyleContextDto Context, CanonicalStyleDto Style, ResolvedStylePropertySourceDto[] Sources,
    string Resolution = "staticPresetHierarchy", bool FinalPixelAppearance = false, string ProtocolVersion = BackendProtocol.Version);
