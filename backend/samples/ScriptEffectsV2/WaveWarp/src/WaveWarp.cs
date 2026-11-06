using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record WaveData(double AmplitudePx, double FrequencyHz, double WavelengthPx, long RiseUs, long SettleUs,
    ImmutableDictionary<TokenId, TimeRange> Times) : PreparedEffect;

/// <summary>正在唱的字按网格起伏，唱完后在 settleUs 内回落。网格作用于整个字族（含注音），
/// 播放头、粒子出生点和星芒都读取变形后的最终几何。</summary>
public sealed class WaveWarp : IPureKaraokeEffect
{
    private const int Columns = 6;
    private const double MarginPx = 64;

    public PreparedEffect Declare(DeclareContext source)
    {
        var times = ImmutableDictionary.CreateBuilder<TokenId, TimeRange>();
        foreach (var token in source.Content.Tokens)
            if (token.TimeRange is { } range) times[token.Id] = range;
        var p = source.Parameters;
        return new WaveData(p.GetNumber("amplitudePx"), p.GetNumber("frequencyHz"), p.GetNumber("wavelengthPx"),
            p.GetDuration("riseUs"), p.GetDuration("settleUs"), times.ToImmutable());
    }

    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
    {
        var d = (WaveData)data;
        if (d.AmplitudePx <= 0) return;
        double seconds = frame.TimeUs / 1_000_000d;
        foreach (var token in frame.Geometry)
        {
            // 共享字簇（连字）不能单独变形；缺时间的字没有演唱区间，保持原样。
            if (!token.BaseGeometry.CanTransformIndependently || !d.Times.ContainsKey(token.WithRuby.TokenId)) continue;
            double envelope = Envelope(frame.TimeUs, d.Times[token.WithRuby.TokenId], d.RiseUs, d.SettleUs);
            if (envelope <= 0) continue;
            var domain = FamilyDomain(token);
            var points = ImmutableArray.CreateBuilder<Point2>((Columns + 1) * 2);
            for (int row = 0; row <= 1; row++)
                for (int column = 0; column <= Columns; column++)
                {
                    double y = row == 0 ? domain.Top : domain.Bottom;
                    double x = domain.Left + domain.Width * column / Columns;
                    double phase = 2 * Math.PI * (d.FrequencyHz * seconds + x / d.WavelengthPx);
                    points.Add(new(x, y + d.AmplitudePx * envelope * Math.Sin(phase)));
                }
            output.SetWarp(token.WithRuby, new GridWarp(domain, Columns, 1, points.MoveToImmutable()));
        }
    }

    private static double Envelope(long time, TimeRange range, long riseUs, long settleUs)
    {
        if (time < range.StartUs) return 0;
        if (time < range.EndUs) return riseUs <= 0 ? 1 : Math.Min(1, (time - range.StartUs) / (double)riseUs);
        return settleUs <= 0 ? 0 : Math.Max(0, 1 - (time - range.EndUs) / (double)settleUs);
    }

    /// <summary>覆盖基础字、字族、注音与字簇的墨迹和推进框，并留出描边／阴影余量。</summary>
    private static Rect2 FamilyDomain(TokenGeometrySample token)
    {
        var domain = Bounds(token.BaseGeometry);
        domain = Union(domain, Bounds(token.FamilyGeometry));
        foreach (var ruby in token.Ruby) domain = Union(domain, Bounds(ruby.Geometry));
        foreach (var cluster in token.Clusters) domain = Union(domain, Bounds(cluster.Geometry));
        return new Rect2(domain.Left - MarginPx, domain.Top - MarginPx, domain.Right + MarginPx, domain.Bottom + MarginPx);
    }

    private static Rect2 Bounds(GeometrySample geometry)
    {
        var a = geometry.AdvanceBounds;
        var ink = geometry.InkBounds ?? a;
        return new Rect2(Math.Min(Math.Min(a.Left, ink.Left), geometry.Baseline.X), Math.Min(Math.Min(a.Top, ink.Top), geometry.Baseline.Y),
            Math.Max(Math.Max(a.Right, ink.Right), geometry.Baseline.X), Math.Max(Math.Max(a.Bottom, ink.Bottom), geometry.Baseline.Y));
    }

    private static Rect2 Union(Rect2 a, Rect2 b)
        => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
}
