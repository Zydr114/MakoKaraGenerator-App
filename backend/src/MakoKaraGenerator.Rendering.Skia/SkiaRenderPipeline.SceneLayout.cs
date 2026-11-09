using System.Runtime.CompilerServices;
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

    // 行基础样式只由工程样式、分支覆盖与不可变行对象决定；源行与预设投影行（按解析器复用）逐帧是同一对象，
    // 按行对象记忆并以引用核对另两项输入，测量、布局与绘制各处不再重复逐层合并与校验。
    private sealed class LineBaseStyleMemo(LyricStyle style, LyricStyleOverride? branchOverride, LyricStyle result)
    {
        internal LyricStyle Style { get; } = style;
        internal LyricStyleOverride? BranchOverride { get; } = branchOverride;
        internal LyricStyle Result { get; } = result;
    }
    private static readonly ConditionalWeakTable<LyricLine, LineBaseStyleMemo> LineBaseStyles = new();

    private static LyricStyle LineBaseStyle(LyricStyle style, LyricLine line, LyricBranch branch)
    {
        if (LineBaseStyles.TryGetValue(line, out var memo) && ReferenceEquals(memo.Style, style)
            && ReferenceEquals(memo.BranchOverride, branch.StyleOverride)) return memo.Result;
        var result = LyricStyleResolver.Resolve(style, lineOverride: line.StyleOverride, branchOverride: branch.StyleOverride).Style;
        LineBaseStyles.AddOrUpdate(line, new(style, branch.StyleOverride, result));
        return result;
    }

    private static double SceneDecorationPadding(ProjectSnapshot snapshot, SceneLineSample scene)
    {
        var line = scene.Frame.Line!;
        var style = LineBaseStyle(snapshot.LyricStyle, line, scene.Branch);
        return line.Tokens.Select(token => token.StyleOverride is { } overlay ? ElementPadding(style, overlay) : DecorationExtent(style))
            .Prepend(DecorationExtent(style)).Max();
    }

    private LineRenderGeometry? MeasureSceneGeometry(ProjectSnapshot snapshot, SceneLineSample scene, Composition composition, bool includeWorld = false)
    {
        if (scene.Frame.Line is not { } line || string.IsNullOrEmpty(line.Text)) return null;
        LyricStyle style = LineBaseStyle(snapshot.LyricStyle, line, scene.Branch);
        var layout = GetLineLayout(line, style);
        var shaped = layout.Shaped;
        return BuildGeometry(line.Id, style, shaped, composition, scene.Placement,
            GroupInk(shaped.InkBounds, layout.Ruby), Math.Min(scene.Presentation.MaxWidthRatio, scene.Presentation.SafeArea.Right - scene.Presentation.SafeArea.Left), scene, includeWorld);
    }
}
