using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>在已有歌词容器上明确切换内部布局；旧节点始终保留。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetOrchestrationLayoutKindCommandDto(string? ContainerId, string? Kind,
    string[]? SlotIds, int? NewSlotCount) : ProjectCommandDto("orchestration.setLayoutKind");

/// <summary>精确工程修订下的本句局部展示；不是最终帧几何。</summary>
public sealed record ProjectLinePresentationResponse(string ProjectSessionId, string ProjectRevision,
    string BranchId, string LineId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectLineLocalTransformDto? LocalTransform,
    string ProtocolVersion = BackendProtocol.Version);
