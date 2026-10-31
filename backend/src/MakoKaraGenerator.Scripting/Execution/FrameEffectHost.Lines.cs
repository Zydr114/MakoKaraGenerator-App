using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private static void ComposeLines(ImmutableArray<LineTransform> output, Dictionary<LineTarget, Affine2> combined, SceneFrame scene)
    {
        foreach (var transform in output)
        {
            var local = combined.GetValueOrDefault(transform.Target, Affine2.Identity).Compose(transform.LocalMatrix);
            if (!local.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "跨实例整行矩阵不可逆。");
            var basis = scene.LineGeometries.Single(item => item.Target == transform.Target).BaseGeometry;
            var world = basis.BaseWorldMatrix.Compose(local);
            if (!world.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "整行最终世界矩阵不可逆。");
            _ = (basis with { BaseWorldMatrix = world }).WorldInkCorners;
            combined[transform.Target] = local;
        }
    }
}
