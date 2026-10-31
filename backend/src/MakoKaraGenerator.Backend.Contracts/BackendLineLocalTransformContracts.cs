using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>本句局部微调；偏移按当前父参考宽高的比例，角度为度，缩放为正倍数。</summary>
public sealed record ProjectLineLocalTransformDto(double OffsetXRatio, double OffsetYRatio,
    double RotationDegrees, double Scale);

/// <summary>设置本句局部微调；显式 null 恢复默认，不修改源歌词时间。</summary>
public sealed record SetLineLocalTransformCommandDto(string? BranchId, string? LineId,
    ProjectLineLocalTransformDto? Transform) : ProjectCommandDto("orchestration.setLineLocalTransform");

/// <summary>本次本句局部微调命令的稳定目标和 canonical 结果。</summary>
public sealed record LineLocalTransformCommandResultDto(string BranchId, string LineId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectLineLocalTransformDto? Transform, bool Changed);

/// <summary>精确工程修订下的单句局部微调；null 表示未覆写，不表示最终帧几何。</summary>
public sealed record ProjectLineLocalTransformResponse(string ProjectSessionId, string ProjectRevision,
    string BranchId, string LineId, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectLineLocalTransformDto? Transform,
    string ProtocolVersion = BackendProtocol.Version);
