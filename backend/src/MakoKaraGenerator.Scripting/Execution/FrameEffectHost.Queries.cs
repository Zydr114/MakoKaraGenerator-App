using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.Projection;

using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private void InvokeContext(ActiveEffect active, string stage, long timeUs, RuntimeDiagnosticBuffer diagnostics,
        Action<DiagnosticReporter, GeometryQueries> action, CancellationToken cancellationToken)
    {
        GeometryQueries queries = new((time, filter) => BaseAt(time, filter, cancellationToken));
        try { Invoke(active, stage, timeUs, diagnostics, reporter =>
        {
            if (stage == "Evaluate") action(reporter, queries);
            else { using var measured = MeasureAuthor(active, stage); action(reporter, queries); }
        }); }
        finally { queries.Invalidate(); }
    }

    private static PerformanceTelemetry.ScriptScope MeasureAuthor(ActiveEffect active, string stage)
    {
        if (active.TelemetryIdentity is null || !PerformanceTelemetry.HasFrame) return default;
        return PerformanceTelemetry.MeasureScriptIdentity(active.TelemetryIdentity, stage);
    }

    private BaseSceneGeometry BaseAt(long timeUs, SourceFilter filter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<LineId>? lineIds = null; HashSet<TokenId>? tokenIds = null;
        switch (filter)
        {
            case SourceFilter.Project: break;
            case SourceFilter.Branch branch: lineIds = [.. _project.Content.GetBranch(branch.Id).LineIds]; break;
            case SourceFilter.Lines sourceLines:
                foreach (var id in sourceLines.Ids) _ = _project.Content.GetLine(id);
                lineIds = [.. sourceLines.Ids]; break;
            case SourceFilter.Tokens tokens:
                lineIds = [.. tokens.Ids.Select(id => _project.Content.GetToken(id).LineId)]; tokenIds = [.. tokens.Ids]; break;
            default: throw new ArgumentOutOfRangeException(nameof(filter));
        }
        var frame = _geometry.GetSceneFrame(_snapshot, timeUs);
        cancellationToken.ThrowIfCancellationRequested();
        var lines = FrameProjection.Lines(frame, _project.Scene).Where(line => lineIds is null || lineIds.Contains(line.LineId)).ToImmutableArray();
        var marks = FrameProjection.Marks(frame, _project.Scene).Where(mark => lineIds is null || lineIds.Contains(mark.TargetLine.LineId)).ToImmutableArray();
        HashSet<NodeId> ancestors = [];
        var nodes = frame.Elements.ToDictionary(static node => node.Id, StringComparer.Ordinal);
        foreach (var id in lines.Select(static line => line.NodeId).Concat(marks.Select(static mark => mark.Target.NodeId)))
        {
            string? current = id.Value;
            while (current is not null && nodes.TryGetValue(current, out var node))
            { if (node.Kind == Rendering.SceneElementKind.Group) ancestors.Add(new(current)); current = node.ParentId; }
        }
        return new() { TimeUs = timeUs,
            Lines = lines, Marks = marks,
            Tokens = [.. frame.TokenGeometries.Where(token => (lineIds is null || lineIds.Contains(token.LineId))
                && (tokenIds is null || tokenIds.Contains(token.WithRuby.TokenId)))],
            LineGeometry = [.. frame.LineGeometries.Where(line => lineIds is null || lineIds.Contains(line.Target.LineId))],
            MarkGeometry = [.. frame.MarkGeometries.Where(mark => marks.Any(item => item.Target == mark.Target))],
            Groups = [.. FrameProjection.Groups(frame).Where(group => lineIds is null || ancestors.Contains(group.Target.NodeId))],
            GroupGeometry = [.. frame.GroupGeometries.Where(group => lineIds is null || ancestors.Contains(group.Target.NodeId))] };
    }
}
