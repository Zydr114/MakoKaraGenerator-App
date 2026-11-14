using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Compilation;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MixedEffectPackageTests
{
    private static void RequireCapabilities(JsonObject manifest, params string[] capabilities)
    {
        var requires = manifest["requires"]!.AsArray();
        foreach (string capability in capabilities)
            if (!requires.Any(node => node?.GetValue<string>() == capability)) requires.Add(capability);
    }
    private static string UniformSource(string calls) => MixedEffectPackageFixture.MaterialSource.Replace(
        "output.SetFill(token.WithRuby, TextState.Both, material);", "{\noutput.SetFill(token.WithRuby, TextState.Both, material);\n" + calls + "\n}");
    private static async Task<(byte[] Pixels, PreparedRenderFrame Frame)> RenderUniform(CompiledEffectPackage package,
        long timeUs = 1_000_000, int density = 1)
    {
        var snapshot = Snapshot(); var output = new RenderTarget { PixelWidth = 640 * density, PixelHeight = 360 * density };
        await using var source = await new PureFrameSourceFactory([package.CreateEffect("controller", new("uniform"), new Fx.EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await source.PrepareAsync(timeUs, Fx.OutputMode.Transparent, output);
        using var native = CompiledPackageGraphExports.Compile(package.Definition);
        using var resources = MaterialResources(native, package, new(0, 0, 640, 360));
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var rendered = renderer.Render(RenderRequest.At(snapshot, timeUs) with { PreparedFrame = frame, Target = output, TransparentBackground = true });
        return (rendered.PixelMemory.ToArray(), frame);
    }

    [Theory]
    [InlineData("UnsungFill", 1)] [InlineData("SungFill", 2)] [InlineData("Stroke", 1)]
    public async Task 真实作者节点uniform只改变指定部位且基础字与注音共用(string slot, int density)
    {
        string prefix = "output.SetStrokes(token.WithRuby,TextState.Both,new StrokeValue(4,material));\n";
        // 描边材质槽按两态拆开（0.112.82）：两态同写的描边在两个描边槽各有一份绑定，两槽写同一 uniform 才与直接写参数等价。
        string[] slots = slot == "Stroke" ? ["UnsungStroke", "SungStroke"] : [slot];
        string actualSource = UniformSource(prefix + string.Concat(slots.Select(s =>
            $"output.SetMaterialUniform(token.WithRuby,MaterialSlot.{s},frame.Resources.GetMaterial(\"ink\"),\"probe\",\"amount\",UniformValue.Float(.8));")));
        string material = "MaterialValue.PackageExport(frame.Resources.GetMaterial(\"ink\"),new([new(\"amount\",new ParameterValue.Number(.8))]))";
        string write = slot switch
        {
            "UnsungFill" => $"output.SetFill(token.WithRuby,TextState.Unsung,{material});",
            "SungFill" => $"output.SetFill(token.WithRuby,TextState.Sung,{material});",
            _ => $"output.SetStrokes(token.WithRuby,TextState.Both,new StrokeValue(4,{material}));",
        };
        var actual = await Compile(MixedEffectPackageFixture.MaterialContents(manifest => RequireCapabilities(manifest, "style.stroke", "material.uniform"), controllerSource: actualSource));
        var reference = await Compile(MixedEffectPackageFixture.MaterialContents(manifest => RequireCapabilities(manifest, "style.stroke"), controllerSource: UniformSource(prefix + write)));
        var a = await RenderUniform(actual, density: density); var b = await RenderUniform(reference, density: density);
        Assert.Equal(b.Pixels, a.Pixels); Assert.Equal(4, a.Frame.Materials.Length);
        var modified = a.Frame.Materials.Where(binding => !binding.Material.Uniforms.Values.IsEmpty).ToArray();
        Assert.Equal(slots, modified.Select(binding => binding.Slot.ToString()));
        foreach (var binding in modified)
        {
            Assert.Equal(.5, binding.Material.Parameters.GetNumber("amount"));
            Assert.Equal(.8, Assert.Single(binding.Material.Uniforms.Values).Value.Numbers[0]);
        }
    }

    [Fact]
    public async Task 多遍复用同程序与同名uniform仍按节点分别产生真实像素()
    {
        static void Graph(JsonObject graph)
        {
            var second = graph["nodes"]![0]!.DeepClone(); second["id"] = "after";
            second["children"]!["original"] = "node:probe"; second["uniforms"]!["amount"] = .5;
            graph["nodes"]!.AsArray().Add(second); graph["output"] = "node:after";
        }
        string source = UniformSource("""
            foreach(var slot in new[]{MaterialSlot.UnsungFill,MaterialSlot.SungFill}) {
                output.SetMaterialUniform(token.WithRuby,slot,frame.Resources.GetMaterial("ink"),"probe","amount",UniformValue.Float(.25));
                output.SetMaterialUniform(token.WithRuby,slot,frame.Resources.GetMaterial("ink"),"after","amount",UniformValue.Float(.8));
            }
            """);
        var actual = await Compile(MixedEffectPackageFixture.MaterialContents(manifest => RequireCapabilities(manifest, "material.uniform"), controllerSource: source, graphChange: Graph));
        var reference = await Compile(MixedEffectPackageFixture.MaterialContents(graphChange: graph =>
        { Graph(graph); graph["nodes"]![0]!["uniforms"]!["amount"] = .25; graph["nodes"]![1]!["uniforms"]!["amount"] = .8; }));
        var a = await RenderUniform(actual); var b = await RenderUniform(reference);
        Assert.Equal(b.Pixels, a.Pixels); Assert.All(a.Frame.Materials, binding => Assert.Equal(2, binding.Material.Uniforms.Values.Count));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 同实例再Set清除覆盖且坏早期写入不能由后来Set隐藏(bool invalidEarly)
    {
        string earlyWrite = invalidEarly
            ? "try { output.SetMaterialUniform(token.WithRuby,MaterialSlot.SungFill,frame.Resources.GetMaterial(\"ink\"),\"probe\",\"amount\",UniformValue.Float(1e100)); } catch (EffectOutputException) { }"
            : "output.SetMaterialUniform(token.WithRuby,MaterialSlot.SungFill,frame.Resources.GetMaterial(\"ink\"),\"probe\",\"amount\",UniformValue.Float(.9));";
        string source = UniformSource($"{earlyWrite}\noutput.SetFill(token.WithRuby,TextState.Sung,material);");
        var package = await Compile(MixedEffectPackageFixture.MaterialContents(manifest => RequireCapabilities(manifest, "material.uniform"), controllerSource: source));
        if (invalidEarly)
        {
            var failure = await Assert.ThrowsAsync<EffectFailureException>(() => RenderUniform(package));
            Assert.Equal("ShaderUniformNativeRange", failure.Code); Assert.Equal(new Fx.EffectInstanceId("uniform"), failure.InstanceId);
        }
        else
        {
            var a = await RenderUniform(package); var b = await RenderUniform(await Compile(MixedEffectPackageFixture.MaterialContents()));
            Assert.Equal(b.Pixels, a.Pixels); Assert.All(a.Frame.Materials, binding => Assert.Empty(binding.Material.Uniforms.Values));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 后实例可修改前实例同修订材质且再次替换清除覆盖与真实归属(bool reset)
    {
        string source = UniformSource("""
            if(frame.Parameters.GetNumber("amount")>.6)
                output.SetMaterialUniform(token.WithRuby,MaterialSlot.SungFill,frame.Resources.GetMaterial("ink"),"probe","amount",UniformValue.Float(.9));
            """).Replace("output.SetFill(token.WithRuby, TextState.Both, material);", "if(frame.Parameters.GetNumber(\"amount\")<=.6) output.SetFill(token.WithRuby, TextState.Both, material);");
        var package = await Compile(MixedEffectPackageFixture.MaterialContents(manifest => RequireCapabilities(manifest, "material.uniform"), controllerSource: source));
        var effects = new List<CompiledFrameEffect> { package.CreateEffect("controller", new("base"), new Fx.EffectBinding.Project(), 1),
            package.CreateEffect("controller", new("override"), new Fx.EffectBinding.Project(), 2, "{\"amount\":0.8}"u8.ToArray()) };
        if (reset) effects.Add(package.CreateEffect("controller", new("reset"), new Fx.EffectBinding.Project(), 3, "{\"amount\":0.3}"u8.ToArray()));
        var snapshot = Snapshot();
        await using var frames = await new PureFrameSourceFactory([.. effects], Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var prepared = await frames.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var sung = Assert.Single(prepared.Materials.Where(binding => binding.Slot == PreparedMaterialSlot.SungFill));
        var unsung = Assert.Single(prepared.Materials.Where(binding => binding.Slot == PreparedMaterialSlot.UnsungFill));
        Assert.Equal(new Fx.EffectInstanceId(reset ? "reset" : "override"), sung.InstanceId);
        Assert.Equal(new Fx.EffectInstanceId(reset ? "reset" : "base"), unsung.InstanceId);
        Assert.Equal(reset ? 0 : 1, sung.Material.Uniforms.Values.Count);
        using var native = CompiledPackageGraphExports.Compile(package.Definition); using var resources = MaterialResources(native, package, new(0, 0, 640, 360));
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.Contains(actual.PixelMemory.ToArray().Where((_, i) => i % 4 == 3), alpha => alpha > 100);
    }

    [Theory]
    [InlineData("node", "MaterialUniformNodeInvalid")] [InlineData("name", "MaterialUniformUnknown")]
    [InlineData("type", "ShaderUniformValueMismatch")] [InlineData("array", "ShaderUniformValueMismatch")]
    [InlineData("overflow", "ShaderUniformNativeRange")] [InlineData("underflow", "ShaderUniformNativeRange")]
    [InlineData("identity", "MaterialUniformIdentityMismatch")]
    public async Task 透明背景跳过之前也必须校验uniform且归属真实作者(string change, string code)
    {
        string node = change == "node" ? "missing" : "probe", name = change == "name" ? "missing" : "amount";
        string value = change switch { "type" => "UniformValue.Int(1)", "array" => "UniformValue.Array(new[]{UniformValue.Float(.5)})",
            "overflow" => "UniformValue.Float(1e100)", "underflow" => "UniformValue.Float(1e-100)", _ => "UniformValue.Float(.5)" };
        string source = UniformSource($"output.SetMaterialUniform(token.WithRuby,MaterialSlot.SungFill,frame.Resources.GetMaterial(\"ink\"),\"{node}\",\"{name}\",{value});");
        if (change == "identity") source = source.Replace("output.SetFill(token.WithRuby, TextState.Both, material);",
            "output.SetFill(token.WithRuby,TextState.Unsung,material);\noutput.SetFill(token.WithRuby,TextState.Sung,MaterialValue.Solid(new ColorRgba(1,1,1,1)));");
        var package = await Compile(MixedEffectPackageFixture.MaterialContents(manifest => RequireCapabilities(manifest, "material.uniform"), background: true, controllerSource: source));
        var failure = await Assert.ThrowsAsync<EffectFailureException>(() => RenderUniform(package));
        Assert.Equal(code, failure.Code); Assert.Equal(new Fx.EffectInstanceId("uniform"), failure.InstanceId);
        Assert.Equal(package.Artifact.ArtifactHash, failure.ArtifactHash); Assert.Equal(1_000_000, failure.TimeUs);
    }

    [Fact]
    public async Task 前奏标记使用真实部位覆盖且重新求值不保留前一帧值()
    {
        string source = UniformSource("output.SetMaterialUniform(token.WithRuby,MaterialSlot.UnsungFill,frame.Resources.GetMaterial(\"ink\"),\"probe\",\"amount\",UniformValue.Float(frame.TimeUs/20000000d));")
            .Replace("TokensInBinding", "MarksInBinding").Replace("token.WithRuby", "token.Target");
        var package = await Compile(MixedEffectPackageFixture.MaterialContents(manifest =>
        {
            RequireCapabilities(manifest, "material.uniform");
            manifest["exports"]![0]!["outputs"]![0]!["target"] = "textMark";
        }, controllerSource: source));
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)) with { EffectInputsFingerprint = Fingerprint };
        await using var frames = await new PureFrameSourceFactory([package.CreateEffect("controller", new("mark"), new Fx.EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var first = await frames.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, target);
        var next = await frames.PrepareAsync(10_500_000, Fx.OutputMode.Transparent, target);
        var repeated = await frames.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, target);
        Assert.All(first.Materials.Where(binding => binding.Slot == PreparedMaterialSlot.UnsungFill), binding => Assert.Equal(.5, Assert.Single(binding.Material.Uniforms.Values).Value.Numbers[0]));
        Assert.All(next.Materials.Where(binding => binding.Slot == PreparedMaterialSlot.UnsungFill), binding => Assert.Equal(.525, Assert.Single(binding.Material.Uniforms.Values).Value.Numbers[0]));
        Assert.NotEmpty(first.Materials);
        Assert.Equal(first.Materials.Length, repeated.Materials.Length);
        foreach (var binding in first.Materials)
        {
            var again = Assert.Single(repeated.Materials.Where(other => other.Target == binding.Target && other.Slot == binding.Slot));
            Assert.Equal(binding.InstanceId, again.InstanceId); Assert.Equal(binding.Material.Handle, again.Material.Handle);
            Assert.Equal(binding.Material.Uniforms, again.Material.Uniforms);
            Assert.Equal(binding.Material.Parameters.GetNumber("amount"), again.Material.Parameters.GetNumber("amount"));
        }
        using var native = CompiledPackageGraphExports.Compile(package.Definition); using var resources = MaterialResources(native, package, new(0, 0, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight));
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var actual = renderer.Render(RenderRequest.At(snapshot, first.TimeUs) with { PreparedFrame = first, TransparentBackground = true });
        using var replayed = renderer.Render(RenderRequest.At(snapshot, repeated.TimeUs) with { PreparedFrame = repeated, TransparentBackground = true });
        Assert.Equal(actual.PixelMemory.ToArray(), replayed.PixelMemory.ToArray());
        Assert.Contains(actual.PixelMemory.ToArray().Where((_, i) => i % 4 == 3), alpha => alpha > 100);
    }
}
