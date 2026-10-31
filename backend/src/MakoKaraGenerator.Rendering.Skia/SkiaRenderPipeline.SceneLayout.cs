using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private SceneLineSample[] ArrangeStaticRows(ProjectSnapshot snapshot,
        IReadOnlyList<SceneLineSample> scene, Composition composition)
    {
        var result = scene.ToArray();
        var groups = result.Select((entry, index) => (entry, index)).Where(item =>
            item.entry.AnimationPolicy == LyricBranchAnimationPolicy.Static)
            .GroupBy(item => item.entry.ContainerId);
        foreach (var group in groups)
        {
            var rows = group.ToArray();
            if (rows.Length == 1 && rows[0].entry.Presentation.SafeArea == NormalizedSafeArea.Full) continue;
            var branch = rows[0].entry.Presentation;
            if (!branch.SafeArea.IsValid) continue;
            var measured = rows.Select(row => MeasureSceneGeometry(snapshot, row.entry with { LocalTransform = null }, composition)).ToArray();
            if (measured.Any(geometry => geometry is null)) continue;
            double width = rows[0].entry.ParentReferenceWidth, height = rows[0].entry.ParentReferenceHeight;
            double top = branch.SafeArea.Top * height, bottom = branch.SafeArea.Bottom * height;
            var paddings = rows.Select(row => SceneDecorationPadding(snapshot, row.entry)).ToArray();
            var heights = measured.Select((geometry, i) =>
            {
                var corners = geometry!.Corners();
                return corners.Max(point => point.Y) - corners.Min(point => point.Y)
                    + paddings[i] * 2 * geometry.Scale;
            }).ToArray();
            const double gap = 6;
            double total = heights.Sum() + gap * (rows.Length - 1);
            double fit = Math.Min(1, (bottom - top) / total);
            double center = (branch.DefaultPlacement?.Position.Y ?? (branch.SafeArea.Top + branch.SafeArea.Bottom) / 2) * height;
            double cursor = Math.Clamp(center - total * fit / 2, top, bottom - total * fit);
            for (int i = 0; i < rows.Length; i++)
            {
                var geometry = measured[i]!;
                double rowCenter = cursor + heights[i] * fit / 2;
                var corners = geometry.Corners();
                double centerX = (corners.Max(point => point.X) + corners.Min(point => point.X)) / 2;
                double halfWidth = (corners.Max(point => point.X) - corners.Min(point => point.X)) * fit / 2
                    + paddings[i] * geometry.Scale * fit;
                double left = branch.SafeArea.Left * width, right = branch.SafeArea.Right * width;
                double targetX = halfWidth * 2 >= right - left ? (left + right) / 2 : Math.Clamp(centerX, left + halfWidth, right - halfWidth);
                double centerOffset = (corners.Max(point => point.Y) + corners.Min(point => point.Y)) / 2 - geometry.Pivot.Y;
                ScenePlacement placement = geometry.Placement with
                {
                    Position = new((targetX - (centerX - geometry.Pivot.X) * fit) / width,
                        (rowCenter - centerOffset * fit) / height),
                    Scale = geometry.Placement.Scale * fit,
                };
                result[rows[i].index] = rows[i].entry with { Placement = placement };
                cursor += (heights[i] + gap) * fit;
            }
        }
        return result;
    }

    private static double SceneDecorationPadding(ProjectSnapshot snapshot, SceneLineSample scene)
    {
        var line = scene.Frame.Line!;
        var style = LyricStyleResolver.Resolve(snapshot.LyricStyle, lineOverride: line.StyleOverride,
            branchOverride: scene.Branch.StyleOverride).Style;
        return line.Tokens.Select(token => token.StyleOverride is { } overlay ? ElementPadding(style, overlay) : DecorationExtent(style))
            .Prepend(DecorationExtent(style)).Max();
    }

    private LineRenderGeometry? MeasureSceneGeometry(ProjectSnapshot snapshot, SceneLineSample scene, Composition composition, bool includeWorld = false)
    {
        if (scene.Frame.Line is not { } line || string.IsNullOrEmpty(line.Text)) return null;
        LyricStyle style = LyricStyleResolver.Resolve(snapshot.LyricStyle, lineOverride: line.StyleOverride,
            branchOverride: scene.Branch.StyleOverride).Style;
        var layout = GetLineLayout(line, style);
        var shaped = layout.Shaped;
        return BuildGeometry(line.Id, style, shaped, composition, scene.Placement,
            GroupInk(shaped.InkBounds, layout.Ruby), Math.Min(scene.Presentation.MaxWidthRatio, scene.Presentation.SafeArea.Right - scene.Presentation.SafeArea.Left), scene, includeWorld);
    }
}
