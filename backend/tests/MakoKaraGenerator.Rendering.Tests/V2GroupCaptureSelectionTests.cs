using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;
using F = MakoKaraGenerator.Rendering.Tests.V2GroupGraphPackageFixture;

namespace MakoKaraGenerator.Rendering.Tests;

// Real v3/native package fixtures for the empty selection distinction.
// The graphs deliberately produce opaque green if Execute is reached.
public sealed class V2GroupCaptureSelectionTests
{
    private static EffectPackageContents Package(string mode) => F.Package(true, graphChange: graph =>
    {
        graph["inputs"]![0]!["capture"]!["includeDescendantGroups"] = false;
        if (mode == "mixed")
        {
            var selected = graph["inputs"]![0]!.DeepClone(); selected["id"] = "selected";
            selected["capture"]!["includeDescendantGroups"] = true; graph["inputs"]!.AsArray().Add(selected);
            graph["nodes"]![0]!["children"]!["selected"] = "input:selected";
        }
        if (mode == "none") { graph["inputs"] = new JsonArray(); graph["nodes"]![0]!["children"] = new JsonObject(); }
    }, shaderSource: mode == "mixed"
        ? "uniform float amount; uniform shader original; uniform shader selected; half4 main(float2 p) { return selected.eval(p)*amount + original.eval(p)*0; }"
        : mode == "none" ? "uniform float amount; half4 main(float2 p) { return half4(0,amount,0,amount); }"
        : "uniform float amount; uniform shader original; half4 main(float2 p) { return half4(0,amount,0,amount)+original.eval(p)*0; }",
        programChange: program =>
        {
            if (mode == "mixed") program["children"]!.AsArray().Add(new JsonObject { ["name"] = "selected", ["type"] = "color" });
            if (mode == "none") program["children"] = new JsonArray();
        });
    private static void Close(byte[] expected, byte[] actual, int tolerance = 2)
    { Assert.Equal(expected.Length, actual.Length); for (int i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, tolerance); }

    [Theory]
    [InlineData("empty")] [InlineData("mixed")] [InlineData("none")]
    public async Task 非空scope显式空目标并集为Info无操作部分空仍执行而无目标输入才整scope替换(string mode)
    {
        var package = Package(mode); var basis = F.Snapshot(package, []); string root = F.Root(basis);
        var snapshot = F.Snapshot(package, [F.Instance(package, true, root, amount: 1, binding: new(ProjectEffectBindingKind.Node, root))]);
        var compiled = await F.Compile(snapshot); await using var session = await F.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await F.Prepare(session, snapshot); Assert.Single(frame.ImageEffects);
        Assert.NotEmpty(frame.FinalGroupGeometry.Single(item => item.Target.NodeId.Value == root).WorldInkCorners);
        // Primary root has only container/slot groups immediately below it.
        Assert.DoesNotContain(frame.Scene.Lines, line => line.ParentId == root);
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package)); using var resources = F.Resources(native);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var expected = F.Pixels(snapshot, frame.WithImageEffects([]), renderer);
        using var actual = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        if (mode == "empty")
        {
            Close(expected, actual.PixelMemory.ToArray());
            var info = Assert.Single(actual.SceneDiagnostics.Where(item => item.Diagnostic.Code == "GroupImageEffectNoSelectedChildren"));
            Assert.Equal(DiagnosticSeverity.Info, info.Diagnostic.Severity); Assert.Equal("group-instance", info.InstanceId.Value); Assert.NotNull(info.PackageIdentity);
            Assert.DoesNotContain(actual.SceneDiagnostics, item => item.Diagnostic.Code == "GroupImageEffectNoVisibleChildren");
        }
        else if (mode == "mixed")
        {
            Close(expected, actual.PixelMemory.ToArray()); Assert.DoesNotContain(actual.SceneDiagnostics, item => item.Diagnostic.Code == "GroupImageEffectNoSelectedChildren");
        }
        else
        {
            var pixels = actual.PixelMemory.ToArray(); Assert.InRange(pixels[1], (byte)254, (byte)255); Assert.Equal((byte)255, pixels[3]);
            Assert.DoesNotContain(actual.SceneDiagnostics, item => item.Diagnostic.Code == "GroupImageEffectNoSelectedChildren");
        }
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
    [Fact]
    public async Task 显式空选择仍先验证资源且不能吞掉缺失native程序错误()
    {
        var package = Package("empty"); var basis = F.Snapshot(package, []); string root = F.Root(basis);
        var snapshot = F.Snapshot(package, [F.Instance(package, true, root, binding: new(ProjectEffectBindingKind.Node, root))]);
        var compiled = await F.Compile(snapshot); await using var session = await F.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await F.Prepare(session, snapshot);
        using var budget = new ImageSurfaceBudgetSession(); using var resources = new SceneImageEffectResources([]);
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var error = Assert.Throws<EffectFailureException>(() => F.Pixels(snapshot, frame, renderer));
        Assert.Equal("ImageEffectResourceUnavailable", error.Code); Assert.Equal("group-instance", error.InstanceId.Value); Assert.NotNull(error.PackageIdentity);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
}
