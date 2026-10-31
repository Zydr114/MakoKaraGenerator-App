using System.Collections.Immutable;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>根据同一固定字形及运行父树聚合组墨迹，不使用容器参考框伪造墨迹。</summary>
public static class GroupInkProjection
{
    /// <summary>聚合基础或最终族／标记墨迹；最终族须完整覆盖固定单元，否则保留完整行依据。</summary>
    public static ImmutableArray<Fx.GroupGeometrySample> Create(SceneFrame frame,
        ImmutableArray<Fx.TokenGeometrySample> finalTokens = default, ImmutableArray<Fx.TextMarkGeometrySample> finalMarks = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.GroupGeometries.IsEmpty) return [];
        var tokens = finalTokens.IsDefault ? frame.TokenGeometries : finalTokens;
        var marks = finalMarks.IsDefault ? frame.MarkGeometries : finalMarks;
        var parents = frame.Elements.ToDictionary(static element => element.Id, static element => element.ParentId, StringComparer.Ordinal);
        var lines = frame.Lines.ToDictionary(static line => line.BindingId!, StringComparer.Ordinal);
        var corners = frame.GroupGeometries.ToDictionary(static group => group.Target.NodeId.Value, static _ => new List<Fx.Point2>(), StringComparer.Ordinal);
        void Add(string? current, ImmutableArray<Fx.Point2> ink)
        {
            HashSet<string> visited = new(StringComparer.Ordinal);
            while (current is not null)
            {
                if (!visited.Add(current)) throw new PreparedFrameException("InvalidSceneTransform", "运行父树不能循环聚合墨迹。");
                if (corners.TryGetValue(current, out var group)) group.AddRange(ink);
                current = parents.GetValueOrDefault(current);
            }
        }
        foreach (var line in frame.LineGeometries)
        {
            string id = lines[line.Target.BindingId.Value].ElementId;
            var families = tokens.Where(token => token.WithRuby.BindingId == line.Target.BindingId).ToArray();
            var units = families.SelectMany(static token => token.BaseGeometry.DrawUnits).ToHashSet();
            if (!finalTokens.IsDefault && families.Length > 0 && line.BaseGeometry.DrawUnits.All(units.Contains))
                foreach (var family in families) Add(id, family.FamilyGeometry.WorldInkCorners);
            else
            {
                Add(id, line.BaseGeometry.WorldInkCorners);
                foreach (var family in families)
                    foreach (var ruby in family.Ruby) Add(id, ruby.Geometry.WorldInkCorners);
            }
        }
        foreach (var mark in marks) Add(mark.Target.NodeId.Value, mark.Geometry.WorldInkCorners);
        return [.. frame.GroupGeometries.Select(group => group with { WorldInkCorners = [.. corners[group.Target.NodeId.Value]] })];
    }
}
