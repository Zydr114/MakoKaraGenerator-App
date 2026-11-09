using System.Collections.Immutable;
using System.Globalization;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using Fx = MakoKaraGenerator.Scripting;
using V2 = MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private ImmutableArray<Fx.TextMarkGeometrySample> MeasureMarkGeometries(SceneFrame frame)
        => [.. frame.TextMarks.Select(mark =>
        {
            var style = mark.Appearance.ToLyricStyle(); ShapedLine shaped = GetLayout(mark.Text, LayoutOptionsFor(style));
            var matrix = mark.Geometry.WorldMatrix!.Value;
            Fx.Affine2 world = new(matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty);
            Fx.TextMarkTarget target = new(new(mark.NodeId), new(mark.CueId), new(mark.Geometry.BindingId!));
            var geometry = MeasureUnits(shaped, [.. Enumerable.Range(0, shaped.DrawUnits.Length)], mark.Geometry.OriginX,
                mark.Geometry.BaselineY, world, mark.Geometry.BindingId!, "mark/" + mark.NodeId, true);
            return new Fx.TextMarkGeometrySample(target, geometry);
        })];

    private ImmutableArray<Fx.LineGeometrySample> MeasureLineGeometries(ProjectSnapshot snapshot, SceneFrame frame)
    {
        return [.. frame.Lines.Select(sample =>
        {
            var line = sample.Frame.Line!;
            var style = LineBaseStyle(snapshot.LyricStyle, line, sample.Branch);
            ShapedLine shaped = GetLineLayout(line, style).Shaped;
            var geometry = sample.Geometry!; SceneMatrix matrix = geometry.WorldMatrix!.Value;
            Fx.Affine2 world = new(matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty);
            var basis = MeasureUnits(shaped, [.. Enumerable.Range(0, shaped.DrawUnits.Length)], geometry.OriginX,
                geometry.BaselineY, world, sample.BindingId!, "base", true);
            return new Fx.LineGeometrySample { BranchId = new(sample.Branch.Id), Target = new(new(line.Id), new(sample.BindingId!)),
                BaseGeometry = basis, ProgressPath = ProgressPath(shaped, 0, line.Text.Length, geometry, basis) };
        })];
    }

    private static Fx.TextProgressPath ProgressPath(ShapedLine shaped, int start, int end,
        LineRenderGeometry line, Fx.GeometrySample basis)
        => new((float)line.OriginX + shaped.XAt(start), (float)line.OriginX + shaped.XAt(end),
            (float)line.BaselineY, basis.AdvanceBounds.Center.Y);

    private ImmutableArray<Fx.TokenGeometrySample> MeasureTokenGeometries(ProjectSnapshot snapshot, SceneFrame frame)
    {
        List<Fx.TokenGeometrySample> result = [];
        foreach (SceneLineSample sample in frame.Lines)
        {
            var line = sample.Frame.Line!;
            if (line.Tokens.IsEmpty) continue;
            var style = LineBaseStyle(snapshot.LyricStyle, line, sample.Branch);
            var layout = GetLineLayout(line, style);
            ShapedLine shaped = layout.Shaped;
            var lineGeometry = sample.Geometry!;
            SceneMatrix matrix = lineGeometry.WorldMatrix!.Value;
            Fx.Affine2 world = new(matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty);
            var annotations = layout.Ruby;
            int offset = 0;
            TextSpan[] spans = [.. line.Tokens.Select(token =>
            {
                var span = new TextSpan(offset, token.Text.Length); offset = span.End; return span;
            })];
            bool validMapping = sample.Frame.TokensMatchLineText && spans.All(static span => span.Length > 0);
            var partitions = validMapping ? GlyphPartitioner.Partition(shaped, spans) : [];
            for (int i = 0; i < line.Tokens.Length; i++)
            {
                var token = line.Tokens[i];
                ImmutableArray<int> unitIndices = validMapping ? partitions[i].DrawUnitIndices : [];
                bool independent = validMapping && partitions[i].CanTransformIndependently;
                Fx.GeometrySample basis = MeasureUnits(shaped, unitIndices, lineGeometry.OriginX, lineGeometry.BaselineY,
                    world, sample.BindingId!, "base", independent);
                List<Fx.RubyGeometrySample> ruby = [];
                foreach (RubyLayout annotation in annotations.Where(annotation => annotation.TokenIndex == i))
                {
                    var measured = MeasureUnits(annotation.Shaped, [.. Enumerable.Range(0, annotation.Shaped.DrawUnits.Length)],
                        lineGeometry.OriginX + annotation.X, lineGeometry.BaselineY + annotation.Baseline,
                        world, sample.BindingId!, "ruby/" + annotation.Id, independent);
                    ruby.Add(new(new(annotation.Id), measured));
                }
                Fx.GeometrySample family = basis with
                {
                    InkBounds = Union(basis.InkBounds, ruby.Select(static item => item.Geometry.InkBounds)),
                    DrawUnits = [.. basis.DrawUnits, .. ruby.SelectMany(static item => item.Geometry.DrawUnits)],
                };
                Fx.TokenId tokenId = new(token.Id); Fx.BindingId bindingId = new(sample.BindingId!);
                List<V2.TextClusterGeometrySample> clusters = [];
                if (validMapping)
                {
                    foreach (int unitIndex in unitIndices)
                    {
                        var unit = shaped.DrawUnits[unitIndex];
                        var owners = line.Tokens.Select((item, index) => (item, index))
                            .Where(item => spans[item.index].Start < unit.Utf16End && unit.Utf16Start < spans[item.index].End)
                            .Select(static item => new Fx.TokenId(item.item.Id)).ToImmutableArray();
                        var measured = MeasureUnits(shaped, [unitIndex], lineGeometry.OriginX, lineGeometry.BaselineY,
                            world, sample.BindingId!, "base", owners.Length == 1 && shaped.MappingIsExact);
                        var target = new V2.TextClusterTarget(owners[0], bindingId, measured.DrawUnits[0], unitIndex);
                        clusters.Add(new(target, owners, unit.Utf16Start, unit.Utf16Length, measured));
                    }
                    foreach (RubyLayout annotation in annotations.Where(annotation => annotation.TokenIndex == i))
                    for (int unitIndex = 0; unitIndex < annotation.Shaped.DrawUnits.Length; unitIndex++)
                    {
                        var unit = annotation.Shaped.DrawUnits[unitIndex];
                        var measured = MeasureUnits(annotation.Shaped, [unitIndex], lineGeometry.OriginX + annotation.X,
                            lineGeometry.BaselineY + annotation.Baseline, world, sample.BindingId!, "ruby/" + annotation.Id,
                            annotation.Shaped.MappingIsExact);
                        var target = new V2.TextClusterTarget(tokenId, bindingId, measured.DrawUnits[0], unitIndex, new(annotation.Id));
                        clusters.Add(new(target, [tokenId], unit.Utf16Start, unit.Utf16Length, measured));
                    }
                }
                result.Add(new() { BranchId = new(sample.Branch.Id), LineId = new(line.Id), WithRuby = new(tokenId, bindingId),
                    BaseOnly = new(tokenId, bindingId), BaseGeometry = basis, FamilyGeometry = family, Ruby = [.. ruby],
                    Clusters = [.. clusters],
                    ProgressPath = validMapping ? ProgressPath(shaped, spans[i].Start, spans[i].End, lineGeometry, basis) : null });
            }
        }
        return [.. result];
    }

    private static Fx.GeometrySample MeasureUnits(ShapedLine shaped, ImmutableArray<int> indices,
        double origin, double baseline, Fx.Affine2 world, string bindingId, string kind, bool independent)
    {
        ShapedDrawUnit[] units = [.. indices.Select(index => shaped.DrawUnits[index])];
        Fx.Rect2 advance = units.Length == 0 ? new(origin, baseline, origin, baseline)
            : new(origin + units.Min(static unit => unit.X), baseline + units.Min(static unit => unit.Ascent),
                origin + units.Max(static unit => unit.X + unit.Advance), baseline + units.Max(static unit => unit.Descent));
        IEnumerable<Fx.Rect2?> inks = units.SelectMany(static unit => unit.Glyphs).Select(glyph =>
            glyph.InkBounds is { } ink ? (Fx.Rect2?)new Fx.Rect2(origin + ink.Left, baseline + ink.Top,
                origin + ink.Right, baseline + ink.Bottom) : null);
        return new() { AdvanceBounds = advance, InkBounds = Union(null, inks), Baseline = new(advance.Left, baseline),
            BaseWorldMatrix = world, DrawUnits = [.. units.Select(unit => new Fx.DrawUnitId(
                bindingId.Length.ToString(CultureInfo.InvariantCulture) + ":" + bindingId + "/" + kind + "/"
                + unit.Utf16Start.ToString(CultureInfo.InvariantCulture) + ":" + unit.Utf16Length.ToString(CultureInfo.InvariantCulture)))],
            CanTransformIndependently = independent, DiagnosticCode = independent ? null : "UnsupportedGlyphPartition" };
    }

    private static Fx.Rect2? Union(Fx.Rect2? basis, IEnumerable<Fx.Rect2?> rectangles)
    {
        foreach (Fx.Rect2? rectangle in rectangles)
        {
            if (rectangle is not { } box) continue;
            basis = basis is { } current ? new(Math.Min(current.Left, box.Left), Math.Min(current.Top, box.Top),
                Math.Max(current.Right, box.Right), Math.Max(current.Bottom, box.Bottom)) : box;
        }
        return basis;
    }
}
