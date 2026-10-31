using System.Collections.Immutable;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private static ImmutableArray<Fx.GroupGeometrySample> MeasureGroupInk(SceneFrame frame)
        => GroupInkProjection.Create(frame);
}
