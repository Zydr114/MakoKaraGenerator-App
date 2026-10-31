using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>材质真实来源；源样式没有脚本实例或编译代次。</summary>
public abstract record PreparedMaterialOwner
{
    private protected PreparedMaterialOwner() { }
    /// <summary>原始存储对象及材质部位。</summary>
    public sealed record Source(SourceMaterialReference Reference, PreparedMaterialSlot Slot) : PreparedMaterialOwner;
    /// <summary>最后一次材质写入的真实脚本。</summary>
    public sealed record Script(EffectInstanceId InstanceId, string ArtifactHash) : PreparedMaterialOwner
    {
        /// <summary>最后一次材质写入的控制器包来源，独立源码接缝为 null。</summary>
        public EffectPackageIdentity? PackageIdentity { get; init; }
    }
    /// <summary>真实来源是否完整。</summary>
    public bool IsValid => this switch
    {
        Source source => source.Reference is { Material: not null } && !string.IsNullOrWhiteSpace(source.Reference.Path)
            && Enum.IsDefined(source.Slot),
        Script script => script.InstanceId.IsValid && !string.IsNullOrWhiteSpace(script.ArtifactHash),
        _ => false,
    };
    /// <summary>建立保留真实来源的失败，不能用假实例包装源错误。</summary>
    public Exception Failure(string stage, long timeUs, string code, Exception inner) => this switch
    {
        Script script => new EffectFailureException(script.InstanceId, script.ArtifactHash, stage, timeUs, code, inner, script.PackageIdentity),
        Source source => new SourceMaterialFailureException(source, stage, timeUs, code, inner),
        _ => new PreparedFrameException("InvalidMaterialOwner", "材质来源无效。"),
    };
}

/// <summary>持久源材质的实际失败；必须阻止导出，不能用普通背景帧替代。</summary>
public sealed class SourceMaterialFailureException : Exception
{
    /// <summary>建立带真实源对象、阶段及时刻的错误。</summary>
    public SourceMaterialFailureException(PreparedMaterialOwner.Source owner, string stage, long timeUs, string code, Exception inner)
        : base($"源材质 {owner.Reference.Path}（{owner.Reference.Material.PackageId}/{owner.Reference.Material.ExportId}）在 {stage} 失败：{inner.Message}", inner)
    { Owner = owner; Stage = stage; TimeUs = timeUs; Code = code; }
    /// <summary>真实持久来源。</summary>
    public PreparedMaterialOwner.Source Owner { get; }
    /// <summary>失败阶段。</summary>
    public string Stage { get; }
    /// <summary>实际微秒时刻。</summary>
    public long TimeUs { get; }
    /// <summary>稳定错误代码。</summary>
    public string Code { get; }
}
