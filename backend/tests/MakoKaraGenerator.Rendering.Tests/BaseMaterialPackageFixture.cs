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

// Real v3 package using the admitted material.uniform and image.graph paths.
internal static class BaseMaterialPackageFixture
{
    internal const long TimeUs = 1_250_000;
    internal static readonly GraphRect Bounds = new(0, 0, 640, 360);
    internal static EffectPackageContents Package(Action<JsonObject>? graphChange = null, string? shaderSource = null)
    {
        var original = MixedEffectPackageFixture.MaterialContents(controllerSource: Source, graphChange: graphChange, shaderSource: shaderSource);
        var manifest = JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = 3; manifest["sdkVersion"] = "2.0.0"; manifest["shaderProfile"] = "skia-ganesh-3.119.4-v2";
        manifest["packageId"] = "makokara.base-material-draft";
        manifest["requires"] = new JsonArray("style.fill", "style.stroke", "token.opacity", "token.affine", "material.uniform", "style.glow");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        foreach (string name in new[] { "mode", "writes" }) manifest["parameters"]!.AsArray().Add(new JsonObject
        { ["id"] = name, ["type"] = "number", ["label"] = name, ["default"] = name == "mode" ? 0 : 1, ["min"] = 0, ["max"] = 257 });
        manifest["exports"]![0]!["parameters"] = new JsonArray("amount", "mode", "writes");
        manifest["exports"]![0]!["outputs"] = new JsonArray(
            Output("baseStyle", "StyleModulation", "baseToken"), Output("familyStyle", "StyleModulation", "tokenFamily"),
            Output("baseAffine", "VisualTransform", "baseToken"));
        var familyGraph = MixedEffectPackageFixture.Output("tokenFamily"); familyGraph.Remove("bypass"); familyGraph["id"] = "familyColor";
        manifest["exports"]![2]!["outputs"]!.AsArray().Add(familyGraph);
        var other = manifest["exports"]![1]!.DeepClone(); other["id"] = "other"; manifest["exports"]!.AsArray().Add(other);
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), original.Files.Select(file =>
            KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    }
    private static JsonObject Output(string id, string kind, string target) => new()
    { ["id"] = id, ["kind"] = kind, ["target"] = target, ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray() };
    internal static ProjectEffectInstance Instance(EffectPackageContents package, string id = "material", int mode = 0, int writes = 1) => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision, ExportId = "controller", Seed = 31,
        Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(new { amount = .5, mode, writes })),
    };
    internal static ProjectSnapshot Snapshot(EffectPackageContents package, ProjectEffectInstance[]? instances = null, bool source = false)
    {
        var line = LyricLine.Create("line", "AB", new(0, 602_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("token", "AB", new(1_000_000, 3_000_000), TimingStatus.Confirmed)]) with
        { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "xy")] };
        var style = LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 64, SungColor = RgbaColor.White,
            UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null };
        if (source)
        {
            var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":0.5}"));
            style = style with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } };
        }
        return ProjectSnapshot.FromProject(Project.Create("Base material", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 603_000_000),
            LyricsDocument.Create([line]), lyricStyle: style) with { Effects = new([package], instances ?? [Instance(package)]), EnterAnimation = null });
    }
    internal static Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot) => CompiledProjectEffects.CompileAsync(snapshot,
        new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")), new PackageImageValidator(),
        new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
    internal static PureFrameSourceFactory Factory(ProjectSnapshot snapshot, CompiledProjectEffects compiled)
        => new(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline(), sourceMaterials: compiled.SourceMaterials);
    internal static ValueTask<PreparedRenderFrame> Prepare(IEvaluatedFrameSource source, ProjectSnapshot snapshot, long time = TimeUs)
        => source.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    internal static PreparedRenderFrame Parent(PreparedRenderFrame frame, System.Collections.Immutable.ImmutableArray<TokenFamilyTransform>? transforms = null) => PreparedRenderFrame.CreateWithDiagnosticSnapshot(frame.Snapshot,
        frame.TimeUs, frame.OutputMode, frame.Target, frame.Scene, transforms ?? frame.TokenTransforms, frame.TokenStyles, frame.DiagnosticSnapshot,
        frame.MarkTransforms, frame.MarkStyles, frame.LineTransforms, frame.GroupTransforms, frame.Materials, frame.SourceMaterials)
        .WithGroupOpacities(frame.GroupOpacities).WithImageEffects(frame.ImageEffects);
    internal static MaterialValue Solid(double amount = .5) => MaterialValue.Solid(new(51 / 255d * amount, 128 / 255d * amount, 204 / 255d * amount, 160 / 255d));
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record Data(int Mode, int Writes) : PreparedEffect;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext c) => new Data((int)c.Parameters.GetNumber("mode"), (int)c.Parameters.GetNumber("writes"));
            public void Evaluate(in TextFrame f, PreparedEffect data, TextOutput o)
            {
                var d = (Data)data;
                foreach (var token in f.Geometry)
                {
                    TextTarget b = new TextTarget.Base(token.BaseOnly); TextTarget family = new TextTarget.Family(token.WithRuby);
                    if (d.Mode == 1) { o.MultiplyOpacity(b, .5); o.AppendTransform(b, Affine2.Translation(2, 1)); continue; }
                    var a = f.Resources.GetMaterial("ink"); var other = f.Resources.GetMaterial("other");
                    var parameters = new ParameterValues(new[] { KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(.5)) });
                    var material = MaterialValue.PackageExport(a, parameters);
                    if (d.Mode == 2) { o.MultiplyOpacity(b, .5); continue; }
                    if (d.Mode == 12 || d.Mode == 13) o.SetGlow(d.Mode == 12 ? b : family, TextState.Both, new GlowValue(new(1,0,0,.8), .8, 14, 3));
                    if (d.Mode == 10) { o.SetStrokes(b, TextState.Both, new StrokeValue(5, material)); o.SetFill(b, TextState.Sung, MaterialValue.Solid(new(0,0,0,0))); o.SetFill(b, TextState.Unsung, MaterialValue.Solid(new(0,0,0,0))); continue; }
                    foreach (var state in new[] { TextState.Unsung, TextState.Sung })
                    {
                        var slot = state == TextState.Sung ? MaterialSlot.SungFill : MaterialSlot.UnsungFill;
                        if (d.Mode == 9) { o.SetMaterialUniform(b, slot, a, "probe", "amount", UniformValue.Float(.75)); continue; }
                        if (d.Mode == 0 || d.Mode == 3) o.SetFill(family, state, MaterialValue.PackageExport(a, new ParameterValues(new[] { KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(1)) })));
                        if (d.Mode == 3) { o.SetFill(b, state, MaterialValue.Solid(new(0,1,0,1))); continue; }
                        if (d.Mode == 4 || d.Mode == 5)
                        {
                            try { o.SetMaterialUniform(b, slot, a, "probe", "amount", UniformValue.Float(d.Mode == 5 ? 1e100 : .75)); }
                            catch (Exception) { if (d.Mode != 5) throw; }
                        }
                        if (d.Mode == 6) o.SetFill(b, state, MaterialValue.PackageExport(a, new ParameterValues(new[] { KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(2)) })));
                        for (int i = 0; i < d.Writes; i++) o.SetFill(b, state, material);
                        if (d.Mode == 7) o.SetMaterialUniform(b, slot, a, "missing", "amount", UniformValue.Float(.75));
                        if (d.Mode == 8) o.SetMaterialUniform(b, slot, a, "probe", "amount", UniformValue.Float(.75));
                    }
                }
            }
        }
        """;
}
