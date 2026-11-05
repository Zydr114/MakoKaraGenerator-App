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
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Rendering.Tests;

// All native programs below originate in actual v3 packages.
internal static class V2GroupGraphPackageFixture
{
    internal const string GroupCapability = "image.graph.group";
    internal static EffectPackageContents Package(bool direct, Action<JsonObject>? manifestChange = null,
        Action<JsonObject>? graphChange = null, long exitUs = 0, string? shaderSource = null, Action<JsonObject>? programChange = null)
    {
        var original = MixedEffectPackageFixture.Contents(pure: direct, controllerSource: Source, graphChange: graphChange, shaderSource: shaderSource, programChange: programChange);
        var manifest = JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = 3; manifest["sdkVersion"] = "2.0.0"; manifest["shaderProfile"] = "skia-ganesh-3.119.4-v2";
        manifest["requires"] = exitUs == 0 ? new JsonArray("image.graph", GroupCapability) : new JsonArray("image.graph", GroupCapability, "line.exit");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = exitUs }; manifest["emitters"] = new JsonArray();
        manifest["parameters"]!.AsArray().Add(new JsonObject { ["id"] = "node", ["type"] = "text", ["label"] = "源组", ["default"] = "" });
        var exports = manifest["exports"]!.AsArray();
        for (int i = exports.Count - 1; i >= 0; i--) if (exports[i]!["kind"]!.GetValue<string>() == "material") exports.RemoveAt(i);
        foreach (var export in exports)
        {
            var output = MixedEffectPackageFixture.Output("group"); output.Remove("bypass"); output["requires"] = new JsonArray(GroupCapability);
            export!["outputs"] = new JsonArray(output);
            if (export["kind"]!.GetValue<string>() == "controller") export["parameters"] = new JsonArray("amount", "node");
        }
        manifestChange?.Invoke(manifest);
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), original.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    }
    internal static ProjectEffectInstance Instance(EffectPackageContents package, bool direct, string node = "", double amount = .5,
        ProjectEffectBinding? binding = null) => new()
    {
        Id = "group-instance", Name = "group-instance", PackageId = package.PackageId, Revision = package.Revision,
        ExportId = direct ? "tint" : "controller", Binding = binding ?? new(ProjectEffectBindingKind.Project), Subscription = new("project"), Seed = 19,
        Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(direct ? (object)new { amount } : new { amount, node })),
    };
    internal static ProjectSnapshot Snapshot(EffectPackageContents package, ProjectEffectInstance[] instances)
    {
        var lines = new[] { Line("line-0", "token-0", "AB", 0, 1_000_000), Line("line-1", "token-1", "CD", 1_000_000, 2_000_000) };
        var project = Project.Create("G4 actual source groups", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 4_000_000),
            LyricsDocument.Create(lines), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56,
                UnsungColor = RgbaColor.White, SungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { Effects = new([package], instances), EnterAnimation = null });
    }
    private static LyricLine Line(string id, string token, string text, long start, long end)
        => LyricLine.Create(id, text, new(start, end), TimingStatus.Edited, [LyricToken.Create(token, text, new(start, end), TimingStatus.Edited)])
            with { RubyAnnotations = [RubyAnnotation.Create("ruby-" + id, token, text.ToLowerInvariant())] };
    internal static string Root(ProjectSnapshot snapshot) => snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId;
    internal static Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot, IScriptCompilationService? compiler = null)
        => V2DirectAndMaterialPackageFixture.Compile(snapshot, compiler);
    internal static PureFrameSourceFactory Factory(ProjectSnapshot snapshot, CompiledProjectEffects compiled)
        => new(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline());
    internal static ValueTask<PreparedRenderFrame> Prepare(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long time = 500_000)
        => session.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    internal static SceneImageEffectResources Resources(CompiledPackageGraphExports native)
        => new([native.CreateProgram(native.Resources.GetGraph("tint"), new(0, 0, 640, 360))]);
    internal static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame frame, SkiaRenderPipeline renderer)
    {
        using var image = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        return image.PixelMemory.ToArray();
    }
    private const string Source = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record Data(string Node, double Amount) : PreparedEffect;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext c) => new Data(c.Parameters.GetText("node"), c.Parameters.GetNumber("amount"));
            public void Evaluate(in TextFrame f, PreparedEffect p, TextOutput o)
            {
                var d = (Data)p;
                var value = new GraphBindingValue(f.Resources.GetGraph("tint"), new ParameterValues(new[] {
                    new System.Collections.Generic.KeyValuePair<string, ParameterValue>("amount", new ParameterValue.Number(d.Amount))
                }), GraphUniformValues.Empty, GraphTransformValues.Empty);
                foreach (var group in f.GroupGeometry)
                    if (d.Node.Length == 0 || group.Target.NodeId.Value == d.Node) o.BindGraph(new TextTarget.Group(group.Target), value);
            }
        }
        """;
}
