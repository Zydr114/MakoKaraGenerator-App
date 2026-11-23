using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaSceneGeometry
{
    /// <inheritdoc/>
    public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId)
    {
        return GetSceneFrame(snapshot, 0, lineId).Lines.FirstOrDefault(entry => entry.Frame.Line?.Id == lineId)?.Geometry;
    }

    /// <inheritdoc/>
    public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
    {
        return MeasureSceneFrame(snapshot, timeUs, editingLineId, false);
    }

    /// <summary>同一几何后端的测量采样接缝；效果会话可使用布局代表或固定编辑行。</summary>
    public SceneFrame GetMeasurementSceneFrame(ProjectSnapshot snapshot, long timeUs, bool layoutReference,
        string? editingLineId = null, string? editingCueId = null)
        => MeasureSceneFrame(layoutReference ? LayoutReferenceScene.CreateSnapshot(snapshot) : snapshot,
            timeUs, editingLineId, layoutReference, editingCueId);

    internal SceneFrame MeasureSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId, bool layoutReference, string? editingCueId = null)
    {
        lock (_synchronizationRoot)
            return MeasureSceneFrameCore(snapshot, timeUs, editingLineId, layoutReference, editingCueId);
    }

    private SceneFrame MeasureSceneFrameCore(ProjectSnapshot snapshot, long timeUs, string? editingLineId, bool layoutReference, string? editingCueId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        SceneFrame frame = layoutReference ? LayoutReferenceScene.Sample(snapshot)
            : editingLineId is null ? LyricSceneSampler.SampleFrame(snapshot, timeUs) : SampleEditingFrame(snapshot, editingLineId, editingCueId);
        frame = PresetSceneStyleProjection.ApplyLines(snapshot, frame);
        var arranged = ArrangeStaticRows(snapshot, frame.Lines, snapshot.Composition);
        frame = MeasureFrameElements(snapshot, frame with { Lines = [.. arranged.Select(sample => sample with
            { Geometry = MeasureSceneGeometry(snapshot, sample, snapshot.Composition, includeWorld: true) })] });
        frame = frame with { TokenGeometries = MeasureTokenGeometries(snapshot, frame), LineGeometries = MeasureLineGeometries(snapshot, frame),
            MarkGeometries = MeasureMarkGeometries(frame) };
        return frame with { GroupGeometries = MeasureGroupInk(frame) };
    }

    private static SceneFrame SampleEditingFrame(ProjectSnapshot snapshot, string lineId, string? editingCueId = null)
    {
        snapshot = snapshot with { LyricPresentation = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics) };
        var branch = snapshot.Lyrics.Branches.FirstOrDefault(item => item.Lines.Any(line => line.Id == lineId));
        if (branch is null) return new() { Definition = SceneDefinitionBuilder.Build(snapshot) };
        var tree = snapshot.LyricPresentation;
        var program = tree.FindProgram(branch.Id)!;
        var selected = branch.Lines.First(line => line.Id == lineId);
        var segment = LyricDisplaySegmentResolver.Resolve(branch, program, selected.Time?.StartUs ?? 0);
        var assignedClip = program.Clips.FirstOrDefault(item => item.LineIds.Contains(lineId));
        string containerId = assignedClip?.ContainerId ?? program.DefaultContainerId;
        var presentation = tree.ResolveDisplay(branch.Id, containerId);
        List<SceneLineSample> lines = [];
        if (presentation.SlotSequenceProfile is { } profile)
        {
            var members = LyricDisplaySegmentResolver.ScheduleMembers(branch, assignedClip is null ? segment : segment with
                { Clip = assignedClip, Lines = LyricDisplaySegmentResolver.Members(branch, assignedClip) });
            var timed = members.Where(line => line.Time is { IsValid: true }).ToArray();
            int index = Array.FindIndex(timed, line => line.Id == lineId);
            int count = profile.Slots.Length;
            int start = index < 0 ? 0 : index / count * count;
            void Add(LyricLine line, int slotIndex)
            {
                lines.Add(new() { Branch = branch, Presentation = presentation, Frame = StaticSample(line), LocalTransform = tree.FindLine(line.Id)?.LocalTransform,
                    DisplayClipId = assignedClip?.Id,
                    DisplayContainerId = containerId, SlotId = profile.Slots[slotIndex].Id,
                    ParentNodeId = tree.FindNode(containerId)!.Layout!.SlotIds[slotIndex],
                    Placement = SceneDefinitionBuilder.SlotTextPlacement(profile.Slots[slotIndex].Placement) });
            }
            if (index < 0)
            {
                Add(selected, 0);
                for (int i = 0; i < Math.Min(count - 1, timed.Length); i++) Add(timed[i], i + 1);
            }
            else for (int i = start; i < Math.Min(start + count, timed.Length); i++)
                if (assignedClip is null || assignedClip.LineIds.Contains(timed[i].Id)) Add(timed[i], i % count);
        }
        else lines.Add(new() { Branch = branch, Presentation = presentation, Frame = StaticSample(selected), LocalTransform = tree.FindLine(selected.Id)?.LocalTransform,
            DisplayClipId = assignedClip?.Id,
            DisplayContainerId = containerId, Placement = presentation.DefaultPlacement });
        var definition = SceneDefinitionBuilder.Build(snapshot);
        var bound = SceneDefinitionBuilder.BindFrame(new() { Definition = definition,
            Lines = [.. lines.Select(line => line with { BindingId = "layout/" + line.ElementId })] }, snapshot.Composition);
        var cue = program.LeadIns.FirstOrDefault(item => item.TargetLineId == lineId && (editingCueId is null || item.Id == editingCueId));
        if (cue is null || LeadInTiming.Range(selected, cue.DurationUs) is not { } range) return bound;
        var updatedLines = bound.Lines.Select(line => line.Frame.Line?.Id == lineId ? line with { CueId = cue.Id } : line).ToImmutableArray();
        bound = bound with { Lines = updatedLines };
        var target = bound.Lines.Single(line => line.Frame.Line!.Id == lineId);
        return bound with { TimeUs = range.StartUs, LeadIns = [new() { CueId = cue.Id, ProgramId = program.Id,
            ContainerId = cue.IndicatorContainerId, TargetElementId = target.ElementId, TargetBindingId = target.BindingId!,
            BranchId = branch.Id, TargetLineId = lineId, Range = range, NormalizedProgress = 0, BlinkOpacity = 1 }] };
    }

    /// <inheritdoc/>
    public LineRenderGeometry? GetNodeGeometry(ProjectSnapshot snapshot,string nodeId,string? editingLineId = null)
        => SceneEditorGeometry.ForNode(snapshot,GetSceneFrame(snapshot,0,editingLineId),nodeId);
    /// <inheritdoc/>
    public LineRenderGeometry? GetLocalTransformGeometry(ProjectSnapshot snapshot,string lineId)
        => SceneEditorGeometry.ForLine(snapshot,GetSceneFrame(snapshot,0,lineId),lineId);

    internal static TextLayoutOptions LayoutOptionsFor(LyricStyle style) => new()
    {
        FamilyName = style.FontFamilyName, FontSizePx = style.FontSizePx,
        FallbackFamilyNames = style.Typography.FallbackFamilyNames,
        FontWeight = style.FontWeight, IsBold = style.IsBold, IsItalic = style.IsItalic,
        LetterSpacingPx = style.LetterSpacingPx,
    };

    private static LineRenderGeometry BuildGeometry(string lineId, LyricStyle style,
        ShapedLine shaped, Composition composition, ScenePlacement? defaultPlacement = null,
        TextInkBounds? groupInk = null, double maxWidthRatio = 1,
        SceneLineSample? scene = null, bool includeWorld = true)
    {
        double width = scene?.ParentReferenceWidth > 0 ? scene.ParentReferenceWidth : composition.CanvasWidth,
            height = scene?.ParentReferenceHeight > 0 ? scene.ParentReferenceHeight : composition.CanvasHeight;
        TextInkBounds baseInk = shaped.InkBounds ?? new(0, -style.FontSizePx * .85f, shaped.Width, style.FontSizePx * .15f);
        TextInkBounds ink = groupInk ?? baseInk;
        double cx = (ink.Left + ink.Right) / 2d, cy = (ink.Top + ink.Bottom) / 2d;
        ScenePlacement basis = defaultPlacement ?? LinePlacementEditor.ResolveDefault(style);
        var local = scene?.LocalTransform ?? LyricLocalTransform.Neutral;
        ScenePlacement placement = basis with { Position = new(basis.Position.X + local.OffsetXRatio, basis.Position.Y + local.OffsetYRatio),
            RotationDegrees = basis.RotationDegrees + local.RotationDegrees, Scale = basis.Scale * local.Scale };
        double pivotX = placement.Position.X * width, pivotY = placement.Position.Y * height;
        (double ax, double ay) = placement.Anchor switch
        {
            ScenePlacementAnchor.TopLeft => (ink.Left, ink.Top),
            ScenePlacementAnchor.TopCenter => (cx, ink.Top),
            ScenePlacementAnchor.BottomRight => (ink.Right, ink.Bottom),
            ScenePlacementAnchor.LeftCenter => (ink.Left, cy),
            ScenePlacementAnchor.RightCenter => (ink.Right, cy),
            _ => (cx, cy),
        };
        double origin = pivotX - ax, baseline = pivotY - ay;
        double scale = style.Scale * basis.Scale, rotation = placement.RotationDegrees;
        if (ink.Right > ink.Left) scale = Math.Min(scale, width * maxWidthRatio / (ink.Right - ink.Left));
        scale *= local.Scale;
        var localMatrix = SceneMatrix.Translate(pivotX, pivotY)
            * SceneMatrix.RotateScale(rotation, placement.Scale)
            * SceneMatrix.RotateScale(0, scale / placement.Scale)
            * SceneMatrix.Translate(-pivotX, -pivotY);
        var parent = includeWorld && scene is not null ? scene.ParentMatrix : SceneMatrix.Identity;
        return new LineRenderGeometry
        {
            LineId = lineId, Placement = placement, OriginX = origin, BaselineY = baseline,
            ElementId = scene?.ElementId, ContainerId = scene?.ContainerId, BindingId = scene?.BindingId,
            WorldMatrix = parent * localMatrix,
            Pivot = parent.Map(new(pivotX, pivotY)), Scale = scale, RotationDegrees = rotation,
            TopLeft = new(origin + ink.Left, baseline + ink.Top),
            BottomRight = new(origin + ink.Right, baseline + ink.Bottom),
            BaseTopLeft = new(origin + baseInk.Left, baseline + baseInk.Top),
            BaseBottomRight = new(origin + baseInk.Right, baseline + baseInk.Bottom),
        };
    }

    private static LyricFrameSample StaticSample(LyricLine line)
    {
        int offset = 0;
        var tokens = line.Tokens.Select((token, index) =>
        {
            TokenSample sample = new(index, offset, token.Text.Length, 0, ProgressSource.Unavailable, false);
            offset += token.Text.Length;
            return sample;
        }).ToImmutableArray();
        return new LyricFrameSample { Line = line, Tokens = tokens,
            TokensMatchLineText = string.Concat(line.Tokens.Select(entry => entry.Text)) == line.Text };
    }
}
