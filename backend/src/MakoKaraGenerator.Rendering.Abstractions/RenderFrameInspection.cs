using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>工程画布尺寸；投影坐标都在这个空间内。</summary>
public sealed record RenderInspectionCanvas(int Width, int Height);

/// <summary>一个真实整形单元；跨元素单元不可独立变形。Ink 是按 x0,y0,x1,y1… 平铺的墨迹多边形，空墨迹为 null。</summary>
public sealed record RenderInspectionCluster(int UnitIndex, string[] OwnerTokenIds, bool Independent, string? DiagnosticCode, double[]? Ink);

/// <summary>一段注音及其墨迹；归属由所在的基础字给出。</summary>
public sealed record RenderInspectionRuby(string RubyId, double[]? Ink);

/// <summary>一个基础字族的最终几何、字簇与注音。</summary>
public sealed record RenderInspectionToken(string TokenId, string BindingId, string LineId, bool Independent, string? DiagnosticCode,
    double[]? Ink, RenderInspectionCluster[] Clusters, RenderInspectionRuby[] Ruby, int OmittedClusters);

/// <summary>一个行装载的最终 Advance 外框。</summary>
public sealed record RenderInspectionLine(string LineId, string BindingId, double[]? Bounds);

/// <summary>一条演唱光标在最终几何上的位置、进度路径起止与进度来源。</summary>
public sealed record RenderInspectionCursor(string LineId, string BindingId, string? TokenId, double Progress, string ProgressSource,
    double[] Point, double[] Start, double[] End);

/// <summary>一个实例本帧的粒子工作量与出生锚点。</summary>
public sealed record RenderInspectionParticles(string InstanceId, int Births, int Alive, int TrailSamples, double[] BirthPoints, int OmittedBirthPoints);

/// <summary>一个效果实例的来源与绑定的源行；BindingKind 为 project、branch、lines、tokens、clips 或 node。</summary>
public sealed record RenderInspectionEffect(string InstanceId, string? PackageId, string? PackageRevision, string? ExportId, int SdkMajor,
    string BindingKind, string[] LineIds, string? ScriptId, int OmittedLines);

/// <summary>按实例与阶段聚合的脚本耗时。</summary>
public sealed record RenderInspectionScript(string ScriptId, string Stage, long Calls, double WallMs, double MaxMs);

/// <summary>本帧锚点补算的真实工作量。</summary>
public sealed record RenderInspectionAnchors(int UniqueTimes, int TextCalls, int FutureEndpoints, int MemoHits, int MemoMisses);

/// <summary>单帧成本。阶段为包含式墙钟时间，不能相加；分配量为全进程增量。</summary>
public sealed record RenderInspectionCost(double WallMs, double PrepareMs, double ScriptMs, double GraphMs, double MaterialMs,
    double ParticleMs, double RasterMs, double ValidationMs, double ReadbackMs, long ScriptCalls, long ReadbackBytes, long AllocatedBytes,
    int Gc0, int Gc1, int Gc2, RenderInspectionScript[] Scripts, long ScriptOverflow, RenderInspectionAnchors Anchors, double? SessionOpenMs);

/// <summary>因预算省略的条目数；为 0 表示完整。</summary>
public sealed record RenderInspectionTruncation(int Tokens, int Lines, int Cursors, int Particles, int Effects);

/// <summary>效果实例的只读描述，供检查投影标注来源与范围；ScriptId 与成本中脚本条目的 16 位哈希相同，直接图没有脚本时为空。</summary>
public sealed record RenderEffectInstanceInfo(string InstanceId, string? PackageId, string? PackageRevision, string? ExportId, int SdkMajor,
    string BindingKind, ImmutableArray<string> LineIds, string? ScriptId);

/// <summary>能描述自身效果实例的求值会话；不能描述时投影中没有效果条目。</summary>
public interface IEvaluatedFrameInspectionSource
{
    /// <summary>当前固定输入的全部效果实例。</summary>
    ImmutableArray<RenderEffectInstanceInfo> DescribeEffects();
}

/// <summary>
/// 一帧的只读检查投影：与像素出自同一次准备，只含身份、数值与代码，不含作者对象、句柄或路径。
/// 用于调试叠加层与成本视图，不参与绘制、缓存或导出。
/// </summary>
public sealed record RenderFrameInspection(RenderInspectionCanvas Canvas, bool Prepared, RenderInspectionToken[] Tokens,
    RenderInspectionLine[] Lines, RenderInspectionCursor[] Cursors, RenderInspectionParticles[] Particles, RenderInspectionEffect[] Effects,
    RenderInspectionCost Cost, RenderInspectionTruncation Truncated)
{
    /// <summary>条目上限；scale 小于 1 时按比例收紧，用于序列化超出字节预算后的重建。</summary>
    public sealed record Limits(int Tokens, int ClustersPerToken, int RubyPerToken, int Lines, int Cursors, int BirthPointsPerInstance,
        int Effects, int LinesPerEffect, int PolygonPoints)
    {
        /// <summary>默认上限。</summary>
        public static Limits Default { get; } = new(1000, 16, 8, 256, 64, 256, 64, 64, 16);
        /// <summary>按比例收紧，至少保留 1 项。</summary>
        public Limits Scale(double factor)
        {
            int S(int value) => Math.Max(1, (int)(value * factor));
            return new(S(Tokens), S(ClustersPerToken), S(RubyPerToken), S(Lines), S(Cursors),
                S(BirthPointsPerInstance), S(Effects), S(LinesPerEffect), PolygonPoints);
        }
    }

    /// <summary>序列化后的字节上限（camelCase JSON）。</summary>
    public const int MaximumBytes = 256 * 1024;
    /// <summary>与 HTTP 和 Worker 传输一致的序列化选项。</summary>
    public static System.Text.Json.JsonSerializerOptions JsonOptions { get; } = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>投影的 UTF-8 JSON 字节数。</summary>
    public int SerializedBytes() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions).Length;

    /// <summary>
    /// 从准备帧、效果目录与单帧成本生成投影；没有准备帧（无效果）时只有画布与成本。
    /// 超出 <see cref="MaximumBytes"/> 时按比例收紧条目上限重建，省略数记在 Truncated 和各条目里。
    /// </summary>
    public static RenderFrameInspection Build(ProjectSnapshot snapshot, PreparedRenderFrame? prepared,
        ImmutableArray<RenderEffectInstanceInfo> effects, PerformanceEvent? cost, double? sessionOpenMs, Limits? limits = null)
    {
        Limits max = limits ?? Limits.Default;
        for (int attempt = 0; ; attempt++)
        {
            var result = BuildCore(snapshot, prepared, effects, cost, sessionOpenMs, attempt == 0 ? max : max.Scale(Math.Pow(0.5, attempt)));
            if (result.SerializedBytes() <= MaximumBytes) return result;
            if (attempt >= 8) return BuildCore(snapshot, null, [], cost, sessionOpenMs, max) with
            {
                Truncated = new(prepared?.FinalGeometry.Length ?? 0, prepared?.FinalLineGeometry.Length ?? 0,
                    prepared?.Playback.Cursors.Length ?? 0, prepared?.V2ParticleSnapshots.Length ?? 0, effects.IsDefault ? 0 : effects.Length),
            };
        }
    }

    private static RenderFrameInspection BuildCore(ProjectSnapshot snapshot, PreparedRenderFrame? prepared,
        ImmutableArray<RenderEffectInstanceInfo> effects, PerformanceEvent? cost, double? sessionOpenMs, Limits max)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RenderInspectionCanvas canvas = new(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight);
        RenderInspectionAnchors anchors = prepared?.V2AnchorStatistics is { } a
            ? new(a.UniqueTimes, a.TextCalls, a.FutureEndpoints, a.MemoHits, a.MemoMisses) : new(0, 0, 0, 0, 0);
        RenderInspectionCost costs = CostOf(cost, anchors, sessionOpenMs);
        effects = effects.IsDefault ? [] : effects;
        var effectItems = effects.Take(max.Effects).Select(effect => new RenderInspectionEffect(effect.InstanceId, effect.PackageId,
            effect.PackageRevision, effect.ExportId, effect.SdkMajor, effect.BindingKind, [.. effect.LineIds.Take(max.LinesPerEffect)], effect.ScriptId,
            Math.Max(0, effect.LineIds.Length - max.LinesPerEffect))).ToArray();
        if (prepared is null)
            return new(canvas, false, [], [], [], [], effectItems, costs, new(0, 0, 0, 0, Math.Max(0, effects.Length - max.Effects)));

        var tokens = prepared.FinalGeometry.Take(max.Tokens).Select(token =>
        {
            var clusters = token.Clusters.Where(static cluster => cluster.Target.RubyId is null).ToArray();
            return new RenderInspectionToken(token.WithRuby.TokenId.Value, token.WithRuby.BindingId.Value, token.LineId.Value,
                token.BaseGeometry.CanTransformIndependently, token.BaseGeometry.DiagnosticCode, Polygon(token.BaseGeometry.WorldInkCorners, max),
                [.. clusters.Take(max.ClustersPerToken).Select(cluster => new RenderInspectionCluster(cluster.Target.UnitIndex,
                    [.. cluster.OwnerTokens.Select(static id => id.Value)], cluster.Geometry.CanTransformIndependently,
                    cluster.Geometry.DiagnosticCode, Polygon(cluster.Geometry.WorldInkCorners, max)))],
                [.. token.Ruby.Take(max.RubyPerToken).Select(ruby => new RenderInspectionRuby(ruby.RubyId.Value, Polygon(ruby.Geometry.WorldInkCorners, max)))],
                Math.Max(0, clusters.Length - max.ClustersPerToken));
        }).ToArray();
        var lines = prepared.FinalLineGeometry.Take(max.Lines).Select(line => new RenderInspectionLine(line.Target.LineId.Value,
            line.Target.BindingId.Value, Advance(line.BaseGeometry, max))).ToArray();
        var playback = prepared.Playback is { IsAvailable: true } frame ? frame.Cursors : [];
        var cursors = playback.Take(max.Cursors).Select(cursor =>
        {
            var at = cursor.At(CursorAnchorMode.AdvanceCenter);
            var start = (cursor with { Progress = 0 }).At(CursorAnchorMode.AdvanceCenter).Position;
            var end = (cursor with { Progress = 1 }).At(CursorAnchorMode.AdvanceCenter).Position;
            return new RenderInspectionCursor(cursor.Identity.LineId.Value, cursor.Identity.BindingId.Value, cursor.Identity.TokenId?.Value,
                Round(cursor.Progress, 4), Camel(cursor.Identity.ProgressSource.ToString()), Point(at.Position), Point(start), Point(end));
        }).ToArray();
        var particles = prepared.V2ParticleSnapshots.Select(snapshotItem =>
        {
            var births = snapshotItem.Births.Births;
            return new RenderInspectionParticles(snapshotItem.InstanceId.Value, births.Length, snapshotItem.Alive.Length,
                snapshotItem.Trails.Sum(static trail => trail.Samples.Length),
                [.. births.Take(max.BirthPointsPerInstance).SelectMany(static birth => Point(birth.BirthPosition))],
                Math.Max(0, births.Length - max.BirthPointsPerInstance));
        }).ToArray();
        return new(canvas, true, tokens, lines, cursors, particles, effectItems, costs,
            new(Math.Max(0, prepared.FinalGeometry.Length - max.Tokens), Math.Max(0, prepared.FinalLineGeometry.Length - max.Lines),
                Math.Max(0, playback.Length - max.Cursors), 0, Math.Max(0, effects.Length - max.Effects)));
    }

    private static RenderInspectionCost CostOf(PerformanceEvent? cost, RenderInspectionAnchors anchors, double? sessionOpenMs)
    {
        if (cost is null) return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0, anchors, Ms(sessionOpenMs));
        return new(Ms(cost.WallMs), Ms(cost.PrepareMs), Ms(cost.ScriptMs), Ms(cost.GraphMs), Ms(cost.MaterialMs), Ms(cost.ParticleMs),
            Ms(cost.RasterMs), Ms(cost.ValidationMs), Ms(cost.ReadbackMs), cost.ScriptCalls, cost.ImageBytes + cost.SurfaceBytes,
            cost.ProcessAllocatedBytes, cost.Gc0, cost.Gc1, cost.Gc2,
            [.. cost.Scripts.Select(static script => new RenderInspectionScript(script.Identity, script.Stage, script.Calls, Ms(script.WallMs), Ms(script.MaxMs)))],
            cost.ScriptOverflow, anchors, Ms(sessionOpenMs));
    }

    private static double[]? Polygon(ImmutableArray<Point2> corners, Limits max)
    {
        if (corners.IsDefaultOrEmpty) return null;
        if (corners.Length <= max.PolygonPoints) return [.. corners.SelectMany(Point)];
        // 网格映射的多边形顶点过多时取外接框，只用于显示，不代表真实字簇身份。
        double left = corners.Min(static p => p.X), right = corners.Max(static p => p.X);
        double top = corners.Min(static p => p.Y), bottom = corners.Max(static p => p.Y);
        return [Round(left), Round(top), Round(right), Round(top), Round(right), Round(bottom), Round(left), Round(bottom)];
    }

    private static double[]? Advance(GeometrySample geometry, Limits max)
    {
        var bounds = geometry.AdvanceBounds;
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return null;
        return Polygon([geometry.MapPoint(new(bounds.Left, bounds.Top)), geometry.MapPoint(new(bounds.Right, bounds.Top)),
            geometry.MapPoint(new(bounds.Right, bounds.Bottom)), geometry.MapPoint(new(bounds.Left, bounds.Bottom))], max);
    }

    private static double[] Point(Point2 point) => [Round(point.X), Round(point.Y)];
    private static double Round(double value, int digits = 2) => double.IsFinite(value) ? Math.Round(value, digits) : 0;
    private static double Ms(double value) => Round(value, 3);
    private static double? Ms(double? value) => value is { } ms ? Round(ms, 3) : null;
    private static string Camel(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
