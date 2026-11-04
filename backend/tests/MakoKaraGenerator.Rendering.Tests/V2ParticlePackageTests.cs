using System.Collections.Immutable;
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

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2ParticlePackageTests
{
    private static readonly string[] TextParameterIds = ["amount"];
    private static readonly string[] TextBindingKinds = ["project"];

    [Theory]
    [InlineData("CursorTrail")]
    [InlineData("VectorTrail")]
    [InlineData("EventBurst")]
    public async Task 真实粒子包的近端三分钟与十分钟乱序重复和重开保持结构与像素(string sample)
    {
        var package = await Package(sample);
        var snapshot = Snapshot([package], [Instance(package, "particles")], distant: true);
        var compiler = Compiler(); var compiled = await Compile(snapshot, compiler);
        int compilationCount = compiler.CompilationCount;
        var factory = Factory(snapshot, compiled);
        await using var session = await factory.CreateAsync(snapshot);
        long[] times = [1_250_000, 180_250_000, 600_250_000, 1_250_000, 2_300_000, 1_250_000];
        List<PreparedRenderFrame> frames = [];
        foreach (long time in times) frames.Add(await Prepare(session, snapshot, time));
        Assert.NotEmpty(frames[0].V2Particles); Assert.NotEmpty(frames[1].V2Particles); Assert.NotEmpty(frames[2].V2Particles);
        string original = Signature(frames[0]); byte[] pixels = Pixels(snapshot, frames[0]);
        Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        foreach (int index in new[] { 3, 5 })
        {
            Assert.Equal(original, Signature(frames[index])); Assert.Equal(pixels, Pixels(snapshot, frames[index]));
        }
        if (sample != "EventBurst")
        {
            Assert.Empty(frames[4].Scene.Lines);
            Assert.NotEmpty(frames[4].V2Particles);
        }
        await using var reopened = await factory.CreateAsync(snapshot);
        var direct = await Prepare(reopened, snapshot, times[0]);
        Assert.Equal(original, Signature(direct)); Assert.Equal(pixels, Pixels(snapshot, direct));
        Assert.Equal(compilationCount, compiler.CompilationCount);
    }

    [Fact]
    public async Task 最终父组整行字族与Ruby合成后出生且两个文字实例的位移只应用一次()
    {
        var cursor = await Package("CursorTrail");
        var initial = Snapshot([cursor], [Instance(cursor, "particles", "{\"rate\":2,\"lifeUs\":5000000,\"trailUs\":0}")]);
        string parent = initial.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var text = TextPackage(parent);
        var snapshot = initial with { Effects = new([cursor, text], [initial.Effects.Instances[0],
            Instance(text, "text-a", "{\"amount\":1}"), Instance(text, "text-b", "{\"amount\":1}")]) };
        var baseline = snapshot with { Effects = new([text], [Instance(text, "text-a", "{\"amount\":0}"),
            Instance(text, "text-b", "{\"amount\":0}")]) };
        var compiled = await Compile(snapshot, Compiler()); var plainCompiled = await Compile(baseline, Compiler());
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        await using var plainSession = await Factory(baseline, plainCompiled).CreateAsync(baseline);
        var frame = await Prepare(session, snapshot, 1_750_000);
        var births = Assert.Single(frame.V2ParticleSnapshots.Where(item => item.InstanceId.Value == "particles")).Births.Births;
        Assert.Equal(new long[] { 500_000, 1_000_000, 1_500_000 }, births.Select(item => item.BirthUs));
        foreach (var birth in births)
        {
            var original = await Prepare(plainSession, baseline, birth.BirthUs);
            var source = Assert.IsType<ParticleSource.Cursor>(birth.Source);
            var anchor = Assert.Single(original.Playback.GetCursors(CursorAnchorMode.AdvanceCenter), item => item.Identity == source.Identity).Position;
            // Each text instance adds (11,7) to the family, (13,17) to the line and (19,23) to one ancestor.
            Close(new(anchor.X + 86, anchor.Y + 94), birth.BirthPosition);
        }
        var plainFrame = await Prepare(plainSession, baseline, frame.TimeUs);
        var ruby = frame.FinalGeometry.SelectMany(item => item.Ruby).ToArray();
        Assert.NotEmpty(ruby);
        foreach (var item in ruby)
        {
            var original = Assert.Single(plainFrame.FinalGeometry.SelectMany(token => token.Ruby), value => value.RubyId == item.RubyId);
            Assert.Equal(original.Geometry.WorldInkCorners.Length, item.Geometry.WorldInkCorners.Length);
            for (int index = 0; index < item.Geometry.WorldInkCorners.Length; index++)
            {
                var point = original.Geometry.WorldInkCorners[index];
                Close(new(point.X + 86, point.Y + 94), item.Geometry.WorldInkCorners[index]);
            }
        }
    }

    [Fact]
    public async Task Bridge使用真实End减一和未来Start最终端点且Jump不制造飞行头()
    {
        var package = await Package("CursorTrail");
        var bridge = Snapshot([package], [Instance(package, "bridge",
            "{\"rate\":10,\"transition\":\"bridge\",\"bridgeUs\":400000,\"trailUs\":0}")], bridge: true);
        var jump = bridge with { Effects = new([package], [Instance(package, "jump", "{\"rate\":10,\"trailUs\":0}")]) };
        var bridgeCompiled = await Compile(bridge, Compiler()); var jumpCompiled = await Compile(jump, Compiler());
        await using var bridgeSession = await Factory(bridge, bridgeCompiled).CreateAsync(bridge);
        await using var jumpSession = await Factory(jump, jumpCompiled).CreateAsync(jump);
        var frame = await Prepare(bridgeSession, bridge, 1_200_000);
        var birth = Assert.Single(Assert.Single(frame.V2ParticleSnapshots).Births.Births,
            item => item.BirthUs == 1_200_000 && item.Source is ParticleSource.Bridge);
        var source = Assert.IsType<ParticleSource.Bridge>(birth.Source);
        Assert.Equal(new Scripting.TimeRange(1_000_000, 1_400_000), source.FlightRange);
        var previous = await Prepare(jumpSession, jump, 999_999);
        var future = await Prepare(jumpSession, jump, 1_400_000);
        var from = Assert.Single(previous.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)).Position;
        var to = Assert.Single(future.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)).Position;
        Close(new((from.X + to.X) / 2, (from.Y + to.Y) / 2), birth.BirthPosition);
        Assert.True(frame.V2AnchorStatistics.FutureEndpoints > 0);
        var jumped = await Prepare(jumpSession, jump, 1_200_000);
        Assert.DoesNotContain(Assert.Single(jumped.V2ParticleSnapshots).Births.Births, item => item.Source is ParticleSource.Bridge);
        Assert.Empty(jumped.Playback.Cursors);
    }

    [Theory]
    [InlineData("ribbon")]
    [InlineData("segments")]
    [InlineData("sprites")]
    public async Task Custom完整出生窗包含死亡记录且拖尾运动和owningSprite使用独立预期(string mode)
    {
        var package = await Package("VectorTrail");
        var snapshot = Snapshot([package], [Instance(package, "custom", "{\"mode\":\"" + mode + "\",\"lifeUs\":100000}")]);
        var compiled = await Compile(snapshot, Compiler());
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await Prepare(session, snapshot, 1_250_000);
        var particles = Assert.Single(frame.V2ParticleSnapshots);
        Assert.True(particles.Births.Births.Length > particles.Alive.Length);
        Assert.Contains(particles.Births.Births, item => item.DeathUs <= frame.TimeUs);
        Assert.Equal(new BirthWindow(0, frame.TimeUs), particles.Births.Window);
        Assert.DoesNotContain(frame.OverlayBatches, batch => batch.Id.Value == "cursor");
        var markers = Assert.Single(frame.DotBatches, batch => batch.Id.Value == "births");
        Assert.Equal(particles.Births.Births.Length, markers.Dots.Length);
        Assert.Equal(particles.Alive.Length, Assert.Single(frame.DotBatches, batch => batch.Id.Value == "heads").Dots.Length);
        foreach (var trail in particles.Trails)
        {
            var particle = Assert.Single(particles.Alive, item => item.Id == trail.Id);
            Assert.NotEmpty(trail.Samples);
            Assert.Equal(frame.TimeUs, trail.Samples[^1].TimeUs);
            foreach (var point in trail.Samples)
            {
                double seconds = (point.TimeUs - particle.BirthUs) / 1_000_000d;
                Close(new(particle.Birth.BirthPosition.X + 40 * seconds,
                    particle.Birth.BirthPosition.Y - 25 * seconds + 10 * seconds * seconds), point.Position);
            }
        }
        if (mode == "ribbon") Assert.NotEmpty(Assert.Single(frame.OverlayBatches.OfType<TrailBatch>()).Trails);
        if (mode == "segments") Assert.NotEmpty(Assert.Single(frame.OverlayBatches.OfType<SegmentBatch>()).Segments);
        if (mode == "sprites")
        {
            var sprites = Assert.Single(frame.OverlayBatches.OfType<SpriteBatch>()).Sprites;
            Assert.NotEmpty(sprites);
            Assert.All(sprites, sprite =>
            {
                Assert.Equal(package.PackageId, sprite.Image.Identity.PackageId); Assert.Equal(package.Revision, sprite.Image.Identity.Revision);
                Assert.Equal("assets/spark.png", sprite.Image.Identity.ExportId);
                Assert.True(sprite.UnitToWorld.A < 0 && sprite.UnitToWorld.B > 0 && sprite.UnitToWorld.D > 0);
                Assert.True(sprite.UnitToWorld.TryInverse(out _));
            });
        }
    }

    [Fact]
    public async Task 冷热与超出memo容量后重算仍精确且取消不污染下一次查询()
    {
        var package = await Package("CursorTrail");
        var snapshot = Snapshot([package], [Instance(package, "memo", "{\"rate\":120,\"lifeUs\":100000,\"trailUs\":0}")], longLine: true);
        var compiled = await Compile(snapshot, Compiler()); var factory = Factory(snapshot, compiled);
        await using var session = await factory.CreateAsync(snapshot);
        var cold = await Prepare(session, snapshot, 5_000_000);
        var warm = await Prepare(session, snapshot, 5_000_001);
        Assert.True(cold.V2AnchorStatistics.MemoMisses > 0); Assert.True(warm.V2AnchorStatistics.MemoHits > 0);
        Assert.Equal(JsonSerializer.Serialize(Assert.Single(cold.V2ParticleSnapshots).Births.Births),
            JsonSerializer.Serialize(Assert.Single(warm.V2ParticleSnapshots).Births.Births));
        int misses = 0;
        foreach (long time in new long[] { 10_000_000, 15_000_000, 20_000_000, 25_000_000 })
            misses += (await Prepare(session, snapshot, time)).V2AnchorStatistics.MemoMisses;
        Assert.True(misses > ParticleLimits.MaximumMemoEntries);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await session.PrepareAsync(5_000_002,
            OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), cancellation.Token));
        var evicted = await Prepare(session, snapshot, 5_000_002);
        Assert.True(evicted.V2AnchorStatistics.MemoMisses > 0);
        await using var reopened = await factory.CreateAsync(snapshot);
        var direct = await Prepare(reopened, snapshot, 5_000_002);
        Assert.Equal(Signature(direct), Signature(evicted)); Assert.Equal(Pixels(snapshot, direct), Pixels(snapshot, evicted));
    }

    [Fact]
    public async Task Custom晚期绘制预算失败归属于实际包且健康独立会话仍可读取()
    {
        var package = await Package("VectorTrail"); var snapshot = Snapshot([package], [Instance(package, "budget")]);
        var compiled = await Compile(snapshot, Compiler());
        var limited = new PureFrameSourceFactory(compiled.Effects, snapshot.EffectInputsFingerprint!,
            static () => new SkiaRenderPipeline(), visualBudget: new(1));
        await using var session = await limited.CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await Prepare(session, snapshot, 1_250_000));
        Assert.Equal("VisualOutputBudgetExceeded", error.Code); Assert.Equal("budget", error.InstanceId.Value);
        Assert.Equal(package.PackageId, error.PackageIdentity!.PackageId); Assert.Equal(package.Revision, error.PackageIdentity.Revision);
        await using var healthy = await Factory(snapshot, compiled).CreateAsync(snapshot);
        Assert.NotEmpty((await Prepare(healthy, snapshot, 1_250_000)).V2Particles);
    }

    private static Task<EffectPackageContents> Package(string name) => EffectPackageStorage.ReadSupportedDirectoryAsync(
        Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", name));
    private static ProjectEffectInstance Instance(EffectPackageContents package, string id, string parameters = "{}") => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision,
        ExportId = Assert.Single(EffectPackageDefinition.Read(package).Controllers).Id, Seed = 17,
        Parameters = EffectParameterOverrides.Parse(parameters), Subscription = new("project"),
    };
    private static ProjectSnapshot Snapshot(EffectPackageContents[] packages, ProjectEffectInstance[] instances,
        bool distant = false, bool bridge = false, bool longLine = false)
    {
        var rows = new List<LyricLine>();
        if (longLine) rows.Add(Row("long", 0, 601_000_000));
        else if (bridge) { rows.Add(Row("from", 0, 1_000_000)); rows.Add(Row("to", 1_400_000, 2_400_000)); }
        else
        {
            rows.Add(Row("near", 0, 2_000_000));
            if (distant) { rows.Add(Row("minute", 180_000_000, 182_000_000)); rows.Add(Row("far", 600_000_000, 602_000_000)); }
        }
        var project = Project.Create("M08 real particle packages", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 603_000_000),
            LyricsDocument.Create(rows), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 40,
                SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { Effects = new(packages, instances), EnterAnimation = null });
    }
    private static LyricLine Row(string id, long start, long end)
    {
        long middle = start + (end - start) / 2;
        return LyricLine.Create(id, "AB", new(start, end), TimingStatus.Edited,
            [LyricToken.Create(id + "-a", "A", new(start, middle), TimingStatus.Edited),
                LyricToken.Create(id + "-b", "B", new(middle, end), TimingStatus.Edited)])
            with { RubyAnnotations = [RubyAnnotation.Create(id + "-ruby", id + "-a", "a")] };
    }
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot, ScriptCompiler compiler)
    {
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler, new PackageImageValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(package => package.Artifact.Diagnostics)));
        return compiled;
    }
    private static PureFrameSourceFactory Factory(ProjectSnapshot snapshot, CompiledProjectEffects compiled)
        => new(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline());
    private static ValueTask<PreparedRenderFrame> Prepare(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long time)
        => session.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    private static string Signature(PreparedRenderFrame frame) => JsonSerializer.Serialize(new
    {
        frame.V2ParticleSnapshots,
        Batches = frame.OverlayBatches.Select(batch => JsonSerializer.Serialize(batch, batch.GetType())).ToArray(),
    });
    private static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame frame)
    {
        using SkiaRenderPipeline renderer = new();
        using var pixels = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { TransparentBackground = true, PreparedFrame = frame });
        return pixels.PixelMemory.ToArray();
    }
    private static void Close(Point2 expected, Point2 actual)
    { Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1e-7); Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1e-7); }

    private static EffectPackageContents TextPackage(string parent)
    {
        string code = """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record TextData(double Amount) : PreparedEffect;
            public sealed class TextOffsets : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => new TextData(source.Parameters.GetNumber("amount"));
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    double m = ((TextData)data).Amount;
                    foreach (var token in frame.Geometry) output.AppendTransform(token.WithRuby, Affine2.Translation(11*m, 7*m));
                    foreach (var line in frame.LineGeometry) output.AppendTransform(line.Target, Affine2.Translation(13*m, 17*m));
                    foreach (var group in frame.GroupGeometry)
                        if (group.Target.NodeId.Value == "PARENT") output.AppendTransform(group.Target, Affine2.Translation(19*m, 23*m));
                }
            }
            """.Replace("PARENT", parent, StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(code);
        var capabilities = new[] { "token.affine", "line.affine", "group.affine" };
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            packageVersion = 3, packageId = "makokara.m08.test-final-text", name = "M08 final text fixture", version = "2.0.0",
            sdkVersion = "2.0.0", shaderProfile = "skia-ganesh-3.119.4-v2", requires = capabilities,
            lineExit = new { durationUs = 0 }, emitters = Array.Empty<object>(),
            files = new[] { new { path = "src/TextOffsets.cs", role = "csharp", sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } },
            parameters = new[] { new { id = "amount", type = "number", label = "Amount", @default = 1, min = 0, max = 1 } },
            exports = new[] { new { id = "text", kind = "controller", name = "Text offsets", entryType = "TextOffsets", parameters = TextParameterIds,
                bindingKinds = TextBindingKinds, outputs = new[] { ("family", "tokenFamily"), ("line", "line"), ("parent", "group") }
                    .Select(item => new { id = item.Item1, kind = "VisualTransform", target = item.Item2, requires = Array.Empty<string>(), dependsOn = Array.Empty<string>() }).ToArray() } },
        });
        return EffectPackageContents.CreateSupported(manifest, [KeyValuePair.Create("src/TextOffsets.cs", (ReadOnlyMemory<byte>)bytes)]);
    }
}
