using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering;

/// <summary>最后一次网格写入与其真实实例、编译产物及固定包归属；null 仍是显式关闭。</summary>
public sealed record PreparedTextWarpBinding(EffectInstanceId InstanceId, string ArtifactHash,
    EffectPackageIdentity? PackageIdentity, TextTarget Target, GridWarp? Value);

public sealed partial class PreparedRenderFrame
{
    /// <summary>按目标保留最后网格及来源；原始全部写入已在宿主成功事务中累计计账。</summary>
    public ImmutableArray<PreparedTextWarpBinding> TextWarps { get; private init; } = [];

    /// <summary>在完整文字输出之后从原层级场景建立统一映射，不从最终矩阵反推局部顺序。</summary>
    public PreparedRenderFrame WithTextWarps(ImmutableArray<PreparedTextWarpBinding> warps)
    {
        if (warps.IsDefault) throw new PreparedFrameException("InvalidPreparedWarp", "网格输出不能为 default。");
        if (warps.IsEmpty && TextWarps.IsEmpty) return this;
        Dictionary<TextTarget, PreparedTextWarpBinding> values = [];
        foreach (var binding in warps)
        {
            if (binding is null || !binding.InstanceId.IsValid || string.IsNullOrWhiteSpace(binding.ArtifactHash)
                || binding.Target is not (TextTarget.Family or TextTarget.Base or TextTarget.Cluster or TextTarget.Mark)
                || !values.TryAdd(binding.Target, binding))
                throw new PreparedFrameException("InvalidPreparedWarp", "网格需要唯一真实目标与完整作者来源。");
            _ = WarpGeometry(binding.Target);
        }
        if (values.Values.All(static binding => binding.Value is null))
            return new(this, OverlayBatches, DiagnosticSnapshot, VisualBudget, Particles, EmissionSchedule) { TextWarps = warps };
        var budget = new TextGeometryProjectionBudget();
        var sourceStyles = StaticStyleProjection.LoadedSourceStyles(Snapshot, HierarchyScene);
        var markStyles = StaticStyleProjection.MarkStyles(Snapshot, HierarchyScene);
        var final = HierarchyScene.TokenGeometries.Select(ProjectToken).ToImmutableArray();
        var marks = HierarchyScene.MarkGeometries.Select(mark =>
        {
            TextTarget target = new TextTarget.Mark(mark.Target);
            if (!values.TryGetValue(target, out var binding) || binding.Value is null) return FinalMarkGeometry.Single(item => item.Target == mark.Target);
            var matrix = MarkTransforms.FirstOrDefault(item => item.Target == mark.Target)?.LocalMatrix ?? Affine2.Identity;
            var mapping = Mapping([(target, matrix)], mark.Geometry.BaseWorldMatrix);
            return mark with { Geometry = Project(mark.Geometry, mapping, target,
                _markStyles.GetValueOrDefault(mark.Target, markStyles[mark.Target.NodeId])) };
        }).ToImmutableArray();
        var lines = FinalLineGeometry.Select(line =>
        {
            var tokens = final.Where(token => token.WithRuby.BindingId == line.Target.BindingId).ToArray();
            if (!tokens.Any(token => token.Clusters.Any(cluster => cluster.Geometry.Mapping is not null))) return line;
            return line with { BaseGeometry = line.BaseGeometry with
                { MappedInkVertices = [.. tokens.SelectMany(static token => token.BaseGeometry.WorldInkCorners)] } };
        }).ToImmutableArray();
        var playback = PlaybackProjection.Create(Snapshot, HierarchyScene, final);
        return new(this, OverlayBatches, DiagnosticSnapshot, VisualBudget, Particles, EmissionSchedule)
        {
            TextWarps = warps, FinalGeometry = final, FinalMarkGeometry = marks, FinalLineGeometry = lines,
            FinalGroupGeometry = GroupInkProjection.Create(HierarchyScene, final, marks), Playback = playback,
        };

        TokenGeometrySample ProjectToken(TokenGeometrySample token)
        {
            TextTarget family = new TextTarget.Family(token.WithRuby), basic = new TextTarget.Base(token.BaseOnly);
            bool affected = values.Any(pair => pair.Value.Value is not null && Belongs(pair.Key, token));
            if (!affected) return FinalGeometry.Single(item => item.WithRuby == token.WithRuby);
            var fa = TokenTransforms.FirstOrDefault(item => item.Target == token.WithRuby)?.LocalMatrix ?? Affine2.Identity;
            var ba = BaseTransforms.FirstOrDefault(item => item.Target == token.BaseOnly)?.LocalMatrix ?? Affine2.Identity;
            var familyStyle = _styles.GetValueOrDefault(token.WithRuby, sourceStyles[token.WithRuby]);
            var baseStyle = BaseDrawingStyles.GetValueOrDefault(token.BaseOnly, familyStyle);
            var baseMapping = Mapping([(basic, ba), (family, fa)], token.BaseGeometry.BaseWorldMatrix);
            var familyMapping = Mapping([(family, fa)], token.BaseGeometry.BaseWorldMatrix);
            var clusters = token.Clusters.Select(cluster =>
            {
                TextTarget target = new TextTarget.Cluster(cluster.Target);
                var ca = ClusterTransforms.FirstOrDefault(item => item.Target == cluster.Target)?.LocalMatrix ?? Affine2.Identity;
                var mapping = cluster.Target.RubyId is null
                    ? Mapping([(target, ca), (basic, ba), (family, fa)], cluster.Geometry.BaseWorldMatrix)
                    : Mapping([(target, ca), (family, fa)], cluster.Geometry.BaseWorldMatrix);
                return cluster.WithGeometry(Project(cluster.Geometry, mapping, target,
                    cluster.Target.RubyId is null ? baseStyle : familyStyle));
            }).ToImmutableArray();
            var baseGeometry = Project(token.BaseGeometry, baseMapping, basic, baseStyle, validateInk: false);
            baseGeometry = baseGeometry with { MappedInkVertices = [.. clusters.Where(static cluster => cluster.Target.RubyId is null)
                .SelectMany(static cluster => cluster.Geometry.WorldInkCorners)] };
            var ruby = token.Ruby.Select(item => item with
            {
                Geometry = Project(item.Geometry, familyMapping, family, familyStyle, validateInk: false) with
                { MappedInkVertices = [.. clusters.Where(cluster => cluster.Target.RubyId == item.RubyId).SelectMany(static cluster => cluster.Geometry.WorldInkCorners)] },
            }).ToImmutableArray();
            var familyGeometry = token.FamilyGeometry with { Mapping = familyMapping,
                MappedInkVertices = [.. baseGeometry.WorldInkCorners, .. ruby.SelectMany(static item => item.Geometry.WorldInkCorners)] };
            return token with { BaseGeometry = baseGeometry, FamilyGeometry = familyGeometry, Ruby = ruby, Clusters = clusters };
        }

        TextGeometryMapping Mapping((TextTarget Target, Affine2 Matrix)[] levels, Affine2 world)
        {
            var steps = ImmutableArray.CreateBuilder<TextGeometryMapping.Step>();
            foreach (var (target, matrix) in levels)
            {
                if (values.TryGetValue(target, out var binding) && binding.Value is { } warp) steps.Add(new TextGeometryMapping.Grid(warp));
                if (matrix != Affine2.Identity) steps.Add(new TextGeometryMapping.Affine(matrix));
            }
            // Keep the outer step even when identity: the pixel path borrows the
            // already installed hierarchy canvas and omits precisely this step.
            steps.Add(new TextGeometryMapping.Affine(world));
            return new(steps.ToImmutable());
        }

        GeometrySample Project(GeometrySample source, TextGeometryMapping mapping, TextTarget target, LyricStyle style, bool validateInk = true)
        {
            try
            {
                _ = mapping.MapRectangle(source.AdvanceBounds, budget);
                _ = mapping.MapPoint(source.Baseline);
                ImmutableArray<Point2> ink = [];
                if (validateInk && source.InkBounds is { } bounds)
                {
                    ink = mapping.MapRectangle(bounds, budget);
                    _ = mapping.MapRectangle(WarpDecoratedBounds(bounds, style), budget);
                }
                return source with { Mapping = mapping, MappedInkVertices = ink };
            }
            catch (EffectOutputException error)
            {
                throw new PreparedFrameException(error.Code, error.Message) { TextTarget = LastWarpTarget(target) };
            }
        }

        TextTarget LastWarpTarget(TextTarget target)
        {
            if (values.TryGetValue(target, out var own) && own.Value is not null) return target;
            TokenFamilyTarget? token = target switch { TextTarget.Base basic => new(basic.Value.TokenId, basic.Value.BindingId),
                TextTarget.Cluster cluster => new(cluster.Value.TokenId, cluster.Value.BindingId), TextTarget.Family family => family.Value, _ => null };
            if (token is not null)
            {
                TextTarget basic = new TextTarget.Base(new(token.TokenId, token.BindingId));
                if (target is not TextTarget.Cluster { Value.RubyId: not null } && values.TryGetValue(basic, out var basis) && basis.Value is not null) return basic;
                TextTarget family = new TextTarget.Family(token);
                if (values.TryGetValue(family, out var parent) && parent.Value is not null) return family;
            }
            return target;
        }
    }

    /// <summary>取得原层级场景中真实目标的固定几何；不接受伪造装载、Ruby 或绘制单元。</summary>
    public GeometrySample WarpGeometry(TextTarget target) => target switch
    {
        TextTarget.Family family => HierarchyScene.TokenGeometries.SingleOrDefault(item => item.WithRuby == family.Value)?.FamilyGeometry,
        TextTarget.Base basic => HierarchyScene.TokenGeometries.SingleOrDefault(item => item.BaseOnly == basic.Value)?.BaseGeometry,
        TextTarget.Cluster cluster => HierarchyScene.TokenGeometries.SelectMany(static item => item.Clusters)
            .FirstOrDefault(item => item.Target == cluster.Value)?.Geometry,
        TextTarget.Mark mark => HierarchyScene.MarkGeometries.SingleOrDefault(item => item.Target == mark.Value)?.Geometry,
        _ => null,
    } ?? throw new PreparedFrameException("InvalidWarpTarget", "网格目标不属于当前真实文字装载。") { TextTarget = target };

    private static bool Belongs(TextTarget target, TokenGeometrySample token) => target switch
    {
        TextTarget.Family family => family.Value == token.WithRuby,
        TextTarget.Base basic => basic.Value == token.BaseOnly,
        TextTarget.Cluster cluster => token.Clusters.Any(item => item.Target == cluster.Value),
        _ => false,
    };

    /// <summary>网格必须容纳的保守装饰框；透明采样 gutter 不计入作者定义域；两态装饰不同时取较大者。</summary>
    public static Rect2 WarpDecoratedBounds(Rect2 ink, LyricStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var sung = style.ForState(true);
        double extent = ReferenceEquals(sung, style) ? DecorationExtent(style) : Math.Max(DecorationExtent(style), DecorationExtent(sung));
        return new(ink.Left - extent, ink.Top - extent, ink.Right + extent, ink.Bottom + extent);
    }

    private static double DecorationExtent(LyricStyle style)
    {
        double stroke = style.Stroke is { IsEnabled: true } outline ? outline.WidthPx : 0;
        double shadow = style.Shadow is { IsEnabled: true } shade
            ? Math.Max(Math.Abs(shade.OffsetXPx), Math.Abs(shade.OffsetYPx)) + 3d * shade.BlurRadiusPx : 0;
        double glow = style.Glow is { IsEnabled: true } halo ? halo.SpreadPx + 3d * halo.BlurRadiusPx : 0;
        return Math.Max(stroke + shadow, glow);
    }
}
