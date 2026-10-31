using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>宿主固定的图元作者来源；不把编译身份交给作者 writer。</summary>
public sealed record PreparedOverlayOwner(EffectInstanceId InstanceId, string ArtifactHash, EffectPackageIdentity? PackageIdentity)
{
    /// <summary>创建保留真实包及控制器身份的后端失败。</summary>
    public EffectFailureException Failure(long timeUs, string code, Exception error)
        => new(InstanceId, ArtifactHash, "RenderOverlays", timeUs, code, error, PackageIdentity);
}
