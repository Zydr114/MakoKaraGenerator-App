using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

public sealed partial class PreparedRenderFrame
{
    private static (ImmutableArray<TextMarkTransform>, ImmutableArray<TextMarkStyle>,
        ImmutableArray<TextMarkGeometrySample>, ImmutableDictionary<TextMarkTarget, LyricStyle>, ImmutableDictionary<TextMarkTarget, StyleView>) PrepareMarks(
        ProjectSnapshot snapshot, SceneFrame scene, ImmutableArray<TextMarkTransform> markTransforms,
        ImmutableArray<TextMarkStyle> markStyles, PreparedSourceMaterials? sourceMaterials)
    {
        var markTargets = scene.TextMarks.Select(static mark => new TextMarkTarget(new(mark.NodeId), new(mark.CueId), new(mark.Geometry.BindingId!))).ToHashSet();
        var markGeometry = new Dictionary<TextMarkTarget, TextMarkGeometrySample>();
        foreach (var geometry in scene.MarkGeometries)
            if (geometry is null || geometry.Target is null || !markTargets.Contains(geometry.Target) || !markGeometry.TryAdd(geometry.Target, geometry))
                throw new PreparedFrameException("InvalidPreparedMarkGeometry", "标记几何重复或不属于当前提示装载。");
        if (markTargets.Count != scene.TextMarks.Length || markGeometry.Count != markTargets.Count)
            throw new PreparedFrameException("MissingPreparedMarkGeometry", "当前提示装载缺少唯一的固定字形几何。");
        var transforms = markTransforms.IsDefault ? [] : markTransforms;
        var transformedMarks = new Dictionary<TextMarkTarget, Affine2>();
        foreach (var transform in transforms)
        {
            if (transform is null || transform.Target is null || !transformedMarks.TryAdd(transform.Target, transform.LocalMatrix)
                || !markGeometry.TryGetValue(transform.Target, out var geometry))
                throw new PreparedFrameException("InvalidPreparedMarkTarget", "准备帧标记变换目标重复或不在当前提示装载。");
            if (!transform.LocalMatrix.TryInverse(out _)) throw new PreparedFrameException("InvalidTransform", "标记局部矩阵不可逆。");
            _ = (geometry.Geometry with { BaseWorldMatrix = geometry.Geometry.BaseWorldMatrix.Compose(transform.LocalMatrix) }).WorldInkCorners;
        }
        ImmutableArray<TextMarkGeometrySample> finalGeometry = [.. scene.MarkGeometries.Select(mark => transformedMarks.TryGetValue(mark.Target, out var local)
            ? mark with { Geometry = mark.Geometry with { BaseWorldMatrix = mark.Geometry.BaseWorldMatrix.Compose(local) } } : mark)];
        var markSources = StaticStyleProjection.MarkStyles(snapshot, scene);
        var effectiveMarks = ImmutableArray.CreateBuilder<TextMarkStyle>();
        var resolvedMarks = ImmutableDictionary.CreateBuilder<TextMarkTarget, LyricStyle>();
        var appearances = ImmutableDictionary.CreateBuilder<TextMarkTarget, StyleView>();
        HashSet<TextMarkTarget> seenMarks = [];
        foreach (var output in markStyles.IsDefault ? [] : markStyles)
        {
            if (output is null || output.Target is null || output.Update is null || !seenMarks.Add(output.Target)
                || !markGeometry.ContainsKey(output.Target) || !markSources.TryGetValue(output.Target.NodeId, out var basis))
                throw new PreparedFrameException("InvalidPreparedMarkTarget", "准备帧标记外观目标重复或不在当前提示装载。");
            LyricStyle result;
            try { result = StaticStyleProjection.ApplyDrawingStyle(basis, output.Update, sourceMaterials); }
            catch (EffectOutputException exception) { throw new PreparedFrameException(exception.Code, exception.Message); }
            if (ReferenceEquals(result, basis) && !ContainsPackageMaterial(output.Update)) continue;
            effectiveMarks.Add(output); resolvedMarks.Add(output.Target, result);
            appearances.Add(output.Target, output.Update.Apply(StaticStyleProjection.View(basis, sourceMaterials)));
        }
        return (transforms, effectiveMarks.ToImmutable(), finalGeometry, resolvedMarks.ToImmutable(), appearances.ToImmutable());
    }
}
