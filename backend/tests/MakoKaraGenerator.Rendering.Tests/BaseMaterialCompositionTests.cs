using System.Collections.Immutable;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class BaseMaterialCompositionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Base材质经Family再Line图读取已处理的完整Base及Ruby颜色(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package,mode:0)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot,compiled).CreateAsync(snapshot);
        var prepared = await BaseMaterialPackageFixture.Prepare(source,snapshot); var token = Assert.Single(prepared.FinalGeometry);
        using var device = BaseMaterialRuntimeTests.Device(gpu);
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiled.Packages[0].Definition,null,device);
        using var resources = BaseMaterialRuntimeTests.Resources(native);
        using var renderer = new SkiaRenderPipeline(null,null,null,resources,false,device,false);
        var family = new ImageEffectTarget.Token(token.WithRuby);
        var line = new ImageEffectTarget.Line(Assert.Single(prepared.FinalLineGeometry).Target);
        var parameters = new ParameterValues([new("amount",new ParameterValue.Number(.5))]);
        PreparedImageEffectBinding Bind(ImageEffectTarget target,string id) => new(new(id),"base-composition-test",
            new(target,native.Resources.GetGraph("tint"),parameters));
        var graphs = ImmutableArray.Create(Bind(family,"family-consumer"),Bind(line,"line-consumer"));
        byte[] expected = BaseMaterialPixelOracle.Expected(renderer, snapshot, prepared, .5, .5, 1,
            strokeOnly: false, postRgbFactors: [.5, .5]);
        using var actual = BaseMaterialRuntimeTests.Render(renderer,snapshot,prepared.WithImageEffects(graphs));
        // A separate transparent-Base parent frame supplies the Ruby reference under the original graphs.
        var clear = MaterialValue.Solid(new(0,0,0,0));
        var rubyReference = BaseMaterialPackageFixture.Parent(prepared).WithTextPartitions([], [new(token.BaseOnly,
            TestStyle.Update(clear, clear, [], new(null), new(null)))],[]).WithImageEffects(graphs);
        using var ruby = BaseMaterialRuntimeTests.Render(renderer,snapshot,rubyReference);
        BaseMaterialRuntimeTests.RubyEqual(ruby,actual,Assert.Single(rubyReference.FinalGeometry));
        BaseMaterialPixelOracle.Compare(expected,actual,2);
        using var plain = BaseMaterialRuntimeTests.Render(renderer,snapshot,prepared);
        Assert.False(plain.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Base材质沿FamilyBaseCluster仿射与Warp仅投影一次且Ruby保持父族(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package,mode:11)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot,compiled).CreateAsync(snapshot);
        var frame = await BaseMaterialPackageFixture.Prepare(source,snapshot); var token = Assert.Single(frame.FinalGeometry);
        var cluster = token.Clusters.First(item => item.Target.RubyId is null);
        var parent = BaseMaterialPackageFixture.Parent(frame,[new(token.WithRuby,Affine2.Translation(9,-4))]);
        var basic = new BaseTokenTransform(token.BaseOnly,Affine2.Translation(5,4));
        var leaf = new TextClusterTransform(cluster.Target,Affine2.Translation(4,5));
        var material = parent.WithTextPartitions([basic],frame.BaseStyles,[leaf],frame.BaseMaterials);
        Rect2 domain = new(-100,-200,800,400);
        GridWarp warp = new(domain,1,1,[new(domain.Left-15,domain.Top+9),new(domain.Right-15,domain.Top+9),
            new(domain.Left-15,domain.Bottom+9),new(domain.Right-15,domain.Bottom+9)]);
        var warpBinding = new PreparedTextWarpBinding(new("warp-owner"),"warp-artifact",null,new TextTarget.Base(token.BaseOnly),warp);
        using var device = BaseMaterialRuntimeTests.Device(gpu);
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiled.Packages[0].Definition,null,device);
        using var resources = BaseMaterialRuntimeTests.Resources(native);
        using var renderer = new SkiaRenderPipeline(null,null,null,resources,false,device,false);
        var warped = material.WithTextWarps([warpBinding]);
        byte[] expected = BaseMaterialPixelOracle.Expected(renderer,snapshot,warped,.5,.5,1);
        using var actual = BaseMaterialRuntimeTests.Render(renderer,snapshot,warped);
        using var family = BaseMaterialRuntimeTests.Render(renderer,snapshot,parent);
        BaseMaterialRuntimeTests.RubyEqual(family,actual,Assert.Single(parent.FinalGeometry));
        BaseMaterialPixelOracle.Compare(expected,actual,2);
        var mapped = Assert.Single(material.WithTextWarps([warpBinding]).FinalGeometry).Clusters.Single(item => item.Target == cluster.Target).Geometry;
        var point = cluster.Geometry.AdvanceBounds.Center;
        var expectedPoint = token.BaseGeometry.BaseWorldMatrix.Map(Affine2.Translation(9,-4).Map(
            basic.LocalMatrix.Map(warp.MapPoint(leaf.LocalMatrix.Map(point)))));
        Assert.InRange(mapped.MapPoint(point).X,expectedPoint.X-.0001,expectedPoint.X+.0001);
        Assert.InRange(mapped.MapPoint(point).Y,expectedPoint.Y-.0001,expectedPoint.Y+.0001);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Base材质Warp捕获中取消及原生失败保留真实owner并在同会话恢复完整像素(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package,mode:11)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot,compiled).CreateAsync(snapshot);
        var frame = await BaseMaterialPackageFixture.Prepare(source,snapshot);
        var basic = Assert.Single(frame.FinalGeometry).BaseOnly;
        Rect2 domain = new(-100,-200,800,400);
        frame = frame.WithTextWarps([new(new("warp-owner"),"warp-artifact",null,new TextTarget.Base(basic),
            new GridWarp(domain,1,1,[new(domain.Left+5,domain.Top),new(domain.Right+5,domain.Top),
                new(domain.Left+5,domain.Bottom),new(domain.Right+5,domain.Bottom)]))]);
        using var device = new ObservedDevice(gpu);
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiled.Packages[0].Definition,null,device);
        using var resources = BaseMaterialRuntimeTests.Resources(native); using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null,null,budget,resources,false,device,false);
        using var cancellation = new CancellationTokenSource();
        bool canceledInMaterial = false;
        device.Created = 0; device.OnCreate = _ => { if (InMaterialWarp()) { canceledInMaterial = true; cancellation.Cancel(); } };
        var error = Assert.Throws<OperationCanceledException>(() => renderer.Render(BaseMaterialRuntimeTests.Request(snapshot,frame)
            with { CancellationToken = cancellation.Token }));
        Assert.Equal(cancellation.Token,error.CancellationToken); Assert.True(canceledInMaterial); Assert.True(budget.Usage.PeakBytes > 0);
        Assert.Equal(0,budget.Usage.ActiveSurfaces); Assert.Equal(0,budget.Usage.EstimatedBytes);
        bool failedInMaterial = false;
        device.OnCreate = _ => { if (InMaterialWarp()) { failedInMaterial = true; throw new InvalidOperationException("injected-native-create"); } };
        var nativeFailure = Assert.Throws<EffectFailureException>(() => BaseMaterialRuntimeTests.Render(renderer,snapshot,frame));
        Assert.True(failedInMaterial); Assert.Equal("material",nativeFailure.InstanceId.Value); Assert.Equal("RenderMaterials",nativeFailure.Stage);
        Assert.Equal(new TextTarget.Base(basic),Assert.IsType<PreparedFrameException>(nativeFailure.InnerException).TextTarget);
        Assert.Equal(0,budget.Usage.ActiveSurfaces); Assert.Equal(0,budget.Usage.EstimatedBytes);
        device.OnCreate = null;
        using var recovered = BaseMaterialRuntimeTests.Render(renderer,snapshot,frame);
        using var repeated = BaseMaterialRuntimeTests.Render(renderer,snapshot,frame);
        Assert.Equal(recovered.PixelMemory.ToArray(),repeated.PixelMemory.ToArray());
        Assert.Equal(0,budget.Usage.ActiveSurfaces); Assert.Equal(0,budget.Usage.EstimatedBytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Base材质共享预算精确峰值准入少一字节失败且归实际材质实例(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package,"actual-material",11)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot,compiled).CreateAsync(snapshot);
        var frame = await BaseMaterialPackageFixture.Prepare(source,snapshot);
        using var device = BaseMaterialRuntimeTests.Device(gpu);
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiled.Packages[0].Definition,null,device);
        using var resources = BaseMaterialRuntimeTests.Resources(native);
        long peak;
        using (var baselineBudget = new ImageSurfaceBudgetSession())
        using (var baseline = new SkiaRenderPipeline(null,null,baselineBudget,resources,false,device,false))
        { using var pixels = BaseMaterialRuntimeTests.Render(baseline,snapshot,frame); peak = baselineBudget.Usage.PeakBytes; }
        Assert.True(peak > 0);
        foreach (long bytes in new[] { peak,peak-1 })
        {
            using var budget = new ImageSurfaceBudgetSession(new() { MaximumBytes = bytes });
            using var renderer = new SkiaRenderPipeline(null,null,budget,resources,false,device,false);
            if (bytes == peak) { using var pixels = BaseMaterialRuntimeTests.Render(renderer,snapshot,frame); }
            else
            {
                var error = Assert.Throws<EffectFailureException>(() => BaseMaterialRuntimeTests.Render(renderer,snapshot,frame));
                Assert.Equal("ImageSurfaceBudgetExceeded",error.Code); Assert.Equal("actual-material",error.InstanceId.Value);
                Assert.Equal("RenderMaterials",error.Stage);
                Assert.IsType<TextTarget.Base>(Assert.IsType<PreparedFrameException>(error.InnerException).TextTarget);
            }
            Assert.Equal(0,budget.Usage.ActiveSurfaces); Assert.Equal(0,budget.Usage.EstimatedBytes);
        }
    }

    private static bool InMaterialWarp()
    {
        var methods = new System.Diagnostics.StackTrace().GetFrames().Select(frame => frame.GetMethod()?.Name ?? "").ToArray();
        return methods.Any(name => name.Contains("CaptureSceneMaterialRegion",StringComparison.Ordinal))
            && methods.Any(name => name.Contains("DrawWarpedTextLayer",StringComparison.Ordinal));
    }

    private sealed class ObservedDevice(bool gpu) : ISkiaRenderDevice
    {
        private readonly ISkiaRenderDevice _inner = BaseMaterialRuntimeTests.Device(gpu);
        internal int Created { get; set; }
        internal Action<int>? OnCreate { get; set; }
        public object SynchronizationRoot => _inner.SynchronizationRoot;
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public SKSurface CreateSurface(SKImageInfo info)
        {
            var surface = _inner.CreateSurface(info); Created++;
            try { OnCreate?.Invoke(Created); return surface; } catch { surface.Dispose(); throw; }
        }
        public void FlushAndSubmit(SKSurface surface) => _inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) => _inner.RasterizeImage(image);
        public SKImage? CreateTextureImage(SKImage image,bool mipmapped) => _inner.CreateTextureImage(image,mipmapped);
        public bool ReadPixels(SKSurface surface,SKImageInfo info,IntPtr destination,int rowBytes) => _inner.ReadPixels(surface,info,destination,rowBytes);
        public bool ReadPixels(SKImage image,SKImageInfo info,IntPtr destination,int rowBytes) => _inner.ReadPixels(image,info,destination,rowBytes);
        public bool ReadOwnedImagePixels(SKImage image,SKImageInfo info,IntPtr destination,int rowBytes) => _inner.ReadOwnedImagePixels(image,info,destination,rowBytes);
        public void Dispose() => _inner.Dispose();
    }
}
