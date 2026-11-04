using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private static bool HasOnlyRubyWarp(TokenGeometrySample? geometry)
        => geometry is not null && geometry.Clusters.Any(static cluster => cluster.Target.RubyId is not null
            && HasEffectiveWarp(cluster.Geometry.Mapping))
            && geometry.Clusters.Where(static cluster => cluster.Target.RubyId is null)
                .All(static cluster => cluster.Geometry.Mapping is { } mapping && WithoutWorld(mapping).IsIdentity);

    private static IEnumerable<ImmutableArray<TextClusterGeometrySample>> MappingRuns(IEnumerable<TextClusterGeometrySample> clusters)
    {
        var run = ImmutableArray.CreateBuilder<TextClusterGeometrySample>();
        foreach (var cluster in clusters.OrderBy(static item => item.Target.UnitIndex))
        {
            if (run.Count > 0 && run[0].Geometry.Mapping != cluster.Geometry.Mapping)
            {
                yield return run.ToImmutable();
                run.Clear();
            }
            run.Add(cluster);
        }
        if (run.Count > 0) yield return run.ToImmutable();
    }

    // 此入口的画布只含原有 world 矩阵；叶到族的所有局部变换来自同一准备映射。
    // 相同映射的单元一起光栅化，演唱裁切、阴影、辉光均发生在源坐标内。
    private void DrawWarpedPartitions(SKCanvas canvas, RenderRequest request, SceneLineSample scene,
        ShapedLine shaped, TextLayoutOptions options, IReadOnlyList<RubyLayout> ruby, LyricFrameSample tokenSample,
        int tokenIndex, LyricStyle familyStyle, LyricStyle lineStyle, Composition composition,
        float origin, float baseline, bool antialias, CapturePaint? capture, TokenGeometrySample geometry,
        ImageSurfaceBudgetFrame? resources, bool rubyOnly)
    {
        var prepared = request.PreparedFrame!;
        var style = prepared.BaseStyles.Any(item => item.Target == geometry.BaseOnly)
            ? prepared.GetBaseDrawingStyle(geometry.BaseOnly) : familyStyle;
        if (!rubyOnly) foreach (var group in MappingRuns(geometry.Clusters.Where(static cluster => cluster.Target.RubyId is null)))
        {
            var indices = group.Select(static cluster => cluster.Target.UnitIndex).ToImmutableArray();
            var bounds = PreparedRenderFrame.WarpDecoratedBounds(TextSourceBounds(shaped, origin, baseline, indices), style);
            DrawWarpedTextLayer(canvas, request, new TextTarget.Cluster(group.First().Target), WithoutWorld(group[0].Geometry.Mapping!),
                bounds, resources ?? capture?.Budget, capture, (source, sourceCapture) =>
                {
                    DrawLineGlow(source, shaped, options, origin, baseline, style, antialias, indices, sourceCapture);
                    using var regions = BuildSungRegions(tokenSample, shaped, origin, source.LocalClipBounds);
                    bool hasSung = !regions.IsEmpty;
                    Draw(false); if (hasSung) Draw(true);
                    void Draw(bool sung)
                    {
                        source.Save();
                        try
                        {
                            if (hasSung) ClipSungRegions(source, regions, sung);
                            DrawStyledLine(source, shaped, options, origin, baseline,
                                sung ? style.SungColor : style.UnsungColor,
                                sung ? style.SungFill.Gradient : style.UnsungFill.Gradient,
                                style, composition, antialias, indices, sourceCapture, sung);
                        }
                        finally { source.Restore(); }
                    }
                });
        }
        foreach (var annotation in ruby.Where(item => item.TokenIndex == tokenIndex))
        foreach (var group in MappingRuns(geometry.Clusters.Where(cluster => cluster.Target.RubyId?.Value == annotation.Id)))
        {
            var indices = group.Select(static cluster => cluster.Target.UnitIndex).ToImmutableArray();
            var bounds = PreparedRenderFrame.WarpDecoratedBounds(TextSourceBounds(annotation.Shaped,
                origin + annotation.X, baseline + annotation.Baseline, indices), familyStyle);
            DrawWarpedTextLayer(canvas, request, new TextTarget.Cluster(group.First().Target), WithoutWorld(group[0].Geometry.Mapping!),
                bounds, resources ?? capture?.Budget, capture, (source, sourceCapture) =>
                    DrawRuby(source, [annotation], scene.Frame, scene.Frame.Line!, lineStyle, composition, origin, baseline,
                        antialias, familyStyle, sourceCapture, indices));
        }
    }
}
