using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    // Keep composition membership on the existing Family object. Capture identity alone becomes Base.
    internal sealed record ObjectMaterialBinding(PreparedMaterialOwner Owner, ImageEffectTarget Target,
        SceneCaptureTarget CaptureTarget, PreparedMaterialSlot Slot, MaterialValue.PackageMaterial Material)
    {
        internal TextTarget? TextTarget => CaptureTarget is SceneCaptureTarget.Base basic ? new TextTarget.Base(basic.Target) : null;
        internal static ObjectMaterialBinding From(PreparedMaterialBinding binding)
            => new(binding.Owner, binding.Target, SkiaRenderPipeline.CaptureTarget(binding.Target), binding.Slot, binding.Material);
        internal static ObjectMaterialBinding From(PreparedBaseMaterialBinding binding)
            => new(binding.Owner, new ImageEffectTarget.Token(new(binding.Target.TokenId, binding.Target.BindingId)),
                new SceneCaptureTarget.Base(binding.Target), binding.Slot, binding.Material);
    }
    private static Exception MaterialTargetError(Exception error, TextTarget? target, string code)
    {
        if (target is null) return error;
        var marked = new PreparedFrameException(code, error.Message) { TextTarget = target };
        marked.Data["OriginalFailure"] = error; return marked;
    }
    private static Exception SceneOwnerFailure(SceneEffectOwner owner, string stage, long timeUs, string code, Exception error)
        => owner.Origin.Failure(stage, timeUs, code, MaterialTargetError(error, owner.TextTarget, code));
}
