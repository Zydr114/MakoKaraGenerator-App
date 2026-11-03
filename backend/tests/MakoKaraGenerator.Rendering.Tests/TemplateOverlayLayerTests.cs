using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class TemplateOverlayLayerTests
{
    private static readonly string[] OutputIds = ["burst", "emitter", "dot"];
    private static EffectPackageContents Package()
    {
        var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("""
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase
            {
                public override void OnEvent(TimelineEvent evt, EventContext context)
                {
                    if (evt is TokenStarted token) context.Bursts.Queue(new("burst"), new()
                    { Count=1, Anchor=new BurstAnchor.SourceToken(token.TokenId), LifetimeUs=1000000,
                        SizePx=12, Color=new(1,0,0,1), SeedStreamKey="burst", Layer=OverlayLayer.AboveText });
                }
                public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
                    => output.Set(new("emitter"), new() { AnchorMode=CursorAnchorMode.AdvanceCenter,
                        RatePerSecond=10, LifetimeUs=1000000, SizePx=12, Color=new(1,0,0,1), SeedStreamKey="continuous", Layer=OverlayLayer.AboveText });
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                {
                    var cursor=frame.Playback.GetCursors(CursorAnchorMode.AdvanceCenter).Single();
                    output.Overlay.DrawDots(new("dot"), [new(cursor.Position, 32, new(1,0,0,1))], OverlayLayer.AboveText);
                }
            }
            """);
        var manifest=EffectPackageFixture.Manifest(files); manifest["exports"]![0]!["outputs"]=new JsonArray([..OutputIds.Select(id=>(JsonNode)new JsonObject
            {["id"]=id,["kind"]="Overlay",["target"]="tokenFamily",["requires"]=new JsonArray(),["dependsOn"]=new JsonArray()})]);
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }
    private static Project Apply(EffectOverlayLayer layer)
    {
        var project=Project.Create("layer", Composition.Create(320,180,FrameRate.Fps30), LyricsDocument.Create([
            LyricLine.Create("row","田",new(0,1000000),TimingStatus.Edited,[LyricToken.Create("a","田",new(0,1000000),TimingStatus.Edited)])]),
            lyricStyle:LyricStyle.Default with {FontSizePx=48, UnsungColor=RgbaColor.White,SungColor=RgbaColor.White,Stroke=null,Shadow=null});
        var package=Package(); var template=StyleTemplate.Create("t","层",StyleTemplateKind.Token,project.LyricStyle,[],
            [new("r","图元",TemplateEffectTarget.TokenFamily,package.PackageId,package.Revision,"scale",overlayLayer:layer)],[package]);
        Assert.True(StyleTemplateApplicator.TryPreview(project,template,"row","a",false,out var result,out var reason),reason);return result.Result;
    }
    [Theory]
    [InlineData(EffectOverlayLayer.Package,Fx.OverlayLayer.AboveText)]
    [InlineData(EffectOverlayLayer.BehindText,Fx.OverlayLayer.BehindText)]
    [InlineData(EffectOverlayLayer.AboveText,Fx.OverlayLayer.AboveText)]
    public async Task 模板层覆盖真实圆点连续粒子Burst与作者查询且重放一致(EffectOverlayLayer layer,Fx.OverlayLayer expected)
    {
        var project=Apply(layer); var compiled=await CompiledProjectEffects.CompileAsync(project,new ScriptCompiler(Path.Combine(AppContext.BaseDirectory,"script-refs","net10.0")));
        Assert.True(compiled.Succeeded);var snapshot=ProjectSnapshot.FromProject(project);var target=RenderTarget.FromComposition(snapshot.Composition);
        await using var source=await compiled.CreateFactory(static()=>new SkiaRenderPipeline(),Fx.ReplayReason.Initial).CreateAsync(snapshot);
        var frame=await source.PrepareAsync(500000,Fx.OutputMode.Transparent,target);
        Assert.Equal(3,frame.DotBatches.Length);Assert.All(frame.DotBatches,b=>Assert.Equal(expected,b.Layer));Assert.Equal(6,frame.Particles.Length);Assert.All(frame.Particles,p=>Assert.Equal(expected,p.Layer));
        Assert.Single(frame.Particles.Where(p=>p.EventId is not null));Assert.Equal(5,frame.Particles.Count(p=>p.EventId is null));
        var pixels=Pixels(frame);await source.PrepareAsync(100000,Fx.OutputMode.Transparent,target);var replay=await source.PrepareAsync(500000,Fx.OutputMode.Transparent,target);
        Assert.Equal(frame.Particles.Select(p=>(p.Id,p.Layer,p.Position)),replay.Particles.Select(p=>(p.Id,p.Layer,p.Position)));Assert.Equal(pixels,Pixels(replay));
        if(layer==EffectOverlayLayer.Package)return;
        var other=Apply(layer==EffectOverlayLayer.BehindText?EffectOverlayLayer.AboveText:EffectOverlayLayer.BehindText);
        var otherCompiled=await CompiledProjectEffects.CompileAsync(other,new ScriptCompiler(Path.Combine(AppContext.BaseDirectory,"script-refs","net10.0")));
        await using var otherSource=await otherCompiled.CreateFactory(static()=>new SkiaRenderPipeline(),Fx.ReplayReason.Initial).CreateAsync(ProjectSnapshot.FromProject(other));
        Assert.NotEqual(pixels,Pixels(await otherSource.PrepareAsync(500000,Fx.OutputMode.Transparent,target)));
    }
    private static byte[] Pixels(PreparedRenderFrame frame)
    {
        using SkiaRenderPipeline renderer=new();using var result=renderer.Render(RenderRequest.At(frame.Snapshot,frame.TimeUs) with
            {PreparedFrame=frame,Target=frame.Target,TransparentBackground=true});return result.PixelMemory.ToArray();
    }
}
