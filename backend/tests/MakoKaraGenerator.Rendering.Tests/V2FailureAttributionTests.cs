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
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2FailureAttributionTests
{
    [Fact]
    public async Task 隐藏左极限几何缺失归属该装载的最长退场声明包()
    {
        var unrelated = await ExitPackage(3_000_000);
        var shortExit = await ExitPackage(100_000);
        var owner = await ExitPackage(600_000);
        var snapshot = Snapshot([unrelated, shortExit, owner],
            [Instance(unrelated, "unrelated-first", "cluster-exit", "line-1"),
                Instance(shortExit, "short", "cluster-exit", "line-0"),
                Instance(owner, "exit-owner", "cluster-exit", "line-0")]);
        var compiled = await Compile(snapshot);
        var geometry = new MissingExitGeometry();
        var factory = new PureFrameSourceFactory(compiled.Effects, snapshot.EffectInputsFingerprint!, () => geometry);
        await using var session = await factory.CreateAsync(snapshot);

        var error = await Assert.ThrowsAsync<EffectFailureException>(async () =>
            await session.PrepareAsync(1_300_000, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));

        AssertFailure(error, compiled, owner, "exit-owner", "cluster-exit", "PrepareExitReplica", 1_300_000,
            "ExitReplicaGeometryUnavailable");
        var cause = Assert.IsType<PreparedFrameException>(error.InnerException);
        Assert.Equal(geometry.MissingBindingId, Assert.IsType<BindingId>(cause.BindingId).Value);
        Assert.Contains(999_999, geometry.Calls);
        Assert.Contains(1_300_000, geometry.Calls);
    }

    [Fact]
    public async Task 基础字包材质已准备但缺失原生资源仍保留实际写入实例和包归属()
    {
        var writer = MaterialPackage();
        var unrelated = await EffectPackageStorage.ReadSupportedDirectoryAsync(
            Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "WordEmphasis"));
        var snapshot = Snapshot([unrelated, writer],
            [Instance(unrelated, "unrelated-first", "word-emphasis"),
                Instance(writer, "material-writer", "controller"),
                Instance(unrelated, "unrelated-last", "word-emphasis")]);
        var compiled = await Compile(snapshot);
        await using var session = await new PureFrameSourceFactory(compiled.Effects, snapshot.EffectInputsFingerprint!,
            static () => new SkiaRenderPipeline()).CreateAsync(snapshot);

        var prepared = await session.PrepareAsync(500_000, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.NotEmpty(prepared.BaseMaterials);
        using var renderer = new SkiaRenderPipeline();
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(RenderRequest.At(snapshot, 500_000)
            with { TransparentBackground = true, PreparedFrame = prepared }));

        AssertFailure(error, compiled, writer, "material-writer", "controller", "RenderMaterials", 500_000,
            "MaterialResourcesRequired");
        var cause = Assert.IsType<PreparedFrameException>(error.InnerException);
        var target = Assert.IsType<TextTarget.Base>(cause.TextTarget);
        Assert.Equal("word-0", target.Value.TokenId.Value);
    }

    private const string MaterialSource = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                var material = MaterialValue.PackageExport(frame.Resources.GetMaterial("ink"), frame.Parameters);
                foreach (var token in frame.Presentations.TokensInBinding)
                    output.SetFill(new TextTarget.Base(token.BaseOnly), FillState.Sung, material);
            }
        }
        """;

    private static EffectPackageContents MaterialPackage()
    {
        var package = MixedEffectPackageFixture.MaterialContents(controllerSource: MaterialSource);
        var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["packageId"] = "makokara.v2.partition-material-failure";
        manifest["requires"] = new JsonArray("style.fill");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 };
        manifest["emitters"] = new JsonArray();
        manifest["exports"]![0]!["outputs"]![0]!["target"] = "baseToken";
        return Repackage(package, manifest);
    }

    private static async Task<EffectPackageContents> ExitPackage(long durationUs)
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(
            Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "ClusterExit"));
        var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["lineExit"]!["durationUs"] = durationUs;
        return Repackage(package, manifest);
    }

    private static EffectPackageContents Repackage(EffectPackageContents package, JsonNode manifest)
        => EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));

    private static ProjectEffectInstance Instance(EffectPackageContents package, string id, string exportId, string? lineId = null) => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision, ExportId = exportId,
        Binding = lineId is null ? new(ProjectEffectBindingKind.Project) : new(ProjectEffectBindingKind.Lines, ids: [lineId]),
    };

    private static ProjectSnapshot Snapshot(EffectPackageContents[] packages, ProjectEffectInstance[] instances)
    {
        var first = LyricLine.Create("line-0", "AB", new(0, 1_000_000), TimingStatus.Edited,
            [LyricToken.Create("word-0", "AB", new(0, 1_000_000), TimingStatus.Edited)]) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby-0", "word-0", "ab")] };
        var second = LyricLine.Create("line-1", "CD", new(1_000_000, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("word-1", "CD", new(1_000_000, 2_000_000), TimingStatus.Edited)]);
        var project = Project.Create("SDK v2 failure owner", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 4_000_000),
            LyricsDocument.Create([first, second]), lyricStyle: LyricStyle.Default with
            { FontFamilyName = "DejaVu Sans", FontSizePx = 56, Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { Effects = new(packages, instances), EnterAnimation = null });
    }

    private static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot)
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler, new PackageImageValidator(),
            new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(package => package.Artifact.Diagnostics)));
        return compiled;
    }

    private static void AssertFailure(EffectFailureException error, CompiledProjectEffects compiled, EffectPackageContents package,
        string instanceId, string exportId, string stage, long timeUs, string code)
    {
        Assert.Equal(instanceId, error.InstanceId.Value);
        Assert.Equal(compiled.Effects.Single(effect => effect.Id.Value == instanceId).RuntimeHash, error.ArtifactHash);
        Assert.Equal(new EffectPackageIdentity(package.PackageId, package.Revision, exportId), error.PackageIdentity);
        Assert.Equal(stage, error.Stage); Assert.Equal(timeUs, error.TimeUs); Assert.Equal(code, error.Code);
    }

    private sealed class MissingExitGeometry : ILineGeometryProvider, IDisposable
    {
        private readonly SkiaRenderPipeline _renderer = new();
        internal List<long> Calls { get; } = [];
        internal string? MissingBindingId { get; private set; }
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        {
            Calls.Add(timeUs);
            var scene = _renderer.GetSceneFrame(snapshot, timeUs, editingLineId);
            if (timeUs != 999_999) return scene;
            var missing = Assert.Single(scene.Lines.Where(line => line.Frame.Line!.Id == "line-0"));
            MissingBindingId = missing.BindingId;
            return scene with { Lines = [.. scene.Lines.Select(line => line == missing ? line with { Geometry = null } : line)] };
        }
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => _renderer.GetLineGeometry(snapshot, lineId);
        public void Dispose() => _renderer.Dispose();
    }
}
