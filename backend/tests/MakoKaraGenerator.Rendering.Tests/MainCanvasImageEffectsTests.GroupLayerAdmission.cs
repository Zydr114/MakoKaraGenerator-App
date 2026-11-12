using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using Fx = MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    [Fact]
    public void G12_真实Base两态拆层在第513来源输入前拒绝并归实际Base材质owner()
    {
        var contents = BaseMaterialPackageFixture.Package(); var definition = EffectPackageDefinition.Read(contents);
        using var native = CompiledPackageGraphExports.Compile(definition); var handle = native.Resources.GetMaterial("ink");
        var material = (Fx.MaterialValue.PackageMaterial)Fx.MaterialValue.PackageExport(handle,
            new Fx.ParameterValues([new("amount",new Fx.ParameterValue.Number(.5))]));
        var tokens = Enumerable.Range(0,129).Select(index => LyricToken.Create("token-"+index,"A",new(0,4_000_000),TimingStatus.Confirmed)).ToArray();
        var style = LyricStyle.Default with { FontFamilyName="DejaVu Sans",FontSizePx=4,Stroke=null,Shadow=null,Glow=null,Opacity=1 };
        var snapshot = ProjectSnapshot.FromProject(Project.Create("layer-gate",Composition.Create(640,360,Core.Time.FrameRate.Fps30,outputDurationUs:5_000_000),
            LyricsDocument.Create([LyricLine.Create("line",new string('A',129),new(0,5_000_000),TimingStatus.Confirmed,tokens)]),lyricStyle:style)
            with { Effects=new([contents],[]),EnterAnimation=null });
        var request = Request(snapshot,width:64,height:36,time:2_000_000); var original = request.PreparedFrame!;
        var owner = new PreparedMaterialOwner.Script(new("actual-base-writer"),"actual-base-artifact");
        var geometries = original.FinalGeometry.ToArray(); Assert.Equal(129,geometries.Length);
        var frame = original.WithTextPartitions([], [..geometries.Select(token => new Fx.V2.BaseTokenStyle(token.BaseOnly, TestStyle.Update(material, material)))],[],
            [..geometries.SelectMany(token => new[] { new PreparedBaseMaterialBinding(owner,token.BaseOnly,PreparedMaterialSlot.UnsungFill,material),
                new PreparedBaseMaterialBinding(owner,token.BaseOnly,PreparedMaterialSlot.SungFill,material) })]);
        request = request with { PreparedFrame=frame.WithImageEffects([Bind("parent-trigger",GroupRoot(request))]) };
        using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"),identity,GroupWhole,Bounds)],null,[native.CreateMaterialProgram(handle,Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces=4096,MaximumBytes=64L*1024*1024 });
        using var renderer = GroupRenderer(resources,budget);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(request));
        Assert.Equal("GroupCompositionBudgetExceeded",error.Code); Assert.Equal("actual-base-writer",error.InstanceId.Value);
        Assert.Equal("actual-base-artifact",error.ArtifactHash); Assert.Equal("RenderMaterials",error.Stage);
        var inner = Assert.IsType<PreparedFrameException>(error.InnerException);
        Assert.Contains("513/512",inner.Message); Assert.IsType<Fx.V2.TextTarget.Base>(inner.TextTarget);
        Assert.Equal(0,budget.Usage.ActiveSurfaces); Assert.Equal(0,budget.Usage.EstimatedBytes);
    }

}
