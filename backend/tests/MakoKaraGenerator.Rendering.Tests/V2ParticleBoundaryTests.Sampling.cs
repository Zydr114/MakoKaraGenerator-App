using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class V2ParticleBoundaryTests
{
    private const long DependencyCutUs = 51_237;

    [Theory]
    [InlineData("group")]
    [InlineData("line")]
    [InlineData("tokenFamily")]
    [InlineData("baseToken")]
    public async Task 第二文字实例的完整依赖断点左右一微秒不跨区段插值(string target)
    {
        var project = SamplingProject();
        string parent = project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var text = TextPackage(SamplingText(DependencyWrite(target, parent), [DependencyCutUs]),
            [target switch { "group" => "group.affine", "line" => "line.affine", _ => "token.affine" }], [target]);
        var emitter = await SamplingEmitter([DependencyCutUs], endUs: 100_000);
        var snapshot = ProjectSnapshot.FromProject(project with { Effects = new([emitter, text],
            [Instance(emitter, "emitter"), Instance(text, "dependency")]) });
        var baseline = ProjectSnapshot.FromProject(project with { Effects = new([text], [Instance(text, "dependency")]) });
        var compiled = await Compile(snapshot); var plainCompiled = await Compile(baseline);
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        await using var plain = await Factory(baseline, plainCompiled).CreateAsync(baseline);
        var frame = await Prepare(session, snapshot, 80_000);
        var births = Assert.Single(frame.V2ParticleSnapshots, item => item.InstanceId.Value == "emitter").Births.Births;
        Assert.Equal([DependencyCutUs - 1, DependencyCutUs, DependencyCutUs + 1], births.Select(item => item.BirthUs));
        foreach (var birth in births)
        {
            var direct = await Prepare(plain, baseline, birth.BirthUs);
            Near(FinalCursor(direct, Assert.IsType<ParticleSource.Cursor>(birth.Source)), birth.BirthPosition);
        }
        Assert.InRange(births[1].BirthPosition.X - births[0].BirthPosition.X, 199.9, 200.1);
        Assert.InRange(frame.V2AnchorStatistics.UniqueTimes, 3, 5);
        Assert.Equal(frame.V2AnchorStatistics.UniqueTimes * 2, frame.V2AnchorStatistics.TextCalls);
    }

    [Fact]
    public async Task 固定网格不跨真实基础字簇切换且Ruby不成为基础播放头身份()
    {
        var project = SamplingProject();
        var initial = ProjectSnapshot.FromProject(project);
        using SkiaRenderPipeline geometry = new();
        var token = Assert.Single(geometry.GetSceneFrame(initial, 0).TokenGeometries);
        var clusters = token.Clusters.Where(item => item.Target.RubyId is null).OrderBy(item => item.Geometry.AdvanceBounds.Left).ToArray();
        Assert.Equal(2, clusters.Length); Assert.Contains(token.Clusters, item => item.Target.RubyId is not null);
        var path = Assert.IsType<TextProgressPath>(token.ProgressPath);
        double edge = clusters[1].Geometry.AdvanceBounds.Left;
        long low = 0, high = 99_999;
        while (low < high)
        {
            long middle = low + (high - low) / 2;
            if (path.At(middle / 100_000d, CursorAnchorMode.Baseline).X >= edge) high = middle;
            else low = middle + 1;
        }
        long cut = low; Assert.InRange(cut, 3, 99_997);
        string code = SamplingText("""
            foreach (var token in frame.Geometry)
                foreach (var cluster in token.Clusters)
                    if (cluster.Target.RubyId is null)
                        output.AppendTransform(new TextTarget.Cluster(cluster.Target),
                            Affine2.Translation(cluster.Target.UnitIndex == FIRST ? 0 : 200, 0));
            """.Replace("FIRST", clusters[0].Target.UnitIndex.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), []);
        var text = TextPackage(code, ["cluster.affine"], ["cluster"]);
        var emitter = await SamplingEmitter([cut], endUs: 100_000);
        var snapshot = ProjectSnapshot.FromProject(project with { Effects = new([emitter, text],
            [Instance(emitter, "emitter"), Instance(text, "clusters")]) });
        var baseline = ProjectSnapshot.FromProject(project with { Effects = new([text], [Instance(text, "clusters")]) });
        var compiled = await Compile(snapshot); var plainCompiled = await Compile(baseline);
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        await using var plain = await Factory(baseline, plainCompiled).CreateAsync(baseline);
        var frame = await Prepare(session, snapshot, 99_999);
        var births = Assert.Single(frame.V2ParticleSnapshots, item => item.InstanceId.Value == "emitter").Births.Births;
        Assert.Equal([cut - 1, cut, cut + 1], births.Select(item => item.BirthUs));
        foreach (var birth in births)
        {
            var direct = await Prepare(plain, baseline, birth.BirthUs);
            var source = Assert.IsType<ParticleSource.Cursor>(birth.Source);
            Near(FinalCursor(direct, source), birth.BirthPosition);
            Assert.Equal("word", source.Identity.TokenId!.Value.Value);
            var changed = Assert.Single(direct.ClusterTransforms);
            Assert.Equal(clusters[1].Target, changed.Target);
            Assert.Equal(Affine2.Translation(200, 0), changed.LocalMatrix);
            var final = Assert.Single(direct.FinalGeometry);
            Assert.Equal(clusters[0].Geometry.BaseWorldMatrix,
                Assert.Single(final.Clusters, item => item.Target == clusters[0].Target).Geometry.BaseWorldMatrix);
            Assert.Equal(clusters[1].Geometry.BaseWorldMatrix.Compose(Affine2.Translation(200, 0)),
                Assert.Single(final.Clusters, item => item.Target == clusters[1].Target).Geometry.BaseWorldMatrix);
            Assert.Equal(token.Ruby.Single().Geometry.BaseWorldMatrix, Assert.Single(final.Ruby).Geometry.BaseWorldMatrix);
        }
        Assert.InRange(births[1].BirthPosition.X - births[0].BirthPosition.X, 199.9, 200.1);
    }

    [Fact]
    public async Task 真实换行装载重入与退出死亡边界不串锚点且已隐藏出生仍完整保留()
    {
        var outer = LyricLine.Create("outer", "AB", new(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("outer-word", "AB", new(0, 2_000_000), TimingStatus.Edited)]);
        var inner = LyricLine.Create("inner", "CD", new(500_000, 700_000), TimingStatus.Edited,
            [LyricToken.Create("inner-word", "CD", new(500_000, 700_000), TimingStatus.Edited)]);
        var template = SamplingProject();
        var project = Project.Create(template.Name, template.Composition, LyricsDocument.Create([outer, inner]),
            lyricStyle: template.LyricStyle);
        var text = WithExit(TextPackage(SamplingText("""
            double shift = frame.Geometry.Length * 100;
            foreach (var token in frame.Geometry) output.AppendTransform(token.WithRuby, Affine2.Translation(shift, 0));
            """, [], endUs: 3_000_000), ["token.affine"], ["tokenFamily"]), 600_000);
        var emitter = await SamplingEmitter([500_000, 700_000, 1_100_000], endUs: 2_000_000);
        var snapshot = ProjectSnapshot.FromProject(project with { Effects = new([emitter, text],
            [Instance(emitter, "emitter"), Instance(text, "exit-dependency")]) });
        var baseline = ProjectSnapshot.FromProject(project with { Effects = new([text], [Instance(text, "exit-dependency")]) });
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var loads = timeline.Events.OfType<BindingEntered>().Where(item => item.Binding.LineId.Value == "outer").ToArray();
        Assert.Equal([0L, 700_000L], loads.Select(item => item.TimeUs));
        Assert.NotEqual(loads[0].Binding.BindingId, loads[1].Binding.BindingId);
        var compiled = await Compile(snapshot); var plainCompiled = await Compile(baseline);
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        await using var plain = await Factory(baseline, plainCompiled).CreateAsync(baseline);
        var frame = await Prepare(session, snapshot, 1_200_000);
        var births = Assert.Single(frame.V2ParticleSnapshots, item => item.InstanceId.Value == "emitter").Births.Births;
        Assert.Equal([499_999L, 500_001L, 699_999L, 700_001L, 1_099_999L, 1_100_000L, 1_100_001L], births.Select(item => item.BirthUs));
        foreach (var birth in births)
        {
            var direct = await Prepare(plain, baseline, birth.BirthUs);
            Near(FinalCursor(direct, Assert.IsType<ParticleSource.Cursor>(birth.Source)), birth.BirthPosition);
        }
        Assert.Equal(loads[0].Binding.BindingId, births[0].Source.BindingId);
        Assert.Equal(loads[1].Binding.BindingId, births[3].Source.BindingId);
        Assert.EndsWith("/reload/700000", births[3].Source.BindingId.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(frame.Scene.Lines, line => line.BindingId == loads[0].Binding.BindingId.Value);
        Assert.InRange(births[5].BirthPosition.X - births[4].BirthPosition.X, -100.1, -99.9);
        Assert.Equal(births.Length, frame.V2Particles.Length);
    }

    private static Project SamplingProject()
    {
        var line = LyricLine.Create("line", "Wi", new(0, 100_000), TimingStatus.Edited,
            [LyricToken.Create("word", "Wi", new(0, 100_000), TimingStatus.Edited)]) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "word", "xy")] };
        return Project.Create("M08 sampling boundaries", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 3_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with
            { FontFamilyName = "DejaVu Sans", FontSizePx = 40, Stroke = null, Shadow = null, Glow = null });
    }

    private static string DependencyWrite(string target, string parent) => "double shift = frame.TimeUs < 51237 ? 0 : 200;\n" + (target switch
    {
        "group" => "foreach (var group in frame.GroupGeometry) if (group.Target.NodeId.Value == \"" + parent
            + "\") output.AppendTransform(group.Target, Affine2.Translation(shift, 0));",
        "line" => "foreach (var line in frame.LineGeometry) output.AppendTransform(line.Target, Affine2.Translation(shift, 0));",
        "baseToken" => "foreach (var token in frame.Geometry) output.AppendTransform(token.BaseOnly, Affine2.Translation(shift, 0));",
        _ => "foreach (var token in frame.Geometry) output.AppendTransform(token.WithRuby, Affine2.Translation(shift, 0));",
    });

    private static string SamplingText(string writes, long[] discontinuities, long endUs = 100_000) => """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record BoundaryData : PreparedEffect;
        public sealed class BoundaryText : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => new BoundaryData
            { GeometrySampling = new(GeometrySamplingMode.Continuous120Hz, [new(0, END)], [CUTS]) };
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                WRITES
            }
        }
        """.Replace("END", endUs.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("CUTS", string.Join(",", discontinuities), StringComparison.Ordinal)
        .Replace("WRITES", writes, StringComparison.Ordinal);

    private static async Task<EffectPackageContents> SamplingEmitter(long[] cuts, long endUs)
    {
        string ranges = string.Join(",", cuts.Select(cut => "new(" + (cut - 2).ToString(CultureInfo.InvariantCulture) + ","
            + (cut + 2).ToString(CultureInfo.InvariantCulture) + ")"));
        string segments = string.Join(",", cuts.Select(cut => "new(new(" + (cut - 2).ToString(CultureInfo.InvariantCulture) + ","
            + (cut + 2).ToString(CultureInfo.InvariantCulture) + "),1000000,1000000)"));
        string source = """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record SamplingData : PreparedEffect;
            public sealed class CursorTrail : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => new SamplingData
                {
                    GeometrySampling = new(GeometrySamplingMode.Continuous120Hz, [new(0, END)], []),
                    Particles = new([new(new("cursor"), new([SEGMENTS], [RANGES]),
                        new ParticleSpec { LifetimeUs = 2000000, SizePx = 4, Color = new(1, 0, 0, 1), SeedStreamKey = "sampling" },
                        CursorAnchorMode.AdvanceCenter, BridgePolicy.Jump)], []),
                };
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """.Replace("END", endUs.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("SEGMENTS", segments, StringComparison.Ordinal).Replace("RANGES", ranges, StringComparison.Ordinal);
        return await ReplacePackageSource(await LoadPackage("CursorTrail"), "src/CursorTrail.cs", source);
    }

    private static EffectPackageContents WithExit(EffectPackageContents package, long durationUs)
    {
        var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["lineExit"]!["durationUs"] = durationUs;
        var requires = manifest["requires"]!.AsArray();
        if (durationUs > 0 && !requires.Any(item => item!.GetValue<string>() == "line.exit")) requires.Add("line.exit");
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    }
    private static void Near(Point2 expected, Point2 actual)
    { Assert.InRange(Math.Abs(expected.X - actual.X), 0, 0.0001); Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 0.0001); }
}
