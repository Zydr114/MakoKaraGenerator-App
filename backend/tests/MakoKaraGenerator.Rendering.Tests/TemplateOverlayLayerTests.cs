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
    private static EffectPackageContents Package()
    {
        var emitters = new JsonArray(
            new JsonObject { ["id"] = "emitter", ["kind"] = "cursor", ["maxLifetimeUs"] = 1_000_000 },
            new JsonObject { ["id"] = "burst", ["kind"] = "burst", ["maxLifetimeUs"] = 1_000_000 });
        return V2RenderPackageHelper.CreateControllerContents("""
            using System.Collections.Immutable;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;

            public sealed record TemplateLayerState : PreparedEffect;

            public sealed class PackEffect : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    var particle = new ParticleSpec
                    {
                        LifetimeUs = 1_000_000, SizePx = 12, Color = new(1, 0, 0, 1),
                        SeedStreamKey = "continuous", Layer = OverlayLayer.AboveText,
                    };
                    var continuous = new CursorEmissionPlan(new("emitter"),
                        new RateTrack([new(new(0, 1_000_000), 10, 10)], [new(0, 1_000_000)]),
                        particle, CursorAnchorMode.AdvanceCenter, BridgePolicy.Jump);
                    var selector = new TimelineSelector(TimelineEventKind.TokenStarted, new TimelineScope.Project());
                    var rows = ImmutableArray.CreateBuilder<BurstEventRow>();
                    for (int index = 0; index < source.Timeline.Count(selector); index++)
                    {
                        if (source.Timeline.Nth(selector, index) is TokenStarted token)
                            rows.Add(new(token.EventId, 1, new BurstAnchor.SourceToken(token.TokenId),
                                particle with { SeedStreamKey = "burst" }));
                    }
                    var burst = new EventBurstPlan(new("burst"), selector, rows.ToImmutable());
                    return new TemplateLayerState
                    {
                        Particles = new([continuous], [burst]),
                        VisualOutputs = [new(new("dot"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)],
                    };
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    var cursor = frame.FinalGeometry.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)[0];
                    output.DrawDots(new("dot"), [new(cursor.Position, 32, new(1, 0, 0, 1))], OverlayLayer.AboveText);
                }
            }
            """,
            [Fx.V2.CapabilityNames.ParticlesContinuous, Fx.V2.CapabilityNames.ParticlesBurst, Fx.V2.CapabilityNames.VisualsDots, Fx.V2.CapabilityNames.TimelineIndex],
            entryType: "PackEffect",
            exportId: "scale",
            outputs:
            [
                new JsonObject
                {
                    ["id"] = "burst", ["kind"] = "Overlay", ["target"] = "canvas",
                    ["requires"] = new JsonArray(Fx.V2.CapabilityNames.ParticlesBurst, Fx.V2.CapabilityNames.VisualsDots),
                    ["dependsOn"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["id"] = "emitter", ["kind"] = "Overlay", ["target"] = "canvas",
                    ["requires"] = new JsonArray(Fx.V2.CapabilityNames.ParticlesContinuous, Fx.V2.CapabilityNames.VisualsDots),
                    ["dependsOn"] = new JsonArray(),
                },
                V2RenderPackageHelper.Output("dot", "Overlay", "canvas", Fx.V2.CapabilityNames.VisualsDots),
            ],
            emitters: emitters,
            packageId: "demo.template.overlay.v2");
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
        Assert.Equal(3, frame.DotBatches.Length); Assert.All(frame.DotBatches, batch => Assert.Equal(expected, batch.Layer));
        Assert.Single(frame.DotBatches, batch => batch.Id.Value == "dot");
        Assert.Equal(6,frame.V2Particles.Length);Assert.All(frame.V2Particles,p=>Assert.Equal(expected,p.Birth.Spec.Layer));
        Assert.Single(frame.V2Particles.Where(p=>p.Birth.EventId is not null));
        Assert.Equal(5,frame.V2Particles.Count(p=>p.Birth.EventId is null));
        var pixels=Pixels(frame);await source.PrepareAsync(100000,Fx.OutputMode.Transparent,target);var replay=await source.PrepareAsync(500000,Fx.OutputMode.Transparent,target);
        Assert.Equal(frame.V2Particles.Select(p=>(p.Birth.Id,p.Birth.Spec.Layer,p.Position)),replay.V2Particles.Select(p=>(p.Birth.Id,p.Birth.Spec.Layer,p.Position)));Assert.Equal(pixels,Pixels(replay));
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
