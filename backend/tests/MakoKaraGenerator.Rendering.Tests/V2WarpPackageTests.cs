using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2WarpPackageTests
{
    private const long TimeUs = 1_250_000;
    private static readonly string[] ParameterIds = ["target", "dx", "dy", "writes", "clear", "badDomain", "cells", "vary"];
    private static readonly string[] BindingKinds = ["project"];
    private static readonly string[] WarpCapabilities = ["token.warp", "cluster.warp", "mark.warp"];

    [Theory]
    [InlineData(0)] // token family
    [InlineData(1)] // base token
    [InlineData(2)] // base cluster
    [InlineData(3)] // text mark
    public async Task 真实v3包经Declare与SetWarp后任意时刻重复乱序和重开保持几何及像素(int target)
    {
        var package = Package();
        var snapshot = Snapshot([package], [Instance(package, "warp", new { target, vary = 1 })], mark: target == 3);
        var compiler = Compiler();
        var compiled = await Compile(snapshot, compiler);
        int compilationCount = compiler.CompilationCount;
        var factory = Factory(snapshot, compiled);
        await using var session = await factory.CreateAsync(snapshot);
        long[] times = target == 3
            ? [10_000_000, 9_250_000, 10_250_000, 10_000_000, 9_500_000, 10_250_000, 10_000_000, 9_250_000]
            : [TimeUs, 180_250_000, 600_250_000, TimeUs, 2_750_000, 600_250_000, TimeUs, 180_250_000];
        List<PreparedRenderFrame> frames = [];
        foreach (long time in times)
        {
            var frame = await Prepare(session, snapshot, time);
            var binding = Assert.Single(frame.TextWarps);
            AssertOwner(binding, compiled, "warp");
            Assert.NotNull(binding.Value);
            Assert.Equal(target, binding.Target switch
            {
                TextTarget.Family => 0, TextTarget.Base => 1, TextTarget.Cluster => 2, TextTarget.Mark => 3, _ => -1,
            });
            frames.Add(frame);
        }
        using var renderer = Renderer();
        byte[][] pixels = [.. frames.Select(frame => Pixels(renderer, snapshot, frame))];
        Assert.All(pixels, image => Assert.Contains(image.Where((_, index) => index % 4 == 3), alpha => alpha > 0));
        Assert.NotEqual(frames[0].TextWarps[0].Value, frames[4].TextWarps[0].Value);
        Assert.True(DifferentPixels(pixels[0], Pixels(renderer, snapshot, Baseline(frames[0]))) > 20,
            "真实包输出的网格没有改变文字像素。");
        foreach (var (original, repeated) in new[] { (0, 3), (2, 5), (0, 6), (1, 7) })
        {
            Assert.Equal(Signature(frames[original]), Signature(frames[repeated]));
            Assert.Equal(pixels[original], pixels[repeated]);
        }
        await using var reopened = await factory.CreateAsync(snapshot);
        var direct = await Prepare(reopened, snapshot, times[0]);
        Assert.Equal(Signature(frames[0]), Signature(direct));
        Assert.Equal(pixels[0], Pixels(renderer, snapshot, direct));
        Assert.Equal(compilationCount, compiler.CompilationCount);
    }

    [Fact]
    public async Task 后实例写入null明确关闭前实例网格并保留关闭写入的真实归属()
    {
        var package = Package();
        var snapshot = Snapshot([package], [Instance(package, "first"),
            Instance(package, "close", new { writes = 0, clear = 1 })]);
        var compiled = await Compile(snapshot, Compiler());
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await Prepare(session, snapshot, TimeUs);
        var binding = Assert.Single(frame.TextWarps);
        Assert.Null(binding.Value);
        AssertOwner(binding, compiled, "close");
        Assert.All(frame.FinalGeometry, token => Assert.Null(token.BaseGeometry.Mapping));
        Assert.Equal(Pixels(snapshot, Baseline(frame)), Pixels(snapshot, frame));
    }

    [Fact]
    public async Task 被覆盖的域错不发布而最终域错属于最后写入的真实包修订及产物()
    {
        var first = Package("earlier");
        var last = Package("later");
        var compiler = Compiler();
        var healthy = Snapshot([first, last], [Instance(first, "earlier", new { badDomain = 1 }), Instance(last, "later")]);
        var compiled = await Compile(healthy, compiler);
        Assert.NotEqual(compiled.Effects[0].RuntimeHash, compiled.Effects[1].RuntimeHash);
        await using var healthySession = await Factory(healthy, compiled).CreateAsync(healthy);
        var frame = await Prepare(healthySession, healthy, TimeUs);
        AssertOwner(Assert.Single(frame.TextWarps), compiled, "later");
        Assert.True(DifferentPixels(Pixels(healthy, Baseline(frame)), Pixels(healthy, frame)) > 20);

        var invalid = healthy with { Effects = new([first, last],
            [Instance(first, "earlier"), Instance(last, "later", new { badDomain = 1 })]) };
        var invalidCompiled = await Compile(invalid, compiler);
        await using var invalidSession = await Factory(invalid, invalidCompiled).CreateAsync(invalid);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await Prepare(invalidSession, invalid, TimeUs));
        AssertFailure(error, invalidCompiled, "later", "WarpOutsideDomain");
        // A failed transaction cannot poison an independently created healthy session.
        await using var recovered = await Factory(healthy, compiled).CreateAsync(healthy);
        Assert.Equal(Signature(frame), Signature(await Prepare(recovered, healthy, TimeUs)));
    }

    [Theory]
    [InlineData(127, 128, 1, 1)] // 256 writes, including the final null, then 257.
    [InlineData(1, 38, 0, 32)] // 39 dense grids fit; the 40th crosses the validation-byte budget.
    public async Task 全实例全部覆盖写入及null计入帧预算且超限归于实际跨线实例(int firstWrites, int lastWrites, int clear, int cells)
    {
        var first = Package("budget-first");
        var last = Package("budget-last");
        var compiler = Compiler();
        var healthy = Snapshot([first, last], [Instance(first, "first", new { writes = firstWrites, cells }),
            Instance(last, "last", new { writes = lastWrites, clear, cells })]);
        var compiled = await Compile(healthy, compiler);
        await using var session = await Factory(healthy, compiled).CreateAsync(healthy);
        var frame = await Prepare(session, healthy, TimeUs);
        var binding = Assert.Single(frame.TextWarps);
        AssertOwner(binding, compiled, "last");
        Assert.Equal(clear == 1, binding.Value is null);

        var invalid = healthy with { Effects = new([first, last], [healthy.Effects.Instances[0],
            Instance(last, "last", new { writes = lastWrites + 1, clear, cells })]) };
        var invalidCompiled = await Compile(invalid, compiler);
        await using var invalidSession = await Factory(invalid, invalidCompiled).CreateAsync(invalid);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await Prepare(invalidSession, invalid, TimeUs));
        AssertFailure(error, invalidCompiled, "last", "WarpGeometryBudgetExceeded");
    }

    [Fact]
    public async Task 字族基础字与字簇网格合成后的最终cursor和真实粒子出生锚点只应用各层一次()
    {
        var warp = Package();
        var cursor = await EffectPackageStorage.ReadSupportedDirectoryAsync(
            Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "CursorTrail"));
        var snapshot = Snapshot([cursor, warp], [Instance(cursor, "cursor", new { rate = 2, lifeUs = 5_000_000, trailUs = 0 }),
            Instance(warp, "warp", new { target = 4, dx = 1 })]);
        var compiled = await Compile(snapshot, Compiler());
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await Prepare(session, snapshot, TimeUs);
        Assert.Equal(3, frame.TextWarps.Length);
        Assert.All(frame.TextWarps, binding => AssertOwner(binding, compiled, "warp"));
        var source = Assert.Single(frame.HierarchyScene.TokenGeometries);
        var final = Assert.Single(frame.FinalGeometry);
        var baseCluster = Assert.Single(source.Clusters, cluster => cluster.Target.RubyId is null);
        var finalCluster = Assert.Single(final.Clusters, cluster => cluster.Target == baseCluster.Target);
        var center = baseCluster.Geometry.AdvanceBounds.Center;
        Close(source.BaseGeometry.BaseWorldMatrix.Map(new(center.X + 43, center.Y + 47)), finalCluster.Geometry.MapPoint(center));
        foreach (var ruby in source.Ruby)
        {
            var point = ruby.Geometry.AdvanceBounds.Center;
            Close(ruby.Geometry.BaseWorldMatrix.Map(new(point.X + 11, point.Y + 7)),
                Assert.Single(final.Ruby, item => item.RubyId == ruby.RubyId).Geometry.MapPoint(point));
        }
        foreach (var mode in new[] { CursorAnchorMode.Baseline, CursorAnchorMode.AdvanceCenter })
        {
            var position = Assert.Single(frame.Playback.GetCursors(mode));
            var local = source.ProgressPath!.At(position.Progress, mode);
            Close(source.BaseGeometry.BaseWorldMatrix.Map(new(local.X + 43, local.Y + 47)), position.Position);
            Close(position.Position, final.MapPoint(local));
        }
        var births = Assert.Single(frame.V2ParticleSnapshots, item => item.InstanceId.Value == "cursor").Births.Births;
        Assert.Equal(new long[] { 500_000, 1_000_000 }, births.Select(birth => birth.BirthUs));
        using var geometry = Renderer();
        foreach (var birth in births)
        {
            var identity = Assert.IsType<ParticleSource.Cursor>(birth.Source).Identity;
            var scene = geometry.GetSceneFrame(snapshot, birth.BirthUs);
            var token = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId == identity.TokenId
                && item.WithRuby.BindingId == identity.BindingId);
            var plain = Assert.Single(PlaybackProjection.Create(snapshot, scene, scene.TokenGeometries)
                .GetCursors(CursorAnchorMode.AdvanceCenter), item => item.Identity == identity);
            var local = token.ProgressPath!.At(plain.Progress, CursorAnchorMode.AdvanceCenter);
            Close(token.BaseGeometry.BaseWorldMatrix.Map(new(local.X + 43, local.Y + 47)), birth.BirthPosition);
        }
        Assert.Contains(Pixels(snapshot, frame).Where((_, index) => index % 4 == 3), alpha => alpha > 0);
    }

    [Fact]
    public void 手工CPU像素参照不授予生产v2准入且未启用Warp包保留CPU路径()
    {
        var package = Package();
        var snapshot = Snapshot([package], [Instance(package, "warp")]);
        Assert.True(ScriptBackendRequirement.RequiresVulkan(snapshot));
        Assert.Equal("PackageBackendUnsupported", Assert.Throws<EffectPackageException>(
            () => ScriptBackendRequirement.RequireSupported(snapshot, false)).Code);
        ScriptBackendRequirement.RequireSupported(snapshot, true);
        var disabled = snapshot with { Effects = new([package],
            snapshot.Effects.Instances.Select(instance => instance with { Enabled = false })) };
        Assert.False(ScriptBackendRequirement.RequiresVulkan(disabled));
        ScriptBackendRequirement.RequireSupported(disabled, false);
        var unused = snapshot with { Effects = new([package], []) };
        Assert.False(ScriptBackendRequirement.RequiresVulkan(unused));
        ScriptBackendRequirement.RequireSupported(unused, false);
    }

    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot, ScriptCompiler compiler)
    {
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler, new PackageImageValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(package => package.Artifact.Diagnostics)));
        return compiled;
    }
    private static PureFrameSourceFactory Factory(ProjectSnapshot snapshot, CompiledProjectEffects compiled)
        => new(compiled.Effects, snapshot.EffectInputsFingerprint!, Renderer);
    private static ValueTask<PreparedRenderFrame> Prepare(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long time)
        => session.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    private static SkiaRenderPipeline Renderer()
    {
        // CPU is only a manually driven pixel reference here. Production admission remains Vulkan-only.
        var device = SkiaRenderDeviceFactory.Create(SkiaRenderBackendOptions.Parse(
            Environment.GetEnvironmentVariable("MAKO_WARP_TEST_BACKEND")));
        return new SkiaRenderPipeline(null, null, null, null, false, device, true);
    }
    private static PreparedRenderFrame Baseline(PreparedRenderFrame frame)
        => new(frame.Snapshot, frame.TimeUs, frame.OutputMode, frame.Target, frame.HierarchyScene, [], [], []);
    private static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame frame)
    {
        using var renderer = Renderer();
        return Pixels(renderer, snapshot, frame);
    }
    private static byte[] Pixels(SkiaRenderPipeline renderer, ProjectSnapshot snapshot, PreparedRenderFrame frame)
    {
        using var pixels = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs)
            with { TransparentBackground = true, PreparedFrame = frame });
        return pixels.PixelMemory.ToArray();
    }
    private static int DifferentPixels(byte[] first, byte[] second)
    {
        Assert.Equal(first.Length, second.Length);
        int different = 0;
        for (int offset = 0; offset < first.Length; offset += 4)
            if (!first.AsSpan(offset, 4).SequenceEqual(second.AsSpan(offset, 4))) different++;
        return different;
    }
    private static string Signature(PreparedRenderFrame frame) => JsonSerializer.Serialize(new
    {
        Warps = frame.TextWarps.Select(binding => new
        { binding.InstanceId, binding.ArtifactHash, binding.PackageIdentity, Target = binding.Target.ToString(), binding.Value }),
        Tokens = frame.FinalGeometry.Select(token => new
        {
            token.WithRuby, Base = token.BaseGeometry.WorldInkCorners, Family = token.FamilyGeometry.WorldInkCorners,
            Clusters = token.Clusters.Select(cluster => new { cluster.Target, Ink = cluster.Geometry.WorldInkCorners }),
        }),
        Marks = frame.FinalMarkGeometry.Select(mark => new { mark.Target, Ink = mark.Geometry.WorldInkCorners }),
        Cursors = frame.Playback.GetCursors(CursorAnchorMode.AdvanceCenter),
    });
    private static void AssertOwner(PreparedTextWarpBinding binding, CompiledProjectEffects compiled, string instance)
    {
        var expected = Assert.Single(compiled.Effects, effect => effect.Id.Value == instance);
        Assert.Equal(expected.Id, binding.InstanceId);
        Assert.Equal(expected.RuntimeHash, binding.ArtifactHash);
        Assert.NotNull(binding.PackageIdentity);
        Assert.Equal(expected.PackageIdentity, binding.PackageIdentity);
    }
    private static void AssertFailure(EffectFailureException error, CompiledProjectEffects compiled, string instance, string code)
    {
        var expected = Assert.Single(compiled.Effects, effect => effect.Id.Value == instance);
        Assert.Equal(code, error.Code);
        Assert.Equal("ComposeOutputs", error.Stage);
        Assert.Equal(TimeUs, error.TimeUs);
        Assert.Equal(expected.Id, error.InstanceId);
        Assert.Equal(expected.RuntimeHash, error.ArtifactHash);
        Assert.NotNull(error.PackageIdentity);
        Assert.Equal(expected.PackageIdentity, error.PackageIdentity);
    }
    private static void Close(Point2 expected, Point2 actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1e-4);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1e-4);
    }
    private static ProjectEffectInstance Instance(EffectPackageContents package, string id, object? parameters = null) => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision,
        ExportId = Assert.Single(EffectPackageDefinition.Read(package).Controllers).Id, Seed = 17,
        Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(parameters ?? new { })), Subscription = new("project"),
    };
    private static ProjectSnapshot Snapshot(EffectPackageContents[] packages, ProjectEffectInstance[] instances, bool mark = false)
    {
        var line = LyricLine.Create("line", "A", new(0, 602_000_000), TimingStatus.Edited,
            [LyricToken.Create("a", "A", new(0, 602_000_000), TimingStatus.Edited)]) with
        { RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "x")] };
        var project = mark ? LeadInFixture.Create() : Project.Create("M09 real Warp package",
            Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 603_000_000), LyricsDocument.Create([line]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 64,
                SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { Effects = new(packages, instances), EnterAnimation = null });
    }
    private static EffectPackageContents Package(string suffix = "main")
    {
        byte[] bytes = Encoding.UTF8.GetBytes("// " + suffix + "\n" + ControllerSource);
        var parameters = new[]
        {
            ("target", 0d, 0d, 4d), ("dx", 13d, -100d, 100d), ("dy", 7d, -100d, 100d),
            ("writes", 1d, 0d, 257d), ("clear", 0d, 0d, 1d), ("badDomain", 0d, 0d, 1d),
            ("cells", 1d, 1d, 32d), ("vary", 0d, 0d, 1d),
        };
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            packageVersion = 3, packageId = "makokara.m09.test-warp-" + suffix, name = "M09 real Warp fixture", version = "2.0.0",
            sdkVersion = "2.0.0", shaderProfile = "skia-ganesh-3.119.4-v2", requires = WarpCapabilities,
            lineExit = new { durationUs = 0 }, emitters = Array.Empty<object>(),
            files = new[] { new { path = "src/WarpEffect.cs", role = "csharp", sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } },
            parameters = parameters.Select(value => new
            { id = value.Item1, type = "number", label = value.Item1, @default = value.Item2, min = value.Item3, max = value.Item4 }),
            exports = new[] { new { id = "warp", kind = "controller", name = "Warp fixture", entryType = "WarpEffect",
                parameters = ParameterIds, bindingKinds = BindingKinds,
                outputs = new[] { ("family", "tokenFamily"), ("base", "baseToken"), ("cluster", "cluster"), ("mark", "textMark") }
                    .Select(value => new { id = value.Item1, kind = "VisualTransform", target = value.Item2,
                        requires = Array.Empty<string>(), dependsOn = Array.Empty<string>() }) } },
        });
        return EffectPackageContents.CreateSupported(manifest,
            [KeyValuePair.Create("src/WarpEffect.cs", (ReadOnlyMemory<byte>)bytes)]);
    }

    private const string ControllerSource = """
        using System;
        using System.Collections.Immutable;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;

        public sealed record WarpData(double Target, double Dx, double Dy, double Writes, double Clear,
            double BadDomain, double Cells, double Vary) : PreparedEffect;

        public sealed class WarpEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => new WarpData(
                source.Parameters.GetNumber("target"), source.Parameters.GetNumber("dx"), source.Parameters.GetNumber("dy"),
                source.Parameters.GetNumber("writes"), source.Parameters.GetNumber("clear"), source.Parameters.GetNumber("badDomain"),
                source.Parameters.GetNumber("cells"), source.Parameters.GetNumber("vary"));

            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                var d = (WarpData)data;
                if (d.Target == 4)
                {
                    foreach (var token in frame.Geometry)
                    {
                        output.SetWarp(token.WithRuby, Mesh(FamilyDomain(token), 11*d.Dx, 7*d.Dx, 1));
                        output.SetWarp(token.BaseOnly, Mesh(Domain(token.BaseGeometry), 13*d.Dx, 17*d.Dx, 1));
                        foreach (var cluster in token.Clusters)
                            if (cluster.Target.RubyId is null)
                                output.SetWarp(cluster.Target, Mesh(Domain(cluster.Geometry), 19*d.Dx, 23*d.Dx, 1));
                    }
                    return;
                }
                TextTarget? target = null;
                Rect2 domain = default;
                if (d.Target == 3)
                {
                    foreach (var mark in frame.MarkGeometry)
                    {
                        target = new TextTarget.Mark(mark.Target);
                        domain = Domain(mark.Geometry);
                        break;
                    }
                }
                else
                {
                    foreach (var token in frame.Geometry)
                    {
                        if (d.Target == 0) { target = new TextTarget.Family(token.WithRuby); domain = FamilyDomain(token); }
                        if (d.Target == 1) { target = new TextTarget.Base(token.BaseOnly); domain = Domain(token.BaseGeometry); }
                        if (d.Target == 2)
                            foreach (var cluster in token.Clusters)
                                if (cluster.Target.RubyId is null)
                                {
                                    target = new TextTarget.Cluster(cluster.Target);
                                    domain = Domain(cluster.Geometry);
                                    break;
                                }
                        break;
                    }
                }
                if (target is null) return;
                if (d.BadDomain > 0) domain = new Rect2(0, 0, 1, 1);
                double dx = d.Dx + (d.Vary > 0 ? frame.TimeUs % 1000000 / 100000d : 0);
                var warp = Mesh(domain, dx, d.Dy, (int)d.Cells);
                for (int index = 0; index < (int)d.Writes; index++) output.SetWarp(target, warp);
                if (d.Clear > 0) output.SetWarp(target, null);
            }

            private static GridWarp Mesh(Rect2 domain, double dx, double dy, int cells)
            {
                var points = ImmutableArray.CreateBuilder<Point2>((cells + 1)*(cells + 1));
                for (int row = 0; row <= cells; row++)
                for (int column = 0; column <= cells; column++)
                    points.Add(new Point2(domain.Left + domain.Width*column/cells + dx,
                        domain.Top + domain.Height*row/cells + dy));
                return new GridWarp(domain, cells, cells, points.MoveToImmutable());
            }
            private static Rect2 FamilyDomain(TokenGeometrySample token)
            {
                var domain = Union(Domain(token.BaseGeometry), Domain(token.FamilyGeometry));
                foreach (var ruby in token.Ruby) domain = Union(domain, Domain(ruby.Geometry));
                foreach (var cluster in token.Clusters) domain = Union(domain, Domain(cluster.Geometry));
                return domain;
            }
            private static Rect2 Domain(GeometrySample geometry)
            {
                var a = geometry.AdvanceBounds;
                var ink = geometry.InkBounds ?? a;
                // Includes decoration and child displacements without deriving a fake Ruby advance from FamilyGeometry.
                return new Rect2(Math.Min(Math.Min(a.Left, ink.Left), geometry.Baseline.X) - 128,
                    Math.Min(Math.Min(a.Top, ink.Top), geometry.Baseline.Y) - 128,
                    Math.Max(Math.Max(a.Right, ink.Right), geometry.Baseline.X) + 128,
                    Math.Max(Math.Max(a.Bottom, ink.Bottom), geometry.Baseline.Y) + 128);
            }
            private static Rect2 Union(Rect2 a, Rect2 b) => new Rect2(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
                Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
        }
        """;
}
