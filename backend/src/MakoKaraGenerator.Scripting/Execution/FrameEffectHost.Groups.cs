using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private static void ComposeGroups(ImmutableArray<GroupTransform> output, Dictionary<GroupTarget, Affine2> combined, SceneFrame scene, bool appendBefore = false)
    {
        foreach (var transform in output)
        {
            var previous = combined.GetValueOrDefault(transform.Target, Affine2.Identity);
            var local = appendBefore ? transform.LocalMatrix.Compose(previous) : previous.Compose(transform.LocalMatrix);
            if (!local.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "跨实例组矩阵不可逆。");
            var basis = scene.GroupGeometries.Single(item => item.Target == transform.Target);
            var world = basis.BaseWorldMatrix.Compose(local);
            if (!world.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "组最终世界矩阵不可逆。");
            var box = basis.ReferenceBounds;
            foreach (var corner in new Point2[] { new(box.Left, box.Top), new(box.Right, box.Top), new(box.Right, box.Bottom), new(box.Left, box.Bottom) })
                _ = world.Map(corner);
            if (local == Affine2.Identity) combined.Remove(transform.Target); else combined[transform.Target] = local;
        }
    }
}
