using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private FinalSceneGeometry FinalGeometryFor(ActiveEffect active, PreparedRenderFrame frame)
    {
        var scene = frame.HierarchyScene; var binding = active.Definition.Binding;
        FrameBindingScope scope = new(scene);
        var tokens = FrameProjection.Tokens(scene).Where(token => scope.ContainsToken(binding, token)).ToImmutableArray();
        var lines = FrameProjection.Lines(scene, _project.Scene).Where(line => scope.ContainsLine(binding, line)).ToImmutableArray();
        var marks = FrameProjection.Marks(scene, _project.Scene).Where(mark => scope.ContainsMark(binding, mark)).ToImmutableArray();
        var tokenTargets = tokens.Select(static token => token.WithRuby).ToHashSet();
        var lineTargets = lines.Select(static line => line.Target).ToHashSet(); var markTargets = marks.Select(static mark => mark.Target).ToHashSet();
        var sourceGroupIds = scene.Elements.Where(static node => node.SourceNodeId is null).Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        var groups = frame.FinalGroupGeometry.Where(group => sourceGroupIds.Contains(group.Target.NodeId.Value)).ToImmutableArray();
        bool CursorInBinding(CursorGeometry cursor) => cursor.Identity.TokenId is { } token
            ? tokenTargets.Contains(new(token, cursor.Identity.BindingId))
            : binding is not EffectBinding.Tokens && lineTargets.Contains(new(cursor.Identity.LineId, cursor.Identity.BindingId));
        return new() { TimeUs = frame.TimeUs,
            Presentations = new() { TokensInBinding = tokens, LinesInBinding = lines, MarksInBinding = marks, Groups = FrameProjection.Groups(scene) },
            Tokens = [.. frame.FinalGeometry.Where(token => tokenTargets.Contains(token.WithRuby))],
            Lines = [.. frame.FinalLineGeometry.Where(line => lineTargets.Contains(line.Target))],
            Marks = [.. frame.FinalMarkGeometry.Where(mark => markTargets.Contains(mark.Target))],
            Groups = groups,
            Playback = frame.Playback.IsAvailable ? new([.. frame.Playback.Cursors.Where(CursorInBinding)]) : PlaybackFrame.Unavailable };
    }
}
