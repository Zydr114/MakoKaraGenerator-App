using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

internal static class PackageRuntimeFixture
{
    internal static ProjectSnapshot Create(string kind)
    {
        EffectPackageContents package = kind switch
        {
            "graph" => MixedEffectPackageFixture.Contents(),
            "material" => MixedEffectPackageFixture.Contents(pure: true),
            "sprite" => SpritePackage(),
            _ => ScalePackage(),
        };
        var line = LyricLine.Create("row", "歌詞", new(0, 700_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌詞", new(0, 700_000), TimingStatus.Edited)]);
        var project = Project.Create("固定包Runtime", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 700_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with
            {
                FontFamilyName = SkiaTextLayoutEngine.DefaultFamilyName, FontSizePx = 40, Stroke = null, Shadow = null,
                UnsungFill = new() { Color = new(230, 90, 50, 210) }, SungFill = new() { Color = new(50, 170, 240, 190) },
            });
        if (kind == "material")
        {
            var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":0.3}"));
            project = project with
            {
                Effects = new([package], []),
                LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } },
            };
        }
        else
        {
            project = project with { Effects = new([package], [new()
            {
                Id = "actual-" + kind, Name = "真实" + kind, PackageId = package.PackageId, Revision = package.Revision,
                ExportId = kind == "graph" ? "controller" : "scale", Seed = ulong.MaxValue,
                Binding = kind == "scale" ? new(ProjectEffectBindingKind.Tokens, ids: ["token"]) : new(ProjectEffectBindingKind.Project),
                Subscription = new("project"),
                Parameters = EffectParameterOverrides.Parse(kind == "graph" ? "{\"amount\":0.3}" : "{\"peak\":1.8}"),
            }]) };
        }
        return ProjectSnapshot.FromProject(project);
    }

    internal static ProjectSnapshot WithoutContribution(ProjectSnapshot snapshot, string kind)
    {
        ProjectSnapshot changed = kind == "material" ? snapshot with
        {
            Effects = ProjectEffects.Empty,
            LyricStyle = snapshot.LyricStyle with
            {
                UnsungFill = new() { Color = new(230, 90, 50, 210) }, SungFill = new() { Color = new(50, 170, 240, 190) },
            },
        } : snapshot with { Effects = new(snapshot.Effects.Packages, []) };
        return PackageSnapshotContractFixture.Fingerprint(changed);
    }

    private static EffectPackageContents ScalePackage() => EffectPackageFixture.Contents();

    private static EffectPackageContents SpritePackage()
    {
        const string path = "images/actual.png";
        using var bitmap = new SKBitmap(3, 2); bitmap.Erase(new SKColor(220, 60, 180, 210)); bitmap.SetPixel(1, 0, new(10, 240, 80, 150));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var files = EffectPackageFixture.Files(); files[path] = data.ToArray();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("""
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record SpriteData : PreparedEffect;
            public sealed class PackEffect : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source) => new SpriteData
                {
                    VisualOutputs = [new(new("actual-image"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText)],
                };
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                    => output.DrawSprites(new("actual-image"),
                        [new(frame.Resources.GetImage("images/actual.png"),new(45,0,0,30,15+frame.TimeUs/50000.0,15),new(1,1,1,.8))],OverlayLayer.AboveText);
            }
            """);
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["requires"] = new JsonArray("visuals.sprites");
        foreach (var file in manifest["files"]!.AsArray())
            if (file!["path"]!.GetValue<string>() == path) file["role"] = "image";
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project");
        manifest["exports"]![0]!["outputs"]![0]!["kind"] = "Overlay";
        manifest["exports"]![0]!["outputs"]![0]!["target"] = "canvas";
        manifest["exports"]![0]!["outputs"]![0]!["id"] = "actual-image";
        manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray("visuals.sprites");
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }

    internal static void AssertFixedFont()
    {
        string family = SkiaTextLayoutEngine.DefaultFamilyName;
        using SKTypeface typeface = SKFontManager.Default.MatchFamily(family, SKFontStyle.Normal);
        Assert.NotNull(typeface); Assert.Equal(family, typeface.FamilyName);
        using SKStreamAsset stream = typeface.OpenStream(); Assert.NotNull(stream); Assert.True(stream.Length > 0);
        if (OperatingSystem.IsLinux())
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] bytes = new byte[64 * 1024]; int read;
            while ((read = stream.Read(bytes, bytes.Length)) != 0) hash.AppendData(bytes, 0, read);
            Assert.Equal("b76b0433203017ca80401b2ee0dd69350349871c4b19d504c34dbdd80541690a", Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        else
        {
            Assert.True(typeface.GetGlyph('歌') != 0);
            Assert.True(typeface.GetGlyph('詞') != 0);
        }
    }
}
