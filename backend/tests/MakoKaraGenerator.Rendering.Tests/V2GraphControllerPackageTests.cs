using System.Security.Cryptography;
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
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Rendering.Tests;

// Real v3 package acceptance after the recorded capability admission red/green.
public sealed class V2GraphControllerPackageTests
{
    private static readonly string[] GraphIds = ["a", "b"];
    private static readonly string[] InstanceIds = ["first", "last"];
    private static readonly string[] ImageGraphRequires = ["image.graph"];
    private static readonly string[] ProjectBindings = ["project"];
    private static readonly string[] GraphParameters = ["amount"];
    private const long TimeUs = 1_250_000;
    private static readonly string[] ControllerParameters = ["target", "writes", "bad", "clear", "value", "vary", "catchFailure"];
    private static ProjectEffectInstance Instance(EffectPackageContents package, string id, object? values = null) => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision, ExportId = "controller", Seed = 17,
        Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(values ?? new { })), Subscription = new("project"),
    };
    private static ProjectSnapshot Snapshot(EffectPackageContents package, ProjectEffectInstance[] instances, bool mark = false)
    {
        var line = LyricLine.Create("line", "A", new(0, 602_000_000), TimingStatus.Edited,
            [LyricToken.Create("a", "A", new(0, 602_000_000), TimingStatus.Edited)]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "x")] };
        var project = mark ? LeadInFixture.Create() : Project.Create("M09 real graph controller", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 603_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 64,
                SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { Effects = new([package], instances) });
    }
    private static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot)
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await CompiledProjectEffects.CompileAsync(snapshot, compiler, new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator());
        Assert.True(result.Succeeded, string.Join('\n', result.Packages.SelectMany(package => package.Artifact.Diagnostics))); return result;
    }
    private static PureFrameSourceFactory Factory(ProjectSnapshot snapshot, CompiledProjectEffects compiled)
        => new(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline());
    private static ValueTask<PreparedRenderFrame> Prepare(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long time = TimeUs)
        => session.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    private static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame frame, CompiledPackageGraphExports exports)
    {
        using var resources = new SceneImageEffectResources(GraphIds.Select(id => exports.CreateProgram(exports.Resources.GetGraph(id), new(0, 0, 640, 360))));
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var image = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true }); return image.PixelMemory.ToArray();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task 真实typed控制器按字族整行标记绑定动态图且任意时刻重复乱序一致(int target)
    {
        var package = Package(); var snapshot = Snapshot(package, [Instance(package, "graph", new { target, vary = 1 })], target == 2);
        var compiled = await Compile(snapshot); await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        long[] times = target == 2 ? [10_000_000, 9_250_000, 10_000_000] : [TimeUs, 600_250_000, 2_750_000, TimeUs];
        List<PreparedRenderFrame> frames = [];
        foreach (long time in times)
        {
            var frame = await Prepare(session, snapshot, time); var binding = Assert.Single(frame.ImageEffects);
            Assert.Equal(compiled.Effects[0].PackageIdentity, binding.PackageIdentity); Assert.Equal(compiled.Effects[0].RuntimeHash, binding.ArtifactHash);
            Assert.Equal(target, binding.Binding.Target switch { ImageEffectTarget.Token => 0, ImageEffectTarget.Line => 1, ImageEffectTarget.Mark => 2, _ => -1 });
            Assert.Single(binding.Binding.Uniforms.Values); Assert.Single(binding.Binding.Transforms.Values); frames.Add(frame);
        }
        using var exports = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package));
        Assert.Equal(frames[0].ImageEffects.ToArray(), frames[^1].ImageEffects.ToArray()); Assert.Equal(Pixels(snapshot, frames[0], exports), Pixels(snapshot, frames[^1], exports));
        Assert.Contains(Pixels(snapshot, frames[0], exports), value => value != 0);
        await using var reopened = await Factory(snapshot, compiled).CreateAsync(snapshot); var direct = await Prepare(reopened, snapshot, times[0]);
        Assert.Equal(frames[0].ImageEffects.ToArray(), direct.ImageEffects.ToArray()); Assert.Equal(Pixels(snapshot, frames[0], exports), Pixels(snapshot, direct, exports));
    }

    [Fact]
    public async Task 完整替换清除两类旧覆盖且同目标跨实例顺序保持各自图身份()
    {
        var package = Package(); var snapshot = Snapshot(package, [Instance(package, "first", new { clear = 1 }), Instance(package, "last", new { value = .25 })]);
        var compiled = await Compile(snapshot); await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot); var frame = await Prepare(session, snapshot);
        Assert.Equal(InstanceIds, frame.ImageEffects.Select(binding => binding.InstanceId.Value));
        Assert.Equal("b", frame.ImageEffects[0].Binding.Graph.Identity.ExportId); Assert.Empty(frame.ImageEffects[0].Binding.Uniforms.Values); Assert.Empty(frame.ImageEffects[0].Binding.Transforms.Values);
        Assert.Equal("a", frame.ImageEffects[1].Binding.Graph.Identity.ExportId); Assert.Single(frame.ImageEffects[1].Binding.Uniforms.Values);
        using var exports = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package)); Assert.Contains(Pixels(snapshot, frame, exports), value => value != 0);
    }

    [Theory]
    [InlineData(1, "ShaderUniformNativeRange", "Evaluate")]
    [InlineData(2, "MaterialUniformUnknown", "ComposeOutputs")]
    [InlineData(3, "GraphTransformNodeUnknown", "ComposeOutputs")]
    [InlineData(4, "PackageParameterOutOfRange", "ComposeOutputs")]
    [InlineData(5, "UndeclaredPackageOutput", "ComposeOutputs")]
    public async Task 错误保留真实包产物owner时间阶段且intrinsic坏值不能靠覆盖掩盖(int bad, string code, string stage)
    {
        var package = Package(); var snapshot = Snapshot(package, [Instance(package, "owner", new { bad, clear = bad == 1 ? 1 : 0, catchFailure = 1 })]);
        var compiled = await Compile(snapshot); await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await Prepare(session, snapshot));
        Assert.Equal(code, failure.Code); Assert.Equal(stage, failure.Stage); Assert.Equal(TimeUs, failure.TimeUs);
        Assert.Equal(compiled.Effects[0].Id, failure.InstanceId); Assert.Equal(compiled.Effects[0].RuntimeHash, failure.ArtifactHash); Assert.Equal(compiled.Effects[0].PackageIdentity, failure.PackageIdentity);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public async Task 最终图身份兼容性只检查存活完整值所以旧节点覆盖可明确清除(int bad)
    {
        var package = Package(); var snapshot = Snapshot(package, [Instance(package, "owner", new { bad, clear = 1 })]); var compiled = await Compile(snapshot);
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot); var frame = await Prepare(session, snapshot);
        var binding = Assert.Single(frame.ImageEffects); Assert.Equal("b", binding.Binding.Graph.Identity.ExportId); Assert.Empty(binding.Binding.Uniforms.Values); Assert.Empty(binding.Binding.Transforms.Values);
    }

    [Fact]
    public async Task 全实例全部被覆盖写入累计计费超限归属跨线实例并可从同一会话取消后恢复()
    {
        var package = Package(); var healthy = Snapshot(package, [Instance(package, "first", new { writes = 128 }), Instance(package, "last", new { writes = 128 })]);
        var compiled = await Compile(healthy); await using var session = await Factory(healthy, compiled).CreateAsync(healthy);
        var frame = await Prepare(session, healthy); Assert.Equal(2, frame.ImageEffects.Length);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await session.PrepareAsync(TimeUs, OutputMode.Transparent, RenderTarget.FromComposition(healthy.Composition), cancellation.Token));
        Assert.Equal(frame.ImageEffects.ToArray(), (await Prepare(session, healthy)).ImageEffects.ToArray());
        var invalid = Snapshot(package, [Instance(package, "first", new { writes = 128 }), Instance(package, "last", new { writes = 129, catchFailure = 1 })]);
        var invalidCompiled = await Compile(invalid); await using var invalidSession = await Factory(invalid, invalidCompiled).CreateAsync(invalid);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await Prepare(invalidSession, invalid));
        Assert.Equal("GraphOutputBudgetExceeded", failure.Code); Assert.Equal("ComposeOutputs", failure.Stage); Assert.Equal("last", failure.InstanceId.Value);
    }

    private static EffectPackageContents Package()
    {
        static object Output(string id, string target) => new { id, kind = "ImageGraph", target, requires = Array.Empty<string>(), dependsOn = Array.Empty<string>() };
        object[] targets = [Output("family", "tokenFamily"), Output("line", "line"), Output("mark", "textMark")];
        var files = new Dictionary<string, (string Role, byte[] Bytes)>(StringComparer.Ordinal);
        files.Add("src/GraphEffect.cs", ("csharp", Encoding.UTF8.GetBytes(Controller)));
        files.Add("shaders/copy.sksl", ("shader", Encoding.UTF8.GetBytes("uniform shader original; uniform float amount; half4 main(float2 p) { return original.eval(p)*amount; }")));
        files.Add("shaders/copy.program.json", ("shaderProgram", JsonSerializer.SerializeToUtf8Bytes(new { programVersion = 1, source = "shaders/copy.sksl", profile = ShaderProgramDefinition.RasterProfile,
            uniforms = new[] { new { name = "amount", type = "float" } }, children = new[] { new { name = "original", type = "color" } } })));
        JsonObject Graph() => new()
        {
            ["graphVersion"] = 1, ["id"] = "dynamic", ["inputs"] = new JsonArray(new JsonObject { ["id"] = "original", ["source"] = "TargetColor", ["type"] = "color",
                ["availability"] = "required", ["backgroundRequirement"] = "none", ["space"] = "canvas", ["capture"] = new JsonObject { ["includeRuby"] = true,
                    ["includeStroke"] = true, ["includeShadow"] = true, ["includeDescendantGroups"] = false, ["includeOwnedOverlay"] = false, ["region"] = "fillAndStroke" } }),
            ["parameters"] = new JsonArray("amount"), ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "tint", ["op"] = "RuntimeShader", ["program"] = "shaders/copy.program.json", ["children"] = new JsonObject { ["original"] = "input:original" },
                    ["uniforms"] = new JsonObject { ["amount"] = "parameter:amount" }, ["outputType"] = "color", ["bounds"] = new JsonObject { ["expandPx"] = 0, ["maxExpandPx"] = 0, ["maxSampleOffsetPx"] = 0 }, ["edge"] = "transparent" },
                new JsonObject { ["id"] = "move", ["op"] = "Transform", ["input"] = "node:tint", ["matrix"] = new JsonArray(1, 0, 0, 1, 0, 0), ["outputBounds"] = new JsonArray(0, 0, 640, 360),
                    ["bounds"] = new JsonObject { ["expandPx"] = 0, ["maxExpandPx"] = 0, ["maxSampleOffsetPx"] = 32 }, ["edge"] = "transparent" }),
            ["output"] = "node:move", ["anchorMode"] = "ColorOnly", ["bounds"] = new JsonObject { ["space"] = "canvas", ["source"] = "targetRegion", ["maxExpandPx"] = 0 },
            ["composite"] = new JsonObject { ["mode"] = "sourceOver" },
        };
        files.Add("graphs/a.graph.json", ("graph", JsonSerializer.SerializeToUtf8Bytes(Graph()))); files.Add("graphs/b.graph.json", ("graph", JsonSerializer.SerializeToUtf8Bytes(Graph())));
        var parameters = new[] { ("target", 0d, 0d, 2d), ("writes", 1d, 0d, 257d), ("bad", 0d, 0d, 5d), ("clear", 0d, 0d, 1d), ("value", .5d, 0d, 1d),
            ("vary", 0d, 0d, 1d), ("catchFailure", 0d, 0d, 1d), ("amount", 1d, 0d, 1d) };
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { packageVersion = 3, packageId = "makokara.m09.graph-controller", name = "M09 graph controller", version = "2.0.0", sdkVersion = "2.0.0",
            shaderProfile = "skia-ganesh-3.119.4-v2", requires = ImageGraphRequires, lineExit = new { durationUs = 0 }, emitters = Array.Empty<object>(),
            files = files.Select(pair => new { path = pair.Key, role = pair.Value.Role, sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value.Bytes)) }),
            parameters = parameters.Select(value => new { id = value.Item1, type = "number", label = value.Item1, @default = value.Item2, min = value.Item3, max = value.Item4 }),
            exports = new object[] { new { id = "controller", kind = "controller", name = "typed controller", entryType = "GraphEffect", parameters = ControllerParameters, bindingKinds = ProjectBindings, outputs = targets },
                new { id = "a", kind = "graph", name = "a", graph = "graphs/a.graph.json", parameters = GraphParameters, bindingKinds = ProjectBindings, outputs = targets },
                new { id = "b", kind = "graph", name = "b", graph = "graphs/b.graph.json", parameters = GraphParameters, bindingKinds = ProjectBindings, outputs = targets },
                new { id = "onlyFamily", kind = "graph", name = "onlyFamily", graph = "graphs/a.graph.json", parameters = GraphParameters, bindingKinds = ProjectBindings, outputs = new[] { Output("family", "tokenFamily") } } } });
        return EffectPackageContents.CreateSupported(manifest, files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value.Bytes)));
    }

    private const string Controller = """
        using System;
        using System.Collections.Generic;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record Data(double Target, double Writes, double Bad, double Clear, double Value, double Vary, double Catch) : PreparedEffect;
        public sealed class GraphEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext c) => new Data(c.Parameters.GetNumber("target"), c.Parameters.GetNumber("writes"), c.Parameters.GetNumber("bad"),
                c.Parameters.GetNumber("clear"), c.Parameters.GetNumber("value"), c.Parameters.GetNumber("vary"), c.Parameters.GetNumber("catchFailure"));
            public void Evaluate(in TextFrame frame, PreparedEffect state, TextOutput output)
            {
                var d = (Data)state; TextTarget? target = null;
                if (d.Target == 0) foreach (var token in frame.Geometry) { target = new TextTarget.Family(token.WithRuby); break; }
                if (d.Target == 1) foreach (var line in frame.LineLoads) { target = new TextTarget.Line(line.Target); break; }
                if (d.Target == 2) foreach (var mark in frame.MarkGeometry) { target = new TextTarget.Mark(mark.Target); break; }
                if (target is null) return;
                double value = d.Vary > 0 ? .25 + (frame.TimeUs % 1000000) / 2000000d : d.Value;
                var parameters = new ParameterValues(new[] { KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(d.Bad == 4 ? 2 : 1)) });
                var uniforms = new GraphUniformValues(new[] { KeyValuePair.Create(("tint", d.Bad == 2 ? "missing" : "amount"), UniformValue.Float(d.Bad == 1 ? 1e100 : value)) });
                var transforms = new GraphTransformValues(new[] { KeyValuePair.Create(d.Bad == 3 ? "missing" : "move", new GraphTransformValue(Affine2.Translation(8, 0), new Rect2(0, 0, 640, 360))) });
                var binding = new GraphBindingValue(frame.Resources.GetGraph("a"), parameters, uniforms, transforms);
                try { for (int i = 0; i < (int)d.Writes; i++) output.BindGraph(target, binding); }
                catch (Exception) { if (d.Catch == 0) throw; }
                if (d.Clear > 0) output.BindGraph(target, new GraphBindingValue(frame.Resources.GetGraph("b"),
                    new ParameterValues(new[] { KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(1)) }), GraphUniformValues.Empty, GraphTransformValues.Empty));
                if (d.Bad == 5) output.BindGraph(new TextTarget.Line(frame.LineLoads[0].Target), new GraphBindingValue(frame.Resources.GetGraph("onlyFamily"), parameters, uniforms, transforms));
            }
        }
        """;
}
