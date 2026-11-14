using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MixedEffectPackageTests
{
    private static SceneImageEffectResources MaterialResources(CompiledPackageGraphExports native, CompiledEffectPackage package, GraphRect bounds)
        => new([native.CreateProgram(package.Resources.GetGraph("tint"), bounds)], null,
            [native.CreateMaterialProgram(package.Resources.GetMaterial("ink"), bounds)]);

    [Theory]
    [InlineData("masked", false, 1)] [InlineData("masked", true, 2)]
    [InlineData("unmasked", false, 2)] [InlineData("unmasked", true, 1)]
    public async Task 真正CSharp材质经准备帧主Render限制在字形并保持注音与局部alpha(string mode, bool stroke, int density)
    {
        var source = MixedEffectPackageFixture.MaterialSource;
        if (stroke) source = source.Replace("output.SetFill(token.WithRuby, TextState.Both, material);",
            "{\noutput.SetFill(token.WithRuby, TextState.Sung, material);\noutput.SetStrokes(token.WithRuby, TextState.Both, new StrokeValue(4, material));\n}");
        string? shader = mode == "unmasked" ? "uniform float amount; uniform shader original; half4 main(float2 p) { return half4(amount,0,original.eval(p).a*.001,1); }" : null;
        var package = await Compile(MixedEffectPackageFixture.MaterialContents(manifest =>
        {
            manifest["exports"]![1]!["coverageMode"] = mode;
            if (stroke) RequireCapabilities(manifest, "style.stroke");
        }, controllerSource: source, shaderSource: shader));
        Assert.True(package.Succeeded, string.Join('\n',package.Artifact.Diagnostics));
        var snapshot = Snapshot(); var effect = package.CreateEffect("controller",new("material"),new Fx.EffectBinding.Project(),1);
        await using var session = await new PureFrameSourceFactory([effect],Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(snapshot);
        var output = new RenderTarget { PixelWidth=640*density, PixelHeight=360*density };
        var prepared = await session.PrepareAsync(1_000_000,Fx.OutputMode.Transparent,output);
        // 两态同写的描边在两个描边槽各有一份绑定（0.112.82）。
        Assert.Equal(stroke ? 3 : 2,prepared.Materials.Length);
        var target = Assert.Single(prepared.FinalGeometry).WithRuby;
        Assert.IsType<Fx.MaterialValue.PackageMaterial>(prepared.GetTokenAppearance(target).Sung.Fill);
        Assert.Equal("MaterialAppearanceRequiresCoordinator",Assert.Throws<PreparedFrameException>(()=>prepared.GetTokenStyle(target)).Code);
        Assert.Equal(prepared.Materials,prepared.WithVisualOutput([],prepared.Diagnostics).Materials);
        using var native=CompiledPackageGraphExports.Compile(package.Definition);
        using var resources=MaterialResources(native,package,new(0,0,640,360));
        using var budget=new ImageSurfaceBudgetSession();
        using var renderer=new SkiaRenderPipeline(null,null,budget,resources);
        var request=RenderRequest.At(snapshot,prepared.TimeUs) with { PreparedFrame=prepared,TransparentBackground=true,Target=output };
        using var actual=renderer.Render(request);
        Assert.Contains(actual.PixelMemory.ToArray().Where((_,i)=>i%4==3),alpha=>alpha>0);
        Assert.True(actual.TryGetPixel(0,0,out var corner));Assert.Equal(0,corner.Alpha);
        Assert.Empty(actual.MaterialDiagnostics);
        Assert.Equal(0,budget.Usage.ActiveSurfaces);
        Assert.Equal(0,budget.Usage.EstimatedBytes);
        Assert.Equal("MaterialProcessedCaptureRequired",Assert.Throws<PreparedFrameException>(()=>renderer.CaptureSceneColorPart(request,
            new SceneCaptureTarget.Token(target),SceneColorPart.BaseFill,new(0,0,640,360),GraphExecutionBudget.Default)).Code);
        using var region=renderer.CaptureSceneMaterialRegion(request,new SceneCaptureTarget.Token(target),SceneColorPart.BaseFill,
            package.Definition.GetMaterial("ink").Fallback,null,new(0,0,640,360),GraphExecutionBudget.Default);
        Assert.Contains(Bytes(region.Region).Where((_,i)=>i%4==3),alpha=>alpha==255);
        using var missing=new SkiaRenderPipeline();
        var error=Assert.Throws<EffectFailureException>(()=>missing.Render(request));
        Assert.Equal("MaterialResourcesRequired",error.Code);Assert.Equal(effect.Id,error.InstanceId);
    }

    [Theory]
    [InlineData("solid")] [InlineData("gradient")]
    public async Task 主画布透明材质回退保留真实提示缓存与包内alpha(string appearance)
    {
        var package=await Compile(MixedEffectPackageFixture.MaterialContents(background:true,
            fallback:appearance=="gradient" ? MixedEffectPackageFixture.Gradient() : null));
        var snapshot=Snapshot();
        await using var session=await new PureFrameSourceFactory([package.CreateEffect("controller",new("fallback"),new Fx.EffectBinding.Project(),1)],
            Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(snapshot);
        var prepared=await session.PrepareAsync(1_000_000,Fx.OutputMode.Transparent,RenderTarget.FromComposition(snapshot.Composition));
        using var native=CompiledPackageGraphExports.Compile(package.Definition);using var resources=MaterialResources(native,package,new(0,0,640,360));
        using var renderer=new SkiaRenderPipeline(null,null,null,resources);var cached=new CachedRenderPipeline(renderer);
        var request=RenderRequest.At(snapshot,prepared.TimeUs) with { PreparedFrame=prepared,TransparentBackground=true };
        using var first=cached.Render(request);using var second=cached.Render(request);
        Assert.Equal(first.PixelMemory.ToArray(),second.PixelMemory.ToArray());
        var warning=Assert.Single(second.MaterialDiagnostics);Assert.Equal("ink",warning.Material.Identity.ExportId);
        Assert.Equal("TransparentBackgroundDependency",warning.Code);Assert.Equal(new Fx.EffectInstanceId("fallback"),warning.InstanceId);
        var target=Assert.Single(prepared.FinalGeometry).WithRuby;
        using var baseRegion=renderer.CaptureSceneMaterialRegion(request,new SceneCaptureTarget.Token(target),SceneColorPart.BaseFill,
            package.Definition.GetMaterial("ink").Fallback,null,new(0,0,640,360),GraphExecutionBudget.Default);
        byte[] fallback=Bytes(baseRegion.TargetColor), actual=first.PixelMemory.ToArray();
        int checkedPixels=0;
        for(int i=3;i<fallback.Length;i+=4) if(fallback[i]>100)
        { Assert.True(Math.Abs(actual[i]-fallback[i]*.7)<=2,$"Pixel ({i/4%640},{i/4/640}): actual {actual[i]}, fallback {fallback[i]}, expected alpha {fallback[i]*.7}.");checkedPixels++; }
        Assert.True(checkedPixels>50);
    }

    [Theory]
    [InlineData("missing","PackageParameterSetMismatch")]
    [InlineData("range","PackageParameterOutOfRange")]
    [InlineData("region","PackageMaterialRegionInvalid")]
    public async Task 真正材质包在作者求值时校验完整参数与绑定区域(string change,string code)
    {
        var source=MixedEffectPackageFixture.MaterialSource;
        if(change=="missing")source=source.Replace("new([new(\"amount\", new ParameterValue.Number(frame.Parameters.GetNumber(\"amount\")))])","ParameterValues.Empty");
        if(change=="range")source=source.Replace("frame.Parameters.GetNumber(\"amount\")","2");
        var package=await Compile(MixedEffectPackageFixture.MaterialContents(manifest=>
        { if(change=="region")manifest["exports"]![1]!["regions"]=new JsonArray("stroke"); },controllerSource:source));
        Assert.True(package.Succeeded,string.Join('\n',package.Artifact.Diagnostics));
        await using var session=await new PureFrameSourceFactory([package.CreateEffect("controller",new("invalid-material"),new Fx.EffectBinding.Project(),1)],
            Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(Snapshot());
        var failure=await Assert.ThrowsAsync<EffectFailureException>(async()=>await session.PrepareAsync(1_000_000,Fx.OutputMode.Transparent,RenderTarget.FromComposition(Snapshot().Composition)));
        Assert.Equal(code,failure.Code);Assert.Equal(new Fx.EffectInstanceId("invalid-material"),failure.InstanceId);
    }

    [Fact]
    public async Task 后续alpha写入不抢走材质归属且恢复纯色会删除区域绑定()
    {
        var material=await Compile(MixedEffectPackageFixture.MaterialContents());
        string alphaSource=MixedEffectPackageFixture.MaterialSource.Replace("output.SetFill(token.WithRuby, TextState.Both, material);","output.MultiplyOpacity(token.WithRuby,.5);");
        var alpha=await Compile(MixedEffectPackageFixture.MaterialContents(manifest=>RequireCapabilities(manifest,"token.opacity"),controllerSource:alphaSource));
        var snapshot=Snapshot();
        await using var session=await new PureFrameSourceFactory([
            material.CreateEffect("controller",new("owner"),new Fx.EffectBinding.Project(),1),alpha.CreateEffect("controller",new("alpha"),new Fx.EffectBinding.Project(),1)],
            Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(snapshot);
        var prepared=await session.PrepareAsync(1_000_000,Fx.OutputMode.Transparent,RenderTarget.FromComposition(snapshot.Composition));
        Assert.All(prepared.Materials,binding=>Assert.Equal(new Fx.EffectInstanceId("owner"),binding.InstanceId));
        Assert.Equal(.35,prepared.GetTokenAppearance(Assert.Single(prepared.FinalGeometry).WithRuby).Opacity,6);
        using var native=CompiledPackageGraphExports.Compile(material.Definition);using var resources=MaterialResources(native,material,new(0,0,640,360));
        using var renderer=new SkiaRenderPipeline(null,null,null,resources);
        using var actual=renderer.Render(RenderRequest.At(snapshot,prepared.TimeUs) with { PreparedFrame=prepared,TransparentBackground=true });
        Assert.Contains(actual.PixelMemory.ToArray().Where((_,i)=>i%4==3),a=>a>0);
        string resetSource=MixedEffectPackageFixture.MaterialSource.Replace("output.SetFill(token.WithRuby, TextState.Both, material);",
            "{\noutput.SetFill(token.WithRuby, TextState.Unsung, frame.Baseline.Get(token.WithRuby).Unsung.Fill);\noutput.SetFill(token.WithRuby, TextState.Sung, frame.Baseline.Get(token.WithRuby).Sung.Fill);\n}");
        var reset=await Compile(MixedEffectPackageFixture.MaterialContents(controllerSource:resetSource));
        await using var resetSession=await new PureFrameSourceFactory([material.CreateEffect("controller",new("first"),new Fx.EffectBinding.Project(),1),
            reset.CreateEffect("controller",new("reset"),new Fx.EffectBinding.Project(),1)],Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(snapshot);
        Assert.Empty((await resetSession.PrepareAsync(1_000_000,Fx.OutputMode.Transparent,RenderTarget.FromComposition(snapshot.Composition))).Materials);
    }
    [Fact]
    public async Task 包材质产物进入后续整行图而非重新读取源样式()
    {
        string source=MixedEffectPackageFixture.MaterialSource.Replace("    }\n}", """
                foreach(var line in frame.Presentations.LinesInBinding)
                    output.BindGraph(line.Target,new(frame.Resources.GetGraph("tint"),new([new("amount",new ParameterValue.Number(.5))]),GraphUniformValues.Empty,GraphTransformValues.Empty));
            }
        }
        """);
        var package=await Compile(MixedEffectPackageFixture.MaterialContents(manifest=>
        {
            RequireCapabilities(manifest, "image.graph");
            var output=MixedEffectPackageFixture.Output("line");output.Remove("bypass");manifest["exports"]![0]!["outputs"]!.AsArray().Add(output);
        },controllerSource:source));
        Assert.True(package.Succeeded,string.Join('\n',package.Artifact.Diagnostics));
        var snapshot=Snapshot();await using var session=await new PureFrameSourceFactory([package.CreateEffect("controller",new("chain"),new Fx.EffectBinding.Project(),1)],
            Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(snapshot);
        var prepared=await session.PrepareAsync(1_000_000,Fx.OutputMode.Transparent,RenderTarget.FromComposition(snapshot.Composition));
        Assert.Single(prepared.ImageEffects);
        using var native=CompiledPackageGraphExports.Compile(package.Definition);using var resources=MaterialResources(native,package,new(0,0,640,360));
        using var renderer=new SkiaRenderPipeline(null,null,null,resources);
        var request=RenderRequest.At(snapshot,prepared.TimeUs) with { PreparedFrame=prepared,TransparentBackground=true };
        using var after=renderer.Render(request);using var before=renderer.Render(request with { PreparedFrame=prepared.WithImageEffects([]) });
        byte[] a=after.PixelMemory.ToArray(),b=before.PixelMemory.ToArray();int visible=0;
        for(int i=0;i<a.Length;i++)
        {
            if(i%4==3) { Assert.InRange(Math.Abs(a[i]-b[i]),0,1);if(a[i]>0)visible++; }
            else Assert.InRange(Math.Abs(a[i]-b[i]*.5),0,2);
        }
        Assert.True(visible>100);
    }

    [Fact]
    public async Task 材质坏覆盖与资源预算失败保留逐属性作者并全部回收()
    {
        var package=await Compile(MixedEffectPackageFixture.MaterialContents(shaderSource:
            "uniform float amount; uniform shader original; half4 main(float2 p) { return half4(amount,0,original.eval(p).a*.001,1); }"));
        string alphaSource=MixedEffectPackageFixture.MaterialSource.Replace("output.SetFill(token.WithRuby, TextState.Both, material);","output.MultiplyOpacity(token.WithRuby,.5);");
        var alpha=await Compile(MixedEffectPackageFixture.MaterialContents(manifest=>RequireCapabilities(manifest,"token.opacity"),controllerSource:alphaSource));
        var snapshot=Snapshot();await using var session=await new PureFrameSourceFactory([package.CreateEffect("controller",new("bad-material"),new Fx.EffectBinding.Project(),1),
            alpha.CreateEffect("controller",new("later-alpha"),new Fx.EffectBinding.Project(),1)],Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(snapshot);
        var prepared=await session.PrepareAsync(1_000_000,Fx.OutputMode.Transparent,RenderTarget.FromComposition(snapshot.Composition));
        using var native=CompiledPackageGraphExports.Compile(package.Definition);using var resources=MaterialResources(native,package,new(0,0,640,360));
        var request=RenderRequest.At(snapshot,prepared.TimeUs) with { PreparedFrame=prepared,TransparentBackground=true };
        foreach(int surfaces in new[]{512,2})
        {
            using var budget=new ImageSurfaceBudgetSession(new(){MaximumSurfaces=surfaces});
            using var renderer=new SkiaRenderPipeline(null,null,budget,resources);
            var failure=Assert.Throws<EffectFailureException>(()=>renderer.Render(request));
            Assert.Equal(surfaces==2 ? "ImageSurfaceBudgetExceeded" : "MaterialOutputOutsideRegion",failure.Code);
            Assert.Equal(new Fx.EffectInstanceId("bad-material"),failure.InstanceId);Assert.Equal(package.Artifact.ArtifactHash,failure.ArtifactHash);
            Assert.Equal(0,budget.Usage.ActiveSurfaces);Assert.Equal(0,budget.Usage.EstimatedBytes);
        }
    }

    [Fact]
    public async Task 真实前奏标记消费材质且共同alpha只应用一次()
    {
        string source=MixedEffectPackageFixture.MaterialSource.Replace("TokensInBinding","MarksInBinding").Replace("token.WithRuby","token.Target");
        var package=await Compile(MixedEffectPackageFixture.MaterialContents(manifest=>manifest["exports"]![0]!["outputs"]![0]!["target"]="textMark",controllerSource:source));
        var project=LeadInFixture.Create(true,true);var snapshot=ProjectSnapshot.FromProject(project) with {EffectInputsFingerprint=Fingerprint};
        await using var session=await new PureFrameSourceFactory([package.CreateEffect("controller",new("mark-material"),new Fx.EffectBinding.Project(),1)],
            Fingerprint,static()=>new SkiaRenderPipeline()).CreateAsync(snapshot);
        var prepared=await session.PrepareAsync(10_000_000,Fx.OutputMode.Transparent,RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(6,prepared.Materials.Length);
        using var native=CompiledPackageGraphExports.Compile(package.Definition);
        using var resources=MaterialResources(native,package,new(0,0,snapshot.Composition.CanvasWidth,snapshot.Composition.CanvasHeight));
        using var renderer=new SkiaRenderPipeline(null,null,null,resources);
        var request=RenderRequest.At(snapshot,prepared.TimeUs) with {PreparedFrame=prepared,TransparentBackground=true};
        using var actual=renderer.Render(request);Assert.Contains(actual.PixelMemory.ToArray().Where((_,i)=>i%4==3),a=>a>0);
        var mark=prepared.FinalMarkGeometry[0];Assert.IsType<Fx.MaterialValue.PackageMaterial>(prepared.GetMarkAppearance(mark.Target).Unsung.Fill);
        using var capture=renderer.CaptureSceneMaterialRegion(request,new SceneCaptureTarget.Mark(mark.Target),SceneColorPart.BaseFill,
            package.Definition.GetMaterial("ink").Fallback,null,new(0,0,snapshot.Composition.CanvasWidth,snapshot.Composition.CanvasHeight),GraphExecutionBudget.Default);
        var pixels=Bytes(capture.TargetColor);int checkedPixels=0;var actualPixels=actual.PixelMemory.ToArray();
        for(int i=3;i<pixels.Length;i+=4)if(pixels[i]>100) { Assert.InRange(Math.Abs(pixels[i]-actualPixels[i]),0,2);checkedPixels++; }
        Assert.True(checkedPixels>10);
    }

}
