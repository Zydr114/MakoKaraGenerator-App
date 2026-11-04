using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>在真实运行父树上组合组与整行局部仿射；不重新调度、整形或修改工程。</summary>
public static class SceneAffineProjection
{
    /// <summary>返回层次变换后的绘制帧；恒等集合保留原帧引用，非法装载／父树或溢出拒绝。</summary>
    public static SceneFrame Apply(SceneFrame scene, ImmutableArray<Fx.LineTransform> transforms)
        => Apply(scene, transforms, []);
    /// <summary>在共同运行父树上各组合一次组和整行输出，恒等集合保留原帧。</summary>
    public static SceneFrame Apply(SceneFrame scene, ImmutableArray<Fx.LineTransform> transforms, ImmutableArray<Fx.GroupTransform> groupTransforms)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (transforms.IsDefault) throw new ArgumentException("整行变换集合不能是 default。", nameof(transforms));
        if (groupTransforms.IsDefault) throw new ArgumentException("组变换集合不能是 default。", nameof(groupTransforms));
        if (transforms.IsEmpty && groupTransforms.IsEmpty) return scene;
        try { return ApplyCore(scene, transforms, groupTransforms); }
        catch (ArgumentException error) { throw new PreparedFrameException("InvalidSceneTransform", "层次变换超出有限可逆范围：" + error.Message); }
    }
    private static SceneFrame ApplyCore(SceneFrame scene, ImmutableArray<Fx.LineTransform> transforms, ImmutableArray<Fx.GroupTransform> groupTransforms)
    {
        var nodes = scene.Elements.ToDictionary(static node => node.Id, StringComparer.Ordinal);
        var lines = scene.Lines.ToDictionary(static line => new Fx.LineTarget(new(line.Frame.Line!.Id), new(line.BindingId!)));
        var lineGeometry = scene.LineGeometries.ToDictionary(static line => line.Target);
        Dictionary<string, Fx.Affine2> own = new(StringComparer.Ordinal);
        HashSet<Fx.GroupTarget> seenGroups = [];
        foreach (var transform in groupTransforms)
        {
            if (transform is null || transform.Target is null || !seenGroups.Add(transform.Target)
                || !nodes.TryGetValue(transform.Target.NodeId.Value, out var node) || node.Kind != SceneElementKind.Group || node.SourceNodeId is not null
                || !scene.GroupGeometries.Any(group => group.Target == transform.Target))
                throw new PreparedFrameException("InvalidPreparedGroupTarget", "组变换目标重复或缺少实际组节点／几何。");
            if (!transform.LocalMatrix.TryInverse(out _)) throw new PreparedFrameException("InvalidTransform", "组局部矩阵不可逆。");
            if (transform.LocalMatrix != Fx.Affine2.Identity)
            {
                own.Add(node.Id, transform.LocalMatrix);
                foreach (var alias in scene.Elements.Where(value => value.Kind == SceneElementKind.Group && value.SourceNodeId == node.Id))
                    own.Add(alias.Id, transform.LocalMatrix);
            }
        }
        HashSet<Fx.LineTarget> seen = [];
        foreach (var transform in transforms)
        {
            if (transform is null || transform.Target is null || !seen.Add(transform.Target) || !lines.TryGetValue(transform.Target, out var line)
                || line.Geometry is null || !lineGeometry.ContainsKey(transform.Target) || !nodes.ContainsKey(line.ElementId))
                throw new PreparedFrameException("InvalidPreparedLineTarget", "整行变换目标重复或缺少实际装载／节点／几何。");
            if (!transform.LocalMatrix.TryInverse(out _)) throw new PreparedFrameException("InvalidTransform", "整行局部矩阵不可逆。");
            if (transform.LocalMatrix != Fx.Affine2.Identity && !own.TryAdd(line.ElementId, transform.LocalMatrix))
                throw new PreparedFrameException("UnsupportedConcurrentNodeTransform", "同一节点的不同装载不能共用一个世界矩阵。");
        }
        if (own.Count == 0) return scene;
        Dictionary<string, (Fx.Affine2 World, Fx.Affine2 Delta)> resolved = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);
        (Fx.Affine2 World, Fx.Affine2 Delta) Resolve(string id)
        {
            if (resolved.TryGetValue(id, out var known)) return known;
            if (!nodes.TryGetValue(id, out var node) || !visiting.Add(id))
                throw new PreparedFrameException("InvalidSceneTransform", "运行父树循环或缺少实际父节点。");
            var basis = Matrix(node.WorldMatrix); var world = basis;
            if (node.ParentId is { } parent)
            {
                var inherited = Resolve(parent).Delta;
                if (inherited != Fx.Affine2.Identity) world = inherited.Compose(world);
            }
            if (own.TryGetValue(id, out var local)) world = world.Compose(local);
            Fx.Affine2 delta = Fx.Affine2.Identity;
            if (world != basis)
            {
                if (!world.TryInverse(out _) || !basis.TryInverse(out var inverse))
                    throw new PreparedFrameException("InvalidSceneTransform", "基础或最终节点矩阵不可逆。");
                delta = world.Compose(inverse);
            }
            visiting.Remove(id); return resolved[id] = (world, delta);
        }
        CanvasPoint Map(Fx.Affine2 delta, CanvasPoint point)
        { var mapped = delta.Map(new(point.X, point.Y)); return new(mapped.X, mapped.Y); }
        LineRenderGeometry Geometry(string id, LineRenderGeometry geometry)
        {
            var transform = Resolve(id);
            return transform.Delta == Fx.Affine2.Identity ? geometry : geometry with
                { WorldMatrix = Matrix(transform.World), Pivot = Map(transform.Delta, geometry.Pivot) };
        }
        Fx.GeometrySample Glyph(string id, Fx.GeometrySample geometry)
            => Resolve(id).Delta == Fx.Affine2.Identity ? geometry : geometry with { BaseWorldMatrix = Resolve(id).World };
        var byBinding = scene.Lines.ToDictionary(static line => line.BindingId!, StringComparer.Ordinal);
        var frame = scene with
        {
            Elements = [.. scene.Elements.Select(node =>
            {
                var transform = Resolve(node.Id);
                if (transform.Delta == Fx.Affine2.Identity) return node;
                var reference = node.PlacementReference;
                if (reference is not null && node.ParentId is { } parent && Resolve(parent).Delta is var inherited && inherited != Fx.Affine2.Identity)
                    reference = reference with { WorldMatrix = Matrix(inherited.Compose(Matrix(reference.WorldMatrix))), Pivot = Map(inherited, reference.Pivot) };
                return node with { WorldMatrix = Matrix(transform.World), Pivot = Map(transform.Delta, node.Pivot),
                    WorldCorners = [.. node.WorldCorners.Select(point => Map(transform.Delta, point))], PlacementReference = reference };
            })],
            Lines = [.. scene.Lines.Select(line => Resolve(line.ElementId).Delta == Fx.Affine2.Identity ? line : line with
            { Geometry = Geometry(line.ElementId, line.Geometry!), ParentMatrix = Matrix(Resolve(line.ParentId).World),
                ContainerMatrix = Matrix(Resolve(line.ContainerId).World) })],
            TextMarks = [.. scene.TextMarks.Select(mark => Resolve(mark.NodeId).Delta == Fx.Affine2.Identity ? mark
                : mark with { Geometry = Geometry(mark.NodeId, mark.Geometry) })],
            LineGeometries = [.. scene.LineGeometries.Select(line => line with
                { BaseGeometry = Glyph(lines[line.Target].ElementId, line.BaseGeometry) })],
            TokenGeometries = [.. scene.TokenGeometries.Select(token =>
            {
                string id = byBinding[token.WithRuby.BindingId.Value].ElementId;
                return Resolve(id).Delta == Fx.Affine2.Identity ? token : token with { BaseGeometry = Glyph(id, token.BaseGeometry),
                    FamilyGeometry = Glyph(id, token.FamilyGeometry), Ruby = [.. token.Ruby.Select(ruby => ruby with { Geometry = Glyph(id, ruby.Geometry) })],
                    Clusters = [.. token.Clusters.Select(cluster => cluster.WithGeometry(Glyph(id, cluster.Geometry)))] };
            })],
            MarkGeometries = [.. scene.MarkGeometries.Select(mark => Resolve(mark.Target.NodeId.Value).Delta == Fx.Affine2.Identity ? mark
                : mark with { Geometry = Glyph(mark.Target.NodeId.Value, mark.Geometry) })],
            GroupGeometries = [.. scene.GroupGeometries.Select(group =>
            {
                var transform = Resolve(group.Target.NodeId.Value);
                return transform.Delta == Fx.Affine2.Identity ? group : group with { BaseWorldMatrix = transform.World,
                    WorldPivot = transform.Delta.Map(group.WorldPivot) };
            })],
        };
        return frame with { GroupGeometries = GroupInkProjection.Create(frame) };
    }
    private static Fx.Affine2 Matrix(SceneMatrix value) => new(value.A, value.B, value.C, value.D, value.Tx, value.Ty);
    private static SceneMatrix Matrix(Fx.Affine2 value) => new(value.A, value.B, value.C, value.D, value.Tx, value.Ty);
}
