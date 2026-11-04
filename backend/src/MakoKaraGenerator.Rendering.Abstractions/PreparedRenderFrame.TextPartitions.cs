using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering;

public sealed partial class PreparedRenderFrame
{
    /// <summary>经过真实独立字形目标验证的仅基础字局部变换。</summary>
    public ImmutableArray<BaseTokenTransform> BaseTransforms { get; private init; } = [];
    /// <summary>仅基础字的最终叶外观；不回写父字族或注音。</summary>
    public ImmutableArray<BaseTokenStyle> BaseStyles { get; private init; } = [];
    /// <summary>真实基础字或 Ruby 不可拆单元的最终局部仿射。</summary>
    public ImmutableArray<TextClusterTransform> ClusterTransforms { get; private init; } = [];
    private ImmutableDictionary<BaseTokenTarget, LyricStyle> BaseDrawingStyles { get; init; } = ImmutableDictionary<BaseTokenTarget, LyricStyle>.Empty;

    /// <summary>将 v2 基础字／真实字簇输出附加到已完成的父层级与字族计划；校验后才发布。</summary>
    public PreparedRenderFrame WithTextPartitions(ImmutableArray<BaseTokenTransform> baseTransforms,
        ImmutableArray<BaseTokenStyle> baseStyles, ImmutableArray<TextClusterTransform> clusterTransforms)
    {
        if (baseTransforms.IsDefault || baseStyles.IsDefault || clusterTransforms.IsDefault)
            throw new PreparedFrameException("InvalidPreparedTextPartition", "文字分区输出需要明确不可变集合。");
        if (baseTransforms.IsEmpty && baseStyles.IsEmpty && clusterTransforms.IsEmpty) return this;
        var tokens = HierarchyScene.TokenGeometries.ToDictionary(static item => item.BaseOnly);
        var clusters = HierarchyScene.TokenGeometries.SelectMany(static item => item.Clusters)
            .DistinctBy(static item => item.Target).ToDictionary(static item => item.Target);
        HashSet<BaseTokenTarget> seen = [];
        foreach (var output in baseTransforms)
        {
            if (output is null || output.Target is null || !seen.Add(output.Target) || !tokens.TryGetValue(output.Target, out var token))
                throw PartitionError("InvalidPreparedTextPartition", "基础字变换需要唯一真实目标。",
                    output?.Target is { } target ? new TextTarget.Base(target) : null);
            TextTarget textTarget = new TextTarget.Base(output.Target);
            Independent(token.BaseGeometry, textTarget); Matrix(output.LocalMatrix, textTarget);
        }
        seen.Clear();
        // 主构造已校验同一 HierarchyScene；仅基础字外观输出需要再次读取源外观。
        var sourceStyles = baseStyles.IsEmpty ? ImmutableDictionary<TokenFamilyTarget, LyricStyle>.Empty
            : StaticStyleProjection.LoadedSourceStyles(Snapshot, HierarchyScene);
        var drawing = ImmutableDictionary.CreateBuilder<BaseTokenTarget, LyricStyle>();
        foreach (var output in baseStyles)
        {
            if (output is null || output.Target is null || output.Update is null || !seen.Add(output.Target)
                || !tokens.TryGetValue(output.Target, out var token)
                || !sourceStyles.TryGetValue(new(output.Target.TokenId, output.Target.BindingId), out var source))
                throw PartitionError("InvalidPreparedTextPartition", "基础字外观需要唯一真实目标。",
                    output?.Target is { } target ? new TextTarget.Base(target) : null);
            TextTarget textTarget = new TextTarget.Base(output.Target);
            Independent(token.BaseGeometry, textTarget);
            var basis = _styles.GetValueOrDefault(token.WithRuby, source);
            // 分区材质需要独立的协调器区域与归属；首版只接受已经能直接绘制的纯色和渐变。
            if (ContainsPackageMaterial(output.Update) || !SourceMaterialCatalog.Collect(basis).IsEmpty || HasMaterial(new ImageEffectTarget.Token(token.WithRuby)))
                throw PartitionError("UnsupportedPartitionMaterial", "基础字独立包材质需要分区协调器支持。", textTarget);
            try { drawing.Add(output.Target, StaticStyleProjection.ApplyDrawingStyle(basis, output.Update, SourceMaterials)); }
            catch (EffectOutputException error) { throw PartitionError(error.Code, error.Message, textTarget); }
            catch (PreparedFrameException error) { throw PartitionError(error.Code, error.Message, error.TextTarget ?? textTarget); }
        }
        HashSet<TextClusterTarget> seenClusters = [];
        foreach (var output in clusterTransforms)
        {
            if (output is null || output.Target is null || !seenClusters.Add(output.Target) || !clusters.TryGetValue(output.Target, out var cluster))
                throw PartitionError("InvalidPreparedClusterTarget", "字簇变换需要唯一真实整形单元。",
                    output?.Target is { } target ? new TextTarget.Cluster(target) : null);
            TextTarget textTarget = new TextTarget.Cluster(output.Target);
            Independent(cluster.Geometry, textTarget); Matrix(output.LocalMatrix, textTarget);
        }
        var families = TokenTransforms.ToDictionary(static item => item.Target, static item => item.LocalMatrix);
        var bases = baseTransforms.ToDictionary(static item => item.Target, static item => item.LocalMatrix);
        var clusterMatrices = clusterTransforms.ToDictionary(static item => item.Target, static item => item.LocalMatrix);
        var final = HierarchyScene.TokenGeometries.Select(Project).ToImmutableArray();
        var playback = PlaybackProjection.Create(Snapshot, HierarchyScene, final);
        if (playback.IsAvailable)
            playback = new([.. playback.Cursors.Select(cursor =>
            {
                if (cursor.Identity.TokenId is not { } id) return cursor;
                var token = final.Single(item => item.WithRuby.TokenId == id && item.WithRuby.BindingId == cursor.Identity.BindingId);
                double x = cursor.Path.At(cursor.Progress, CursorAnchorMode.Baseline).X;
                var cluster = token.Clusters.Where(static item => item.Target.RubyId is null && item.OwnerTokens.Length == 1)
                    .FirstOrDefault(item => x >= item.Geometry.AdvanceBounds.Left && x < item.Geometry.AdvanceBounds.Right);
                return cluster is null ? cursor : cursor with { WorldMatrix = cluster.Geometry.BaseWorldMatrix };
            })]);
        return new(this, OverlayBatches, DiagnosticSnapshot, VisualBudget, Particles, EmissionSchedule)
        {
            BaseTransforms = baseTransforms, BaseStyles = baseStyles, ClusterTransforms = clusterTransforms,
            BaseDrawingStyles = drawing.ToImmutable(), FinalGeometry = final, Playback = playback,
            FinalGroupGeometry = GroupInkProjection.Create(HierarchyScene, final, FinalMarkGeometry),
        };

        TokenGeometrySample Project(TokenGeometrySample token)
        {
            if (!bases.ContainsKey(token.BaseOnly) && !drawing.ContainsKey(token.BaseOnly)
                && !token.Clusters.Any(cluster => clusterMatrices.ContainsKey(cluster.Target)))
                return FinalGeometry.Single(item => item.WithRuby == token.WithRuby);
            var family = families.GetValueOrDefault(token.WithRuby, Affine2.Identity);
            var basic = bases.GetValueOrDefault(token.BaseOnly, Affine2.Identity);
            var familyWorld = token.BaseGeometry.BaseWorldMatrix.Compose(family);
            var baseWorld = familyWorld.Compose(basic);
            var projected = token.Clusters.Select(cluster => cluster.WithGeometry(cluster.Geometry with
            {
                BaseWorldMatrix = (cluster.Target.RubyId is null ? baseWorld : familyWorld)
                    .Compose(clusterMatrices.GetValueOrDefault(cluster.Target, Affine2.Identity)),
            })).ToImmutableArray();
            var baseGeometry = token.BaseGeometry with { BaseWorldMatrix = baseWorld };
            var ruby = token.Ruby.Select(item => item with { Geometry = item.Geometry with { BaseWorldMatrix = familyWorld } }).ToImmutableArray();
            if (!projected.IsEmpty)
            {
                baseGeometry = baseGeometry with { InkBounds = ClusterInk(projected.Where(static item => item.Target.RubyId is null), baseWorld) };
                ruby = [.. ruby.Select(item => item with { Geometry = item.Geometry with
                    { InkBounds = ClusterInk(projected.Where(cluster => cluster.Target.RubyId == item.RubyId), familyWorld) } })];
            }
            var familyInk = UnionInk(new[] { baseGeometry }.Concat(ruby.Select(static item => item.Geometry)), familyWorld);
            return token with { BaseGeometry = baseGeometry, FamilyGeometry = token.FamilyGeometry with
                { BaseWorldMatrix = familyWorld, InkBounds = familyInk }, Ruby = ruby, Clusters = projected };
        }
    }

    /// <summary>读取只影响基础字的最终绘制描述；包含父字族外观，再应用基础字叶覆盖。</summary>
    public LyricStyle GetBaseDrawingStyle(BaseTokenTarget target) => BaseDrawingStyles.TryGetValue(target, out var style) ? style
        : throw new PreparedFrameException("MissingPreparedBaseStyle", "基础字没有独立外观输出。");

    private static PreparedFrameException PartitionError(string code, string message, TextTarget? target)
        => new(code, message) { TextTarget = target };
    private static void Independent(GeometrySample geometry, TextTarget target)
    {
        if (!geometry.CanTransformIndependently)
            throw PartitionError(geometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "跨源元素的整形单元不能独立修改。", target);
    }
    private static void Matrix(Affine2 matrix, TextTarget target)
    {
        if (!matrix.TryInverse(out _)) throw PartitionError("InvalidTransform", "文字分区矩阵必须有限且可逆。", target);
    }
    private static Rect2? ClusterInk(IEnumerable<TextClusterGeometrySample> clusters, Affine2 world)
        => UnionInk(clusters.Select(static item => item.Geometry), world);
    private static Rect2? UnionInk(IEnumerable<GeometrySample> geometries, Affine2 world)
    {
        if (!world.TryInverse(out var inverse)) throw new PreparedFrameException("InvalidTransform", "最终文字分区世界矩阵不可逆。");
        Rect2? result = null;
        foreach (var point in geometries.SelectMany(static item => item.WorldInkCorners).Select(inverse.Map))
            result = result is { } box ? new(Math.Min(box.Left, point.X), Math.Min(box.Top, point.Y), Math.Max(box.Right, point.X), Math.Max(box.Bottom, point.Y))
                : new(point.X, point.Y, point.X, point.Y);
        return result;
    }
}
