using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.TestSupport;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

// M13 A3 b：D8 核对时 C08 Random 在 v2 没有直接测试，v2 也只用过 NotAnalyzed 音频。
// 这里走工程实例 → 编译 → 正式准备会话（与预览／导出同一入口），核对随机取值身份与已分析音频的读取。
public sealed class V2RandomAndAudioInputTests
{
    private const string Source = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record Probe(string Mode, double Declared) : PreparedEffect;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source)
            {
                string mode = source.Parameters.GetEnum("mode");
                if (mode == "audio" && (source.Audio.Availability != AudioAvailability.Available || source.Audio.Revision is null))
                    throw new System.InvalidOperationException("DeclareAudioUnavailable");
                return new Probe(mode, source.Random.GetUnitDouble("declare", 0));
            }
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                var probe = (Probe)data;
                foreach (var token in frame.Geometry)
                {
                    if (probe.Mode == "random")
                    {
                        double x = frame.Random.GetUnitDouble("jitter", (ulong)(frame.TimeUs / 100_000)) * 20;
                        output.AppendTransform(token.WithRuby, Affine2.Translation(x, probe.Declared * 20));
                    }
                    else if (frame.Audio.GetAt(frame.TimeUs).Values is { } values)
                    {
                        var fill = MaterialValue.Solid(new ColorRgba(.1, System.Math.Round(values.BassLevel * 255) / 255, .1, 1));
                        output.SetFill(token.WithRuby, TextState.Both, fill);
                    }
                }
            }
        }
        """;

    [Fact]
    public async Task 随机取值只由种子实例和分流键决定且跳转重开不变()
    {
        static (double X, double Y) Expected(ulong seed, string id, long time)
        {
            Fx.DeterministicRandom random = new(seed, new(id));
            return (random.GetUnitDouble("jitter", (ulong)(time / 100_000)) * 20, random.GetUnitDouble("declare", 0) * 20);
        }
        async Task<(double X, double Y)[]> Run(ulong seed, string id, params long[] times)
        {
            var snapshot = Snapshot(TextProject(), "random", id, seed);
            await using var session = await (await Compile(snapshot)).CreateFactory(static () => new SkiaRenderPipeline())
                .CreateAsync(snapshot);
            List<(double, double)> result = [];
            foreach (long time in times)
            {
                var matrix = Assert.Single((await session.PrepareAsync(time, Fx.OutputMode.Transparent,
                    RenderTarget.FromComposition(snapshot.Composition))).TokenTransforms).LocalMatrix;
                result.Add((matrix.Tx, matrix.Ty));
            }
            return [.. result];
        }
        var first = await Run(7, "random-a", 500_000, 1_200_000, 500_000);
        Assert.Equal([Expected(7, "random-a", 500_000), Expected(7, "random-a", 1_200_000), Expected(7, "random-a", 500_000)], first);
        Assert.NotEqual(first[0], first[1]);
        Assert.Equal(first[0], Assert.Single(await Run(7, "random-a", 500_000)));
        var otherInstance = Assert.Single(await Run(7, "random-b", 500_000));
        var otherSeed = Assert.Single(await Run(8, "random-a", 500_000));
        Assert.Equal(Expected(7, "random-b", 500_000), otherInstance); Assert.Equal(Expected(8, "random-a", 500_000), otherSeed);
        Assert.NotEqual(first[0], otherInstance); Assert.NotEqual(first[0], otherSeed);
    }

    [Fact]
    public async Task 已分析音频在声明与求值中可用且像素等于按低频取色的静态编辑()
    {
        using TemporaryDirectory temp = new();
        string audio = temp.FilePath("音频.wav");
        WavWriter.Write(audio, Enumerable.Range(0, 48624).Select(i => (float)(.7 * Math.Sin(2 * Math.PI * 80 * i / 48000d))).ToArray(), 48000);
        var analysis = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(audio);
        var project = TextProject() with { AudioPath = audio, AudioAnalysis = analysis.ToSourceData() };
        var features = ProjectAudioProjection.Create(project);
        Assert.Equal(Fx.AudioAvailability.Available, features.Availability);
        var snapshot = Snapshot(project, "audio", "audio", 1);
        await using var session = await (await Compile(snapshot)).CreateFactory(static () => new SkiaRenderPipeline())
            .CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var middle = await session.PrepareAsync(500_000, Fx.OutputMode.Transparent, target);
        Assert.Single(middle.TokenStyles);
        // 分析只覆盖约 1.013 秒；越界时刻 GetAt 返回 OutsideRange，作者不写外观，不报错。
        Assert.Equal(Fx.AudioAvailability.OutsideRange, features.GetAt(1_200_000).Availability);
        Assert.Empty((await session.PrepareAsync(1_200_000, Fx.OutputMode.Transparent, target)).TokenStyles);

        byte green = (byte)Math.Round(features.GetAt(500_000).Values!.Value.BassLevel * 255);
        Assert.True(green > 26, "低频值太小，无法与底色区分。");
        var plain = ProjectSnapshot.FromProject(project with { LyricStyle = project.LyricStyle with
            { UnsungColor = new(26, green, 26), SungColor = new(26, green, 26) } });
        using SkiaRenderPipeline renderer = new();
        using var actual = renderer.Render(RenderRequest.At(snapshot, 500_000) with { PreparedFrame = middle, TransparentBackground = true });
        using var expected = renderer.Render(RenderRequest.At(plain, 500_000) with { TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        using var unchanged = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = true });
        Assert.False(unchanged.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
    }

    private static Project TextProject() => Project.Create("v2 随机与音频", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 1_400_000),
        LyricsDocument.Create([LyricLine.Create("line", "AB", new(0, 1_400_000), TimingStatus.Edited,
            [LyricToken.Create("token", "AB", new(0, 1_400_000), TimingStatus.Edited)])]),
        lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 50, SungColor = RgbaColor.White,
            UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null });

    private static ProjectSnapshot Snapshot(Project project, string mode, string id, ulong seed)
    {
        var package = Package();
        return ProjectSnapshot.FromProject(project with { Effects = new([package], [new ProjectEffectInstance
        {
            Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision, ExportId = "probe", Seed = seed,
            Subscription = new("project"), Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(new { mode })),
        }]) });
    }

    private static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot)
    {
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")),
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(item => item.ControllerArtifact?.Diagnostics ?? [])));
        return compiled;
    }

    private static EffectPackageContents Package()
    {
        var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(Source);
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion; manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile; manifest["packageId"] = "makokara.m13.random-audio";
        manifest["requires"] = new JsonArray("token.affine", "style.fill");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        manifest["parameters"] = new JsonArray(new JsonObject { ["id"] = "mode", ["type"] = "enum", ["label"] = "模式", ["default"] = "random",
            ["values"] = new JsonArray(new JsonObject { ["value"] = "random", ["label"] = "随机" }, new JsonObject { ["value"] = "audio", ["label"] = "音频" }) });
        var export = manifest["exports"]![0]!; export["id"] = "probe"; export["parameters"] = new JsonArray("mode");
        export["bindingKinds"] = new JsonArray("project");
        export["outputs"] = new JsonArray(
            new JsonObject { ["id"] = "move", ["kind"] = "VisualTransform", ["target"] = "tokenFamily", ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray() },
            new JsonObject { ["id"] = "fill", ["kind"] = "StyleModulation", ["target"] = "tokenFamily", ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray() });
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }
}
