using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
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

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2ProjectDurationTests
{
    private const string Fingerprint = "v2-project-duration-fixed-input";

    [Fact]
    public async Task 宿主把工程成片时长交给Declare与文字帧且未设置保持null()
    {
        var effect = await Effect();
        foreach ((long? duration, double expected) in new (long?, double)[] { (2_500_000, 250), (7_000_000, 700), (null, -7) })
        {
            var snapshot = Snapshot(duration);
            await using var session = await new PureFrameSourceFactory([effect], Fingerprint,
                static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
            var target = RenderTarget.FromComposition(snapshot.Composition);
            foreach (long time in new long[] { 50_000, 0, 50_000 })
            {
                var matrix = Assert.Single((await session.PrepareAsync(time, OutputMode.Transparent, target)).TokenTransforms).LocalMatrix;
                // Declare 的准备结果写 Tx，逐帧读取写 Ty，两者都必须来自同一固定输入。
                Assert.Equal(expected, matrix.Tx); Assert.Equal(expected, matrix.Ty);
            }
        }
    }

    private static async Task<CompiledFrameEffect> Effect()
    {
        const string source = """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record DurationData(long Declared) : PreparedEffect;
            public sealed class WordEmphasis : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => new DurationData(source.ProjectDurationUs ?? -70_000);
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    double declared = ((DurationData)data).Declared / 10_000d, current = (frame.ProjectDurationUs ?? -70_000) / 10_000d;
                    foreach (var token in frame.Geometry) output.AppendTransform(token.WithRuby, Affine2.Translation(declared, current));
                }
            }
            """;
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "WordEmphasis"));
        const string path = "src/WordEmphasis.cs"; byte[] bytes = Encoding.UTF8.GetBytes(source);
        var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == path)!["sha256"]
            = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        package = EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)(file.Path == path ? bytes : file.Content.ToArray()))));
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(package),
            new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        return compiled.CreateEffect("word-emphasis", new("duration"), new EffectBinding.Project(), 0);
    }

    private static ProjectSnapshot Snapshot(long? durationUs)
    {
        var line = LyricLine.Create("line", "AB", new(0, 100_000), TimingStatus.Edited,
            [LyricToken.Create("word", "AB", new(0, 100_000), TimingStatus.Edited)]);
        var project = Project.Create("v2 duration", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: durationUs),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56,
                Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { EnterAnimation = null }) with { EffectInputsFingerprint = Fingerprint };
    }
}
