using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>主画布实际图旁路的实例、修订和输入身份。</summary>
/// <param name="InstanceId">真实工程实例。</param><param name="ArtifactHash">作者编译代次。</param>
/// <param name="Graph">固定图导出。</param><param name="InputId">触发跳过的背景输入。</param><param name="Code">稳定原因。</param>
public sealed record RenderImageEffectDiagnostic(EffectInstanceId InstanceId, string ArtifactHash, GraphHandle Graph, string InputId, string Code);
