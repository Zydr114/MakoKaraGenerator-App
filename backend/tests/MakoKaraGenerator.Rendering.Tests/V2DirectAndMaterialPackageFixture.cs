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

// Real package fixture for the admitted image.graph/material.uniform paths.
internal static class V2DirectAndMaterialPackageFixture
{
    private static readonly string[] MaterialIds = ["ink", "other"];
    internal static EffectPackageContents Package(bool direct = false, string target = "line")
    {
        var original = MixedEffectPackageFixture.MaterialContents(controllerSource: Source);
        var manifest = JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = 3; manifest["sdkVersion"] = "2.0.0"; manifest["shaderProfile"] = "skia-ganesh-3.119.4-v2";
        manifest["requires"] = direct ? new JsonArray("image.graph") : new JsonArray("style.fill", "style.stroke", "material.uniform", "style.wipe");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        foreach (var name in new[] { "mode", "mark", "vary", "writes" }) manifest["parameters"]!.AsArray().Add(new JsonObject
        { ["id"] = name, ["type"] = "number", ["label"] = name, ["default"] = name == "writes" ? 1 : 0, ["min"] = 0, ["max"] = name == "writes" ? 257 : 20 });
        var controller = manifest["exports"]![0]!;
        controller["parameters"] = new JsonArray("amount", "mode", "mark", "vary", "writes");
        controller["outputs"] = new JsonArray(new JsonObject { ["id"] = "family", ["kind"] = "StyleModulation", ["target"] = "tokenFamily", ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray() },
            new JsonObject { ["id"] = "mark", ["kind"] = "StyleModulation", ["target"] = "textMark", ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray() });
        var other = manifest["exports"]![1]!.DeepClone(); other["id"] = "other"; other["name"] = "Other"; manifest["exports"]!.AsArray().Add(other);
        var files = original.Files.Where(file => !direct || file.Role != "csharp").ToArray();
        if (direct)
        {
            manifest["exports"]!.AsArray().RemoveAt(0);
            var graph = manifest["exports"]!.AsArray().Single(item => item!["kind"]!.GetValue<string>() == "graph")!;
            graph["outputs"]![0]!["target"] = target;
            manifest["files"] = new JsonArray(manifest["files"]!.AsArray().Where(item => item!["role"]!.GetValue<string>() != "csharp").Select(item => item!.DeepClone()).ToArray());
        }
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    }
    internal static ProjectEffectInstance Instance(EffectPackageContents package, string id = "effect", int mode = 1, bool direct = false, int mark = 0, int writes = 1, int vary = 0, double amount = .5) => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision, ExportId = direct ? "tint" : "controller", Seed = 3, Subscription = new("project"),
        Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(direct ? (object)new { amount } : new { amount, mode, mark, writes, vary })),
    };
    internal static ProjectSnapshot Snapshot(EffectPackageContents package, ProjectEffectInstance[] instances, bool mark = false)
    {
        var line = LyricLine.Create("line", "A", new(0, 602_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "A", new(0, 602_000_000), TimingStatus.Edited)]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "x")] };
        var project = mark ? LeadInFixture.Create() : Project.Create("G3 final material scope", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 603_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 64,
                SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { Effects = new([package], instances) });
    }
    internal static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot, IScriptCompilationService? compiler = null)
    {
        compiler ??= new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler, new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(item => item.ControllerArtifact?.Diagnostics ?? []))); return compiled;
    }
    internal static PureFrameSourceFactory Factory(ProjectSnapshot snapshot, CompiledProjectEffects compiled) => new(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline());
    internal static ValueTask<PreparedRenderFrame> Prepare(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long time = 1_250_000)
        => session.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    internal static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame frame, CompiledPackageGraphExports native)
    {
        using var resources = new SceneImageEffectResources([native.CreateProgram(native.Resources.GetGraph("tint"), new(0, 0, 640, 360))], null,
            MaterialIds.Select(id => native.CreateMaterialProgram(native.Resources.GetMaterial(id), new(0, 0, 640, 360))));
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var image = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true }); return image.PixelMemory.ToArray();
    }
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record Data(double Mode, double Mark, double Vary, double Writes, double Amount) : PreparedEffect;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext c) => new Data(c.Parameters.GetNumber("mode"), c.Parameters.GetNumber("mark"), c.Parameters.GetNumber("vary"), c.Parameters.GetNumber("writes"), c.Parameters.GetNumber("amount"));
            public void Evaluate(in TextFrame f, PreparedEffect prepared, TextOutput o)
            {
                var d = (Data)prepared; TextTarget? t = null;
                if (d.Mark > 0) { foreach (var item in f.MarkGeometry) { t = new TextTarget.Mark(item.Target); break; } }
                else { foreach (var item in f.Geometry) { t = new TextTarget.Family(item.WithRuby); break; } }
                if (t is null) return;
                var a = f.Resources.GetMaterial("ink"); var b = f.Resources.GetMaterial("other");
                var parameters = new ParameterValues(new[] { KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(d.Amount)) });
                var materialA = MaterialValue.PackageExport(a, parameters); var materialB = MaterialValue.PackageExport(b, parameters);
                double value = d.Vary > 0 ? .25 + (f.TimeUs % 1000000) / 2000000d : .75;
                if (d.Mode == 15) { o.SetWipe(t, new WipeValue(.5, new WipeHighlightValue(new(1, 1, .6, 1)))); return; }
                if (d.Mode == 10)
                {
                    o.SetStrokes(t, TextState.Both, new StrokeValue(2, materialA)); o.SetMaterialUniform(t, MaterialSlot.UnsungStroke, a, "probe", "amount", UniformValue.Float(value));
                    o.SetStrokes(t, TextState.Both); o.SetStrokes(t, TextState.Both, new StrokeValue(3, materialA)); return;
                }
                foreach (var slot in new[] { MaterialSlot.UnsungFill, MaterialSlot.SungFill })
                {
                    TextState state = slot == MaterialSlot.UnsungFill ? TextState.Unsung : TextState.Sung;
                    if (d.Mode == 0 || d.Mode == 4 || d.Mode == 5 || d.Mode == 11)
                    {
                        try { o.SetMaterialUniform(t, slot, a, d.Mode == 4 ? "missing" : "probe", "amount", UniformValue.Float(d.Mode == 5 ? 1e100 : value)); }
                        catch (Exception) { if (d.Mode != 5) throw; }
                        if (d.Mode != 11) o.SetFill(t, state, materialB);
                    }
                    else if (d.Mode == 1 || d.Mode == 8 || d.Mode == 9 || d.Mode == 12 || d.Mode == 13)
                    {
                        if (d.Mode != 13) o.SetFill(t, state, materialA);
                        if (d.Mode == 8 || d.Mode == 9) o.SetMaterialUniform(t, slot, a, "probe", "amount", UniformValue.Int(1));
                        if (d.Mode != 12 && d.Mode != 9) for (int i = 0; i < (int)d.Writes; i++) o.SetMaterialUniform(t, slot, a, "probe", "amount", UniformValue.Float(value));
                    }
                    else if (d.Mode == 2)
                    {
                        o.SetFill(t, state, materialA); o.SetMaterialUniform(t, slot, a, "probe", "amount", UniformValue.Float(.2));
                        o.SetFill(t, state, materialB); o.SetMaterialUniform(t, slot, b, "probe", "amount", UniformValue.Float(.3)); o.SetFill(t, state, materialA);
                    }
                    else if (d.Mode == 3) o.SetMaterialUniform(t, slot, a, "probe", "amount", UniformValue.Float(value));
                    else if (d.Mode == 6)
                    {
                        var bad = new ParameterValues(new[] { KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(2)) });
                        o.SetFill(t, state, MaterialValue.PackageExport(a, bad)); o.SetFill(t, state, materialB);
                    }
                    else if (d.Mode == 14) { o.SetFill(t, state, materialB); o.SetMaterialUniform(t, slot, a, "probe", "amount", UniformValue.Float(value)); }
                    else if (d.Mode == 7) { o.SetFill(t, state, materialA); o.SetMaterialUniform(t, slot, a, "missing", "amount", UniformValue.Float(value)); }
                }
            }
        }
        """;
}
