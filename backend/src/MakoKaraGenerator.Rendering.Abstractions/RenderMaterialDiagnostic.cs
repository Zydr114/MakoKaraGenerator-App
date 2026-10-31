using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>透明输出实际跳过的材质身份，回退为自身包基础外观。</summary>
/// <param name="Owner">真实源对象或脚本写入者。</param>
/// <param name="Material">真实材质句柄。</param><param name="InputId">依赖背景的输入。</param><param name="Code">原因。</param>
public sealed record RenderMaterialDiagnostic(PreparedMaterialOwner Owner, MaterialHandle Material, string InputId, string Code)
{
    /// <summary>脚本来源诊断，保留真实实例及编译身份。</summary>
    public RenderMaterialDiagnostic(EffectInstanceId instanceId, string artifactHash, MaterialHandle material, string inputId, string code)
        : this(new PreparedMaterialOwner.Script(instanceId, artifactHash), material, inputId, code) { }
    /// <summary>真实脚本实例；源样式为null。</summary>
    public EffectInstanceId? InstanceId => (Owner as PreparedMaterialOwner.Script)?.InstanceId;
    /// <summary>真实脚本代次；源样式为null。</summary>
    public string? ArtifactHash => (Owner as PreparedMaterialOwner.Script)?.ArtifactHash;
    /// <summary>供导出提示使用的真实来源描述。</summary>
    public string OwnerDescription => Owner switch
    {
        PreparedMaterialOwner.Script script => "效果 " + script.InstanceId.Value,
        PreparedMaterialOwner.Source source => "源样式 " + source.Reference.Path,
        _ => throw new PreparedFrameException("InvalidMaterialOwner", "材质诊断缺少真实来源。"),
    };
}
