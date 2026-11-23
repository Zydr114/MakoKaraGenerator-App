using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaSceneGeometry
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

    internal static LyricStyle LineBaseStyle(LyricStyle style, LyricLine line, LyricBranch branch)
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
        return line.Tokens.Select(token => token.StyleOverride is { } overlay ? LayoutElementPadding(style, overlay) : LayoutDecorationExtent(style))
            .Prepend(LayoutDecorationExtent(style)).Max();
    }

    private LineRenderGeometry? MeasureSceneGeometry(ProjectSnapshot snapshot, SceneLineSample scene, Composition composition, bool includeWorld = false)
    {
        if (scene.Frame.Line is not { } line || string.IsNullOrEmpty(line.Text)) return null;
        LyricStyle style = LineBaseStyle(snapshot.LyricStyle, line, scene.Branch);
        var layout = GetLineLayout(line, style);
        var shaped = layout.Shaped;
        TextInkBounds? groupInk = GroupInk(shaped.InkBounds, layout.Ruby);
        double maxWidthRatio = Math.Min(scene.Presentation.MaxWidthRatio, scene.Presentation.SafeArea.Right - scene.Presentation.SafeArea.Left);
        var geometry = BuildGeometry(line.Id, style, shaped, composition, scene.Placement, groupInk, maxWidthRatio, scene, includeWorld);
        if (!includeWorld || geometry is null || scene.CueId is not { } cueId) return geometry;
        return YieldForAttachedCountdown(snapshot, scene, style, shaped, composition, groupInk, maxWidthRatio, cueId) ?? geometry;
    }

    /// <summary>在目标句完整绑定生命周期内保持恒定退让，使前奏期与演唱期交接处零跳变。</summary>
    private static LineRenderGeometry? YieldForAttachedCountdown(ProjectSnapshot snapshot, SceneLineSample scene, LyricStyle style,
        ShapedLine shaped, Composition composition, TextInkBounds? groupInk, double maxWidthRatio, string cueId)
    {
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        var program = tree.FindProgram(scene.Branch.Id);
        var cue = program?.LeadIns.FirstOrDefault(item => item.Id == cueId);
        if (cue is null || tree.FindNode(cue.IndicatorContainerId) is not { Content: CountdownSequenceContent sequence } group) return null;
        var mode = group.Attachment?.Mode ?? SceneAttachmentMode.FixedParent;
        if (mode is not (SceneAttachmentMode.CueTargetText or SceneAttachmentMode.CueTargetAboveStart)) return null;
        if (LeadInTiming.Range(scene.Frame.Line!, cue.DurationUs) is null) return null;
        int totalMarks = sequence.MarkNodeIds.Length;
        if (totalMarks == 0) return null;
        int firstVisibleIndex = -1;
        int lastVisibleIndex = -1;
        for (int i = 0; i < totalMarks; i++)
        {
            if (tree.FindNode(sequence.MarkNodeIds[i]) is { Content: TextMarkContent markContent } markNode
                && ResolveMarkOpacity(snapshot, sequence, markNode, markContent) > 1e-4f)
            {
                if (firstVisibleIndex < 0) firstVisibleIndex = i;
                lastVisibleIndex = i;
            }
        }
        if (firstVisibleIndex < 0) return null;

        var fixedNodes = SceneTreeGeometryResolver.Resolve(tree.Nodes, composition.CanvasWidth, composition.CanvasHeight);
        var containerRef = fixedNodes.TryGetValue(scene.ContainerId, out var resolvedContainer)
            ? resolvedContainer
            : new SceneTreeGeometry(composition.CanvasWidth, composition.CanvasHeight, SceneMatrix.Identity, new(0, 0));
        double containerW = containerRef.Width, containerH = containerRef.Height;

        SceneMatrix localParentMatrix = SceneMatrix.Identity;
        if (!string.Equals(scene.ParentId, scene.ContainerId, StringComparison.Ordinal))
        {
            string? currentId = scene.ParentId;
            bool reachedContainer = false;
            int guard = 0;
            while (currentId is not null && guard++ <= tree.Nodes.Length)
            {
                if (string.Equals(currentId, scene.ContainerId, StringComparison.Ordinal))
                {
                    reachedContainer = true;
                    break;
                }
                var node = tree.FindNode(currentId);
                if (node is null) break;
                var nodeParentRef = node.ParentId is { } npId && fixedNodes.TryGetValue(npId, out var resolvedNodeParent)
                    ? resolvedNodeParent
                    : new SceneTreeGeometry(composition.CanvasWidth, composition.CanvasHeight, SceneMatrix.Identity, new(0, 0));
                double nw = nodeParentRef.Width * node.ReferenceRect.WidthRatio;
                double nh = nodeParentRef.Height * node.ReferenceRect.HeightRatio;
                var nAnchor = SceneMatrix.Anchor(node.Placement.Anchor, nw, nh);
                var nLocal = SceneMatrix.Translate(node.Placement.Position.X * nodeParentRef.Width, node.Placement.Position.Y * nodeParentRef.Height)
                    * SceneMatrix.RotateScale(node.Placement.RotationDegrees, node.Placement.Scale)
                    * SceneMatrix.Translate(-nAnchor.X, -nAnchor.Y);
                localParentMatrix = nLocal * localParentMatrix;
                currentId = node.ParentId;
            }
            if (!reachedContainer)
            {
                if (!containerRef.WorldMatrix.TryInverse(out var containerInv)) return null;
                localParentMatrix = containerInv * scene.ParentMatrix;
            }
        }
        if (!localParentMatrix.TryInverse(out var localParentInv)) return null;

        var steadyScene = scene with
        {
            ContainerMatrix = SceneMatrix.Identity,
            ParentMatrix = localParentMatrix,
        };
        var steadyGeometry = BuildGeometry(scene.Frame.Line!.Id, style, shaped, composition, scene.Placement, groupInk, maxWidthRatio, steadyScene, includeWorld: true);
        if (steadyGeometry.WorldMatrix is null) return null;

        var safe = scene.Presentation.SafeArea.IsValid ? scene.Presentation.SafeArea : NormalizedSafeArea.Full;
        double safeLeft = safe.Left * containerW;
        double safeRight = safe.Right * containerW;
        double safeTop = safe.Top * containerH;
        double safeBottom = safe.Bottom * containerH;
        double safeW = Math.Max(1, safeRight - safeLeft);
        double safeH = Math.Max(1, safeBottom - safeTop);

        var parentRef = group.ParentId is { } parentId && fixedNodes.TryGetValue(parentId, out var resolvedParent)
            ? resolvedParent
            : new SceneTreeGeometry(composition.CanvasWidth, composition.CanvasHeight, SceneMatrix.Identity, new(0, 0));

        (double OrigStartX, double CountdownMinX, double MinX, double MaxX, double MinY, double MaxY) MeasureAttach(LineRenderGeometry target)
        {
            var baseTop = target.EffectiveBaseTopLeft;
            var baseBottom = target.EffectiveBaseBottomRight;
            double baseCenterY = (baseTop.Y + baseBottom.Y) / 2d;
            double origStartX = target.WorldMatrix!.Value.Map(new(baseTop.X, baseCenterY)).X;
            var anchor = mode == SceneAttachmentMode.CueTargetAboveStart
                ? new CanvasPoint(baseTop.X, target.TopLeft.Y)
                : new CanvasPoint(baseTop.X, baseCenterY);
            var basis = target.WorldMatrix!.Value * SceneMatrix.Translate(anchor.X, anchor.Y);
            double gw = parentRef.Width * group.ReferenceRect.WidthRatio;
            double gh = parentRef.Height * group.ReferenceRect.HeightRatio;
            double px = group.Placement.Position.X * parentRef.Width;
            double py = group.Placement.Position.Y * parentRef.Height;
            var gAnchor = SceneMatrix.Anchor(group.Placement.Anchor, gw, gh);
            var gWorld = basis * SceneMatrix.Translate(px, py)
                * SceneMatrix.RotateScale(group.Placement.RotationDegrees, group.Placement.Scale)
                * SceneMatrix.Translate(-gAnchor.X, -gAnchor.Y);
            double x0 = gw * firstVisibleIndex / totalMarks;
            double x1 = gw * (lastVisibleIndex + 1) / totalMarks;
            CanvasPoint[] markCorners =
            [
                gWorld.Map(new(x0, 0d)),
                gWorld.Map(new(x1, 0d)),
                gWorld.Map(new(x1, gh)),
                gWorld.Map(new(x0, gh)),
            ];
            double countdownMinX = markCorners.Min(p => p.X);
            CanvasPoint[] all = [.. target.Corners(), .. markCorners];
            return (origStartX, countdownMinX, all.Min(p => p.X), all.Max(p => p.X), all.Min(p => p.Y), all.Max(p => p.Y));
        }

        var metrics = MeasureAttach(steadyGeometry);
        var basisPlacement = scene.Placement ?? LinePlacementEditor.ResolveDefault(style);
        double unionW = metrics.MaxX - metrics.MinX;
        double unionH = metrics.MaxY - metrics.MinY;
        double fit = Math.Clamp(Math.Min(unionW > safeW ? safeW / unionW : 1d, unionH > safeH ? safeH / unionH : 1d), 0.1d, 1d);
        if (fit < 1d)
        {
            var scaledPlacement = basisPlacement with { Scale = basisPlacement.Scale * fit };
            steadyGeometry = BuildGeometry(scene.Frame.Line!.Id, style, shaped, composition, scaledPlacement, groupInk, maxWidthRatio, steadyScene, includeWorld: true);
            if (steadyGeometry.WorldMatrix is null) return null;
            metrics = MeasureAttach(steadyGeometry);
        }

        double inlineDx = mode == SceneAttachmentMode.CueTargetText && metrics.CountdownMinX < metrics.OrigStartX
            ? metrics.OrigStartX - metrics.CountdownMinX
            : 0d;
        double shiftedMinX = metrics.MinX + inlineDx;
        double shiftedMaxX = metrics.MaxX + inlineDx;
        double clampDx = shiftedMaxX - shiftedMinX <= safeW
            ? (shiftedMinX < safeLeft ? safeLeft - shiftedMinX : shiftedMaxX > safeRight ? safeRight - shiftedMaxX : 0d)
            : (safeLeft + safeRight) / 2d - (shiftedMinX + shiftedMaxX) / 2d;
        double clampDy = metrics.MaxY - metrics.MinY <= safeH
            ? (metrics.MinY < safeTop ? safeTop - metrics.MinY : metrics.MaxY > safeBottom ? safeBottom - metrics.MaxY : 0d)
            : (safeTop + safeBottom) / 2d - (metrics.MinY + metrics.MaxY) / 2d;
        double dx = inlineDx + clampDx;
        double dy = clampDy;

        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6 && fit >= 1d - 1e-9) return null;

        var shiftedContainerPivot = new CanvasPoint(steadyGeometry.Pivot.X + dx, steadyGeometry.Pivot.Y + dy);
        var shiftedLocalPivot = localParentInv.Map(shiftedContainerPivot);
        double refW = scene.ParentReferenceWidth > 0 ? scene.ParentReferenceWidth : composition.CanvasWidth;
        double refH = scene.ParentReferenceHeight > 0 ? scene.ParentReferenceHeight : composition.CanvasHeight;
        var local = scene.LocalTransform ?? LyricLocalTransform.Neutral;
        var yieldedPlacement = basisPlacement with
        {
            Position = new(shiftedLocalPivot.X / refW - local.OffsetXRatio, shiftedLocalPivot.Y / refH - local.OffsetYRatio),
            Scale = basisPlacement.Scale * fit,
        };
        return BuildGeometry(scene.Frame.Line!.Id, style, shaped, composition, yieldedPlacement, groupInk, maxWidthRatio, scene, includeWorld: true);
    }

    private static float ResolveMarkOpacity(ProjectSnapshot snapshot, CountdownSequenceContent sequence,
        SceneContainerDefinition markNode, TextMarkContent markContent) =>
        snapshot.PresetApplications.Items.IsEmpty
            ? sequence.Appearance.Resolve(markContent.AppearanceOverride).Opacity
            : PresetSceneStyleProjection.Mark(snapshot, markNode.Id).Style.Opacity;
}
