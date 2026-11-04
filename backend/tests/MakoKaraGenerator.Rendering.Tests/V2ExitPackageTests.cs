using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using V2 = MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2ExitPackageTests
{
    [Fact]
    public async Task 真实退场空基础字外观与显式继承外观路径保持几何像素和来源一致()
    {
        var package = await Package(); var snapshot = Snapshot([package], [Instance(package, "exit")]);
        var compiled = await Compile(snapshot, Compiler());
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        foreach (long time in new long[] { 1_000_001, 1_300_000, 1_599_999, 1_300_000 })
        {
            var frame = await Prepare(session, snapshot, time);
            Assert.Empty(frame.BaseStyles); Assert.NotEmpty(frame.ClusterTransforms);
            Assert.Contains(frame.ClusterTransforms, static item => item.Target.RubyId is null);
            Assert.Contains(frame.ClusterTransforms, static item => item.Target.RubyId is not null);
            var token = frame.HierarchyScene.TokenGeometries.First(token =>
                token.Clusters.Any(cluster => frame.ClusterTransforms.Any(output => output.Target == cluster.Target)));

            // An explicit inherited leaf appearance requires the full source-style
            // projection, while the real sample has only family opacity/cluster writes.
            // Reapplying these partitions starts from HierarchyScene, not FinalGeometry.
            var reference = frame.WithTextPartitions(frame.BaseTransforms,
                [new V2.BaseTokenStyle(token.BaseOnly, new())], frame.ClusterTransforms);
            Assert.Same(frame.Scene, reference.Scene); Assert.Same(frame.HierarchyScene, reference.HierarchyScene);
            Assert.Equal(frame.TokenStyles.ToArray(), reference.TokenStyles.ToArray());
            Assert.Equal(frame.ClusterTransforms.ToArray(), reference.ClusterTransforms.ToArray());
            Assert.Equal(JsonSerializer.Serialize(frame.FinalGeometry), JsonSerializer.Serialize(reference.FinalGeometry));
            Assert.Equal(JsonSerializer.Serialize(frame.FinalGroupGeometry), JsonSerializer.Serialize(reference.FinalGroupGeometry));
            Assert.Equal(JsonSerializer.Serialize(frame.Playback), JsonSerializer.Serialize(reference.Playback));
            Assert.Same(frame.DiagnosticSnapshot, reference.DiagnosticSnapshot);
            Assert.Equal(frame.OverlayOwners.ToArray(), reference.OverlayOwners.ToArray());
            Assert.Equal(frame.V2ParticleSnapshots.ToArray(), reference.V2ParticleSnapshots.ToArray());
            Assert.Same(frame.VisualBudget, reference.VisualBudget);
            var pixels = Pixels(snapshot, frame);
            Assert.Equal(pixels, Pixels(snapshot, reference));
            if (time == 1_300_000)
            {
                Assert.True(Alpha(pixels) > 0);
                Assert.Equal(frame.GetTokenStyle(token.WithRuby), reference.GetBaseDrawingStyle(token.BaseOnly));
            }
        }
    }

    [Fact]
    public async Task 真实字簇退场包在半开边界乱序与重建后保持几何和像素一致()
    {
        var package = await Package();
        Assert.Equal(600_000, package.LineExitDurationUs);
        var snapshot = Snapshot([package], [Instance(package, "exit")]);
        var compiler = Compiler(); var compiled = await Compile(snapshot, compiler);
        int compilationCount = compiler.CompilationCount;
        var factory = Factory(snapshot, compiled);
        await using var session = await factory.CreateAsync(snapshot);
        long[] times = [999_999, 1_000_000, 1_599_999, 1_600_000, 1_300_000, 1_000_001,
            1_300_000, 600_000_000, 999_999, 1_300_000];
        List<PreparedRenderFrame> frames = [];
        foreach (long time in times) frames.Add(await Prepare(session, snapshot, time));
        Assert.DoesNotContain(frames[0].Scene.Lines, line => line.ExitReplica is not null);
        var firstExit = Assert.Single(frames[1].Scene.Lines.Where(line => line.ExitReplica is not null));
        Assert.Equal(1_000_000, firstExit.ExitReplica!.HideTimeUs);
        Assert.Equal("line-0", firstExit.Frame.Line!.Id);
        Assert.Equal(Assert.Single(frames[0].Scene.Lines).BindingId, firstExit.BindingId);
        Assert.Contains(frames[2].Scene.Lines, line => line.ExitReplica is not null && line.Frame.Line!.Id == "line-0");
        Assert.DoesNotContain(frames[3].Scene.Lines, line => line.Frame.Line!.Id == "line-0");
        Assert.Empty(frames[7].Scene.Lines);
        Assert.NotEmpty(frames[4].ClusterTransforms);
        Assert.Contains(frames[4].ClusterTransforms, transform => transform.Target.RubyId is not null);
        Assert.Contains(frames[4].ClusterTransforms, transform => transform.Target.RubyId is null);
        Assert.All(frames[4].TokenStyles, style => Assert.InRange(style.Update.OpacityFactor, .49, .51));
        var pixels = Pixels(snapshot, frames[4]);
        Assert.True(Alpha(pixels) > 0);
        var baseline = new PreparedRenderFrame(snapshot, frames[4].TimeUs, OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), frames[4].Scene, []);
        Assert.False(pixels.AsSpan().SequenceEqual(Pixels(snapshot, baseline)));
        foreach (int position in new[] { 6, 9 })
        {
            Assert.Equal(frames[4].ClusterTransforms.ToArray(), frames[position].ClusterTransforms.ToArray());
            Assert.Equal(frames[4].TokenStyles.ToArray(), frames[position].TokenStyles.ToArray());
            Assert.Equal(pixels, Pixels(snapshot, frames[position]));
        }
        await using var reopened = await factory.CreateAsync(snapshot);
        var direct = await Prepare(reopened, snapshot, 1_300_000);
        Assert.Equal(frames[4].ClusterTransforms.ToArray(), direct.ClusterTransforms.ToArray());
        Assert.Equal(frames[4].TokenStyles.ToArray(), direct.TokenStyles.ToArray());
        Assert.Equal(pixels, Pixels(snapshot, direct));
        Assert.Equal(compilationCount, compiler.CompilationCount);

        using SkiaRenderPipeline renderer = new();
        var live = Assert.Single(renderer.GetSceneFrame(snapshot, 1_300_000).Lines);
        var actual = Assert.Single(frames[4].Scene.Lines.Where(line => line.ExitReplica is null));
        Assert.Equal(live.BindingId, actual.BindingId); Assert.Equal(live.ParentId, actual.ParentId);
        Assert.Equal(live.Geometry!.WorldMatrix, actual.Geometry!.WorldMatrix);
        Assert.Equal(live.Geometry.Pivot, actual.Geometry.Pivot);
    }

    [Fact]
    public async Task 同一包源的长短实例各用自己的夹值进度且短实例不会复活()
    {
        var shortPackage = await Package(100_000); var longPackage = await Package();
        var snapshot = Snapshot([shortPackage, longPackage], [Instance(shortPackage, "short"), Instance(longPackage, "long")]);
        var compiler = Compiler(); var compiled = await Compile(snapshot, compiler);
        int compilationCount = compiler.CompilationCount;
        Assert.NotEqual(shortPackage.Revision, longPackage.Revision);
        Assert.Equal(compiled.Effects[0].Artifact!.ArtifactHash, compiled.Effects[1].Artifact!.ArtifactHash);
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await Prepare(session, snapshot, 1_300_000);
        var replica = Assert.Single(frame.Scene.Lines.Where(line => line.ExitReplica is not null));
        Assert.Equal("line-0", replica.Frame.Line!.Id);
        Assert.All(frame.TokenStyles, style => Assert.Equal(0, style.Update.OpacityFactor));
        Assert.NotEmpty(frame.TokenStyles);
        Assert.NotEmpty(frame.ClusterTransforms);
        Assert.All(frame.ClusterTransforms, transform => Assert.Equal(replica.BindingId, transform.Target.BindingId.Value));
        Assert.DoesNotContain((await Prepare(session, snapshot, 1_600_000)).Scene.Lines, line => line.ExitReplica is not null);
        Assert.Equal(compilationCount, compiler.CompilationCount);
    }

    [Fact]
    public async Task 不同装载的长声明不延长短声明的真实副本()
    {
        var shortPackage = await Package(100_000); var longPackage = await Package();
        var snapshot = Snapshot([shortPackage, longPackage],
            [Instance(shortPackage, "short", "line-0"), Instance(longPackage, "long", "line-1")]);
        var compiled = await Compile(snapshot, Compiler());
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        Assert.Contains((await Prepare(session, snapshot, 1_099_999)).Scene.Lines,
            line => line.Frame.Line!.Id == "line-0" && line.ExitReplica is not null);
        Assert.DoesNotContain((await Prepare(session, snapshot, 1_100_000)).Scene.Lines, line => line.Frame.Line!.Id == "line-0");
        Assert.DoesNotContain((await Prepare(session, snapshot, 1_300_000)).Scene.Lines, line => line.Frame.Line!.Id == "line-0");
        var longFrame = await Prepare(session, snapshot, 2_300_000);
        Assert.Equal("line-1", Assert.Single(longFrame.Scene.Lines).Frame.Line!.Id);
        Assert.NotEmpty(longFrame.ClusterTransforms);
    }

    [Fact]
    public async Task 零声明单独不延寿但其他实例保留副本时按完成态求值()
    {
        var zero = await Package(0); var longPackage = await Package();
        var zeroSnapshot = Snapshot([zero], [Instance(zero, "zero")]);
        var zeroCompiled = await Compile(zeroSnapshot, Compiler());
        await using var zeroSession = await Factory(zeroSnapshot, zeroCompiled).CreateAsync(zeroSnapshot);
        var withoutExit = await Prepare(zeroSession, zeroSnapshot, 1_000_000);
        Assert.DoesNotContain(withoutExit.Scene.Lines, line => line.ExitReplica is not null);
        Assert.Empty(withoutExit.ClusterTransforms); Assert.Empty(withoutExit.TokenStyles);

        var sharedSnapshot = Snapshot([zero, longPackage], [Instance(zero, "zero"), Instance(longPackage, "long")]);
        var sharedCompiled = await Compile(sharedSnapshot, Compiler());
        await using var sharedSession = await Factory(sharedSnapshot, sharedCompiled).CreateAsync(sharedSnapshot);
        var shared = await Prepare(sharedSession, sharedSnapshot, 1_000_000);
        Assert.Single(shared.Scene.Lines.Where(line => line.ExitReplica is not null));
        Assert.NotEmpty(shared.TokenStyles);
        Assert.All(shared.TokenStyles, style => Assert.Equal(0, style.Update.OpacityFactor));
        Assert.NotEmpty(shared.ClusterTransforms);
    }

    [Fact]
    public async Task 三秒寿命上限与取消恢复沿真实包和随机访问路径执行()
    {
        var package = await Package(3_000_000); var snapshot = Snapshot([package], [Instance(package, "maximum")]);
        var compiled = await Compile(snapshot, Compiler());
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await session.PrepareAsync(1_300_000,
            OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), cancellation.Token));
        var inside = await Prepare(session, snapshot, 3_999_999);
        Assert.Contains(inside.Scene.Lines, line => line.Frame.Line!.Id == "line-0" && line.ExitReplica is not null);
        var outside = await Prepare(session, snapshot, 4_000_000);
        Assert.DoesNotContain(outside.Scene.Lines, line => line.Frame.Line!.Id == "line-0");
        Assert.Contains((await Prepare(session, snapshot, 1_300_000)).Scene.Lines, line => line.Frame.Line!.Id == "line-0");
    }

    [Fact]
    public async Task 跨token连字退场按完整行变换且Ruby与基础字保持完整真实像素()
    {
        var package = await Package(); var snapshot = Snapshot([package], [Instance(package, "ligature")], true);
        var compiled = await Compile(snapshot, Compiler());
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await Prepare(session, snapshot, 1_100_000);
        var replica = Assert.Single(frame.Scene.Lines.Where(line => line.ExitReplica is not null));
        var tokens = frame.Scene.TokenGeometries.Where(token => token.WithRuby.BindingId.Value == replica.BindingId).ToArray();
        Assert.Contains(tokens.SelectMany(token => token.Clusters), cluster => cluster.OwnerTokens.Length > 1);
        Assert.Contains(tokens.SelectMany(token => token.Clusters), cluster => cluster.Target.RubyId is not null);
        Assert.Empty(frame.ClusterTransforms); Assert.Empty(frame.TokenStyles);
        var lineTransform = Assert.Single(frame.LineTransforms);
        Assert.Equal(replica.BindingId, lineTransform.Target.BindingId.Value);
        var baseline = new PreparedRenderFrame(snapshot, frame.TimeUs, OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), frame.Scene, []);
        var plain = Pixels(snapshot, baseline); var actual = Pixels(snapshot, frame);
        Assert.True(Alpha(plain) > 0); Assert.True(Alpha(actual) > 0);
        Assert.False(plain.AsSpan().SequenceEqual(actual));
        foreach (var token in tokens)
        {
            var final = frame.FinalGeometry.Single(item => item.WithRuby == token.WithRuby);
            Assert.Equal(token.Clusters.Length, final.Clusters.Length);
            Assert.All(final.Clusters, cluster => Assert.Equal(final.BaseGeometry.BaseWorldMatrix, cluster.Geometry.BaseWorldMatrix));
        }
    }

    private static async Task<EffectPackageContents> Package(long durationUs = 600_000)
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "ClusterExit"));
        if (durationUs == package.LineExitDurationUs) return package;
        var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["lineExit"]!["durationUs"] = durationUs;
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    }
    private static ProjectEffectInstance Instance(EffectPackageContents package, string id, string? lineId = null) => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision, ExportId = "cluster-exit",
        Binding = lineId is null ? new(ProjectEffectBindingKind.Project) : new(ProjectEffectBindingKind.Lines, ids: [lineId]),
    };
    private static ProjectSnapshot Snapshot(EffectPackageContents[] packages, ProjectEffectInstance[] instances, bool ligature = false)
    {
        var first = ligature
            ? LyricLine.Create("line-0", "office", new(0, 1_000_000), TimingStatus.Edited,
                [.. "office".Select((character, index) => LyricToken.Create("word-" + index, character.ToString(),
                    new(index * 100_000, index * 100_000 + 100_000), TimingStatus.Edited))]) with
                { RubyAnnotations = [RubyAnnotation.Create("ruby-0", "word-1", "xy")] }
            : LyricLine.Create("line-0", "AB", new(0, 1_000_000), TimingStatus.Edited,
                [LyricToken.Create("word-0", "AB", new(0, 1_000_000), TimingStatus.Edited)]) with
                { RubyAnnotations = [RubyAnnotation.Create("ruby-0", "word-0", "ab")] };
        var second = LyricLine.Create("line-1", "CD", new(1_000_000, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("next", "CD", new(1_000_000, 2_000_000), TimingStatus.Edited)]) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby-1", "next", "cd")] };
        var project = Project.Create("SDK v2 exits", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 601_000_000),
            LyricsDocument.Create([first, second]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56,
                Stroke = null, Shadow = null, Glow = null, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White });
        return ProjectSnapshot.FromProject(project with { Effects = new(packages, instances), EnterAnimation = null });
    }
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot, ScriptCompiler compiler)
    {
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler);
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(package => package.Artifact.Diagnostics)));
        return compiled;
    }
    private static PureFrameSourceFactory Factory(ProjectSnapshot snapshot, CompiledProjectEffects compiled)
        => new(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline());
    private static ValueTask<PreparedRenderFrame> Prepare(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long timeUs)
        => session.PrepareAsync(timeUs, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    private static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame prepared)
    {
        using SkiaRenderPipeline renderer = new();
        using var frame = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared });
        return frame.PixelMemory.ToArray();
    }
    private static long Alpha(byte[] pixels)
    {
        long alpha = 0; for (int index = 3; index < pixels.Length; index += 4) alpha += pixels[index];
        return alpha;
    }
}
