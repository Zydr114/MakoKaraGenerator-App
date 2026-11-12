using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

internal static class FrameFingerprint
{
    // Explicit primitive projections avoid arbitrary record serialization and TokenId-key dictionary converters.
    // Target collections are maps; source/final geometry arrays retain their production order.
    internal static byte[] Serialize(PreparedRenderFrame frame) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "mako-m07-frame-v2", frame.TimeUs,
        tokenTransforms = frame.TokenTransforms.OrderBy(item => item.Target.BindingId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Target.TokenId.Value, StringComparer.Ordinal)
            .Select(item => new { target = Family(item.Target), matrix = Matrix(item.LocalMatrix) }),
        tokenStyles = frame.TokenStyles.OrderBy(item => item.Target.BindingId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Target.TokenId.Value, StringComparer.Ordinal)
            .Select(item => new { target = Family(item.Target), update = Style(item.Update) }),
        lineTransforms = frame.LineTransforms.OrderBy(item => item.Target.BindingId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Target.LineId.Value, StringComparer.Ordinal)
            .Select(item => new { binding = item.Target.BindingId.Value, line = item.Target.LineId.Value, matrix = Matrix(item.LocalMatrix) }),
        groupTransforms = frame.GroupTransforms.OrderBy(item => item.Target.NodeId.Value, StringComparer.Ordinal)
            .Select(item => new { node = item.Target.NodeId.Value, matrix = Matrix(item.LocalMatrix) }),
        baseTransforms = frame.BaseTransforms.OrderBy(item => item.Target.BindingId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Target.TokenId.Value, StringComparer.Ordinal)
            .Select(item => new { target = Basic(item.Target), matrix = Matrix(item.LocalMatrix) }),
        baseStyles = frame.BaseStyles.OrderBy(item => item.Target.BindingId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Target.TokenId.Value, StringComparer.Ordinal)
            .Select(item => new { target = Basic(item.Target), update = Style(item.Update) }),
        clusterTransforms = frame.ClusterTransforms.OrderBy(item => item.Target.BindingId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Target.DrawUnitId.Value, StringComparer.Ordinal).ThenBy(item => item.Target.UnitIndex)
            .Select(item => new { target = Cluster(item.Target), matrix = Matrix(item.LocalMatrix) }),
        bindings = frame.Scene.Lines.Select(item => new
        {
            item.BindingId, item.ElementId, item.ParentId, line = item.Frame.Line!.Id,
            world = item.Geometry?.WorldMatrix is { } matrix ? SceneMatrixValues(matrix) : null,
            pivot = item.Geometry is { } geometry ? new[] { geometry.Pivot.X, geometry.Pivot.Y } : null,
            exit = item.ExitReplica is { } exit ? new
            {
                exit.HideTimeUs, exit.LoadOrdinal, path = exit.SourceNodePath.ToArray(), lineStyle = FrozenStyle(exit.LineStyle),
                tokens = exit.TokenStyles.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
                    .Select(pair => new { token = pair.Key.Value, style = FrozenStyle(pair.Value) }),
            } : null,
        }),
        finalGeometry = frame.FinalGeometry.Select(item => new
        {
            branch = item.BranchId.Value, line = item.LineId.Value, target = Family(item.WithRuby),
            basic = Geometry(item.BaseGeometry), family = Geometry(item.FamilyGeometry),
            ruby = item.Ruby.Select(ruby => new { id = ruby.RubyId.Value, geometry = Geometry(ruby.Geometry) }),
            clusters = item.Clusters.Select(cluster => new { target = Cluster(cluster.Target),
                owners = cluster.OwnerTokens.Select(id => id.Value), cluster.Utf16Start, cluster.Utf16Length, geometry = Geometry(cluster.Geometry) }),
        }),
        playback = new { frame.Playback.IsAvailable, cursors = frame.Playback.Cursors.Select(item => new
        {
            track = item.TrackId.Value, binding = item.Identity.BindingId.Value, line = item.Identity.LineId.Value,
            token = item.Identity.TokenId?.Value, occurrence = item.Identity.OccurrenceId?.Value,
            start = item.Identity.TimeRange.StartUs, end = item.Identity.TimeRange.EndUs, item.Progress,
            path = new[] { (double)item.Path.StartX, item.Path.EndX, item.Path.BaselineY, item.Path.CenterY },
            world = Matrix(item.WorldMatrix),
        }) },
        diagnostics = frame.Diagnostics.Select(item => new { instance = item.InstanceId.Value, item.Stage, item.TimeUs,
            severity = item.Diagnostic.Severity.ToString(), item.Diagnostic.Code, item.Diagnostic.Message,
            location = item.Diagnostic.Location is { } location ? new { location.Path, location.Line, location.Column } : null }),
    });

    private static object Family(TokenFamilyTarget target) => new { binding = target.BindingId.Value, token = target.TokenId.Value };
    private static object Basic(BaseTokenTarget target) => new { binding = target.BindingId.Value, token = target.TokenId.Value };
    private static object Cluster(TextClusterTarget target) => new { binding = target.BindingId.Value, token = target.TokenId.Value,
        drawUnit = target.DrawUnitId.Value, target.UnitIndex, ruby = target.RubyId?.Value };
    private static double[] Matrix(Affine2 matrix) => [matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty];
    private static double[] SceneMatrixValues(SceneMatrix matrix) => [matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty];
    private static object Geometry(GeometrySample geometry) => new
    {
        advance = Bounds(geometry.AdvanceBounds), ink = geometry.InkBounds is { } ink ? Bounds(ink) : null,
        baseline = new[] { geometry.Baseline.X, geometry.Baseline.Y }, world = Matrix(geometry.BaseWorldMatrix),
        drawUnits = geometry.DrawUnits.Select(id => id.Value), geometry.CanTransformIndependently, geometry.DiagnosticCode,
    };
    private static double[] Bounds(Rect2 bounds) => [bounds.Left, bounds.Top, bounds.Right, bounds.Bottom];
    private static object FrozenStyle(PresetStyleResolution resolution) => new
    {
        resolution.Style.FontFamilyName, resolution.Style.FontSizePx, resolution.Style.FontWeight,
        resolution.Style.IsItalic, resolution.Style.Opacity, resolution.Style.Scale,
        appearance = Appearance(StaticStyleProjection.View(resolution.Style)),
        sources = resolution.Sources.OrderBy(pair => pair.Key).Select(pair => new { property = pair.Key.ToString(), path = pair.Value }),
    };
    // P3 第 6 步起外观分两态、描边多层并带扫字样式；指纹按两态各一份记录。
    private static object Appearance(StyleView style) => new { unsung = Face(style.Unsung), sung = Face(style.Sung), style.Opacity, wipe = style.Wipe };
    private static object Face(AppearanceView face) => new
        { fill = Material(face.Fill), strokes = face.Strokes.Select(Stroke).ToArray(), shadow = face.Shadow, glow = face.Glow };
    private static object Style(StyleUpdate style) => new
    {
        style.OpacityFactor, unsung = Update(style.Unsung), sung = Update(style.Sung), wipe = style.Wipe,
        edits = style.MaterialEdits.Select(edit => edit is MaterialEdit.ReplaceMaterial replace
            ? new { slot = replace.Slot.ToString(), material = Material(replace.Material) }
            : throw new InvalidOperationException("Finite M07 runner does not fingerprint uniform writes.")),
    };
    private static object? Update(AppearanceUpdate? update) => update is null ? null : new
    {
        fill = Material(update.Fill),
        strokes = update.Strokes is { } strokes ? strokes.Layers.Select(Stroke).ToArray() : null,
        shadow = update.Shadow is { } shadow ? new { value = shadow.Value } : null,
        glow = update.Glow is { } glow ? new { value = glow.Value } : null,
    };
    private static object Stroke(StrokeValue stroke) => new
        { stroke.WidthPx, material = Material(stroke.Material), join = stroke.Join.ToString() };
    private static SolidMaterialFingerprint? Material(MaterialValue? material) => material switch
    {
        null => null,
        MaterialValue.SolidMaterial solid => new("solid", [solid.Color.Red, solid.Color.Green, solid.Color.Blue, solid.Color.Alpha]),
        _ => throw new InvalidOperationException("Finite M07 runner only fingerprints solid materials; expand the canonical format before adding other samples."),
    };

    private sealed record SolidMaterialFingerprint(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("color")] double[] Color);
}
