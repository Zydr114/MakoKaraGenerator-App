using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task B9_Family与Base材质复用同resolver且Source不排除脚本图元(bool basic, bool source)
    {
        var contents = OwnedMaterialPackage(); var definition = EffectPackageDefinition.Read(contents);
        using var native = CompiledPackageGraphExports.Compile(definition);
        var handle = native.Resources.GetMaterial("ink");
        var material = (Fx.MaterialValue.PackageMaterial)Fx.MaterialValue.PackageExport(handle,
            new Fx.ParameterValues([new("amount", new Fx.ParameterValue.Number(.5))]));
        var style = LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 64, Opacity = 1,
            UnsungColor = RgbaColor.White, SungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null };
        if (source)
        {
            var spec = new MaterialSpec.PackageMaterial(contents.PackageId, contents.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":0.5}"));
            style = style with { UnsungFill = new() { Material = spec }, SungFill = new() { Material = spec } };
        }
        var project = Project.Create("owned-material", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([LyricLine.Create("line", "AB", new(0, 5_000_000), TimingStatus.Confirmed,
                [LyricToken.Create("token", "AB", new(0, 4_000_000), TimingStatus.Confirmed)]) with
                { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "xy")] }]), lyricStyle: style)
            with { Effects = new([contents], []), EnterAnimation = null };
        var snapshot = ProjectSnapshot.FromProject(project); PreparedRenderFrame frame;
        if (source)
        {
            var compiled = await SourceMaterialRuntimeTests.Compile(project); Assert.True(compiled.Succeeded);
            await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(snapshot);
            frame = await session.PrepareAsync(3_000_000, Fx.OutputMode.Transparent, new() { PixelWidth = 640, PixelHeight = 360 });
        }
        else
        {
            using var measure = new SkiaRenderPipeline(); var scene = measure.GetSceneFrame(snapshot, 3_000_000);
            frame = new(snapshot, 3_000_000, Fx.OutputMode.Transparent, new() { PixelWidth = 640, PixelHeight = 360 }, scene, []);
        }
        var token = Assert.Single(frame.FinalGeometry); var family = new Fx.ImageEffectTarget.Token(token.WithRuby);
        var writer = new PreparedMaterialOwner.Script(new("read"), "material-writer");
        var update = new Fx.StyleUpdate(unsung: material, sung: material);
        if (!source && !basic)
            frame = new(snapshot, frame.TimeUs, frame.OutputMode, frame.Target, frame.Scene, [], [new(token.WithRuby, update)], [], [], [], [], [],
                [new(writer, family, PreparedMaterialSlot.UnsungFill, material), new(writer, family, PreparedMaterialSlot.SungFill, material)]);
        else if (!source)
            frame = frame.WithTextPartitions([], [new(token.BaseOnly, update)], [],
                [new(writer, token.BaseOnly, PreparedMaterialSlot.UnsungFill, material), new(writer, token.BaseOnly, PreparedMaterialSlot.SungFill, material)]);
        else if (basic)
            frame = frame.WithTextPartitions([], [new(token.BaseOnly, new(opacityFactor: 1))], []);
        Assert.NotEmpty(basic ? frame.BaseMaterials : frame.Materials.Select(value => new PreparedBaseMaterialBinding(value.Owner, token.BaseOnly, value.Slot, value.Material)).ToImmutableArray());
        Assert.Equal(source, (basic ? frame.BaseMaterials : frame.Materials.Select(value =>
            new PreparedBaseMaterialBinding(value.Owner, token.BaseOnly, value.Slot, value.Material)).ToImmutableArray()).All(value => value.Owner is PreparedMaterialOwner.Source));

        using var probe = OwnedProbe("trigger", 480);
        using var resources = new SceneImageEffectResources([new(OwnedCatalog.GetGraph("a"), probe, GroupWhole, Bounds)], null,
            [native.CreateMaterialProgram(handle, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        // Parent's owned Below triggers prefix scheduling even for source material.
        // Both materials sample a declared 160px offset into real glyph pixels far from overlays.
        Fx.DotBatch Ink(string owner, Fx.ColorRgba color) => new(new(owner), new(owner), Fx.OverlayLayer.BehindText,
            [new(new(160, 240), 110, color)]);
        frame = frame.WithOverlayOutput([Ink("other", new(0, 1, 0, .5)), Ink("read", new(1, 0, 0, 1))], [])
            .WithImageEffects([OwnedBind("read", new Fx.ImageEffectTarget.Group(new(new(project.LyricPresentation.FindProgram("primary")!.RootContainerId))), "a")]);
        using var actual = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, Target = frame.Target, TransparentBackground = true });
        using var geometry = OwnedInkRegion(basic ? token.BaseGeometry : token.FamilyGeometry);
        byte[] pixels = actual.Pixels.ToArray(); int checkedPixels = 0;
        for (int y = 40; y < 340; y++) for (int x = 300; x < 460; x++)
        {
            if (!geometry.Contains(x + .5f, y + .5f)) continue;
            int offset = (y * 640 + x) * 4;
            if (pixels[offset + 3] < 100) continue;
            // Source has no script owner and reads red; script read filters its
            // red and samples the other green. Strong channel assertion avoids
            // relying on a graph identity hidden under the real overlay.
            if (source) { Assert.True(pixels[offset + 2] > 80); Assert.InRange(pixels[offset + 1], 0, 2); }
            else { Assert.True(pixels[offset + 1] > 40); Assert.InRange(pixels[offset + 2], 0, 2); }
            checkedPixels++;
        }
        Assert.True(checkedPixels > 50); Assert.Empty(actual.MaterialDiagnostics);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    private static EffectPackageContents OwnedMaterialPackage()
    {
        var original = MixedEffectPackageFixture.Contents(pure: true,
            manifestChange: manifest => manifest["exports"]![0]!["coverageMode"] = "unmasked",
            graphChange: graph =>
            {
                var below = EffectPackageGraphFixture.Input("below", "BackdropBelow"); below.Remove("capture");
                graph["inputs"]!.AsArray().Add(below);
                graph["nodes"]![0]!["children"]!["below"] = "input:below";
                graph["nodes"]![0]!["bounds"]!["maxSampleOffsetPx"] = 160;
            },
            programChange: program => program["children"]!.AsArray().Add(new JsonObject { ["name"] = "below", ["type"] = "color" }),
            shaderSource: "uniform float amount; uniform shader original; uniform shader below; half4 main(float2 p) { return below.eval(p-float2(160,0)); }");
        var manifest = JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion; manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("style.fill", "image.graph");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), original.Files.Select(file =>
            KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    }
}
