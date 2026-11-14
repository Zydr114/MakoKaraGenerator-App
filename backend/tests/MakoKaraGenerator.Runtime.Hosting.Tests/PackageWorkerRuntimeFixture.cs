using System.Security.Cryptography;
using MakoKaraGenerator.Scripting.Compilation;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using SkiaSharp;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

internal static class PackageWorkerRuntimeFixture
{
    internal static async Task<ScriptPreparedInputLease> PrepareScriptsAsync(ProjectSnapshot snapshot, byte[] source, string root)
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        List<(ScriptCompilationInput, ScriptCompilationResult)> compilations = [];
        foreach (var contents in snapshot.Effects.Packages)
        {
            var definition = EffectPackageDefinition.Read(contents);
            var input = ScriptCompilationInput.Create(EffectPackageSources.Read(contents), definition.Controllers.Select(controller => controller.EntryType), compiler.Identity);
            compilations.Add((input, await compiler.CompileAsync(input.Sources, input.EntryTypes)));
        }
        return ScriptPreparedInputCodec.Prepare(source, root, compiler.Identity, compilations);
    }

    internal const string Family = "Noto Sans CJK SC";

    internal static ProjectSnapshot Create(string kind)
    {
        EffectPackageContents package = kind switch
        {
            "graph" => MixedEffectPackageFixture.Contents(),
            "material" => MixedEffectPackageFixture.Contents(pure: true),
            "sprite" => SpritePackage(),
            _ => EffectPackageFixture.Contents(),
        };
        return CreateWithPackage(kind, package);
    }

    internal static ProjectSnapshot CreateScale(string scaleSource) =>
        CreateWithPackage("scale", V2ScalePackage(scaleSource));

    private static ProjectSnapshot CreateWithPackage(string kind, EffectPackageContents package)
    {
        var line = LyricLine.Create("row", "歌詞", new(0, 700_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌詞", new(0, 700_000), TimingStatus.Edited)]);
        var project = Project.Create("固定包Runtime", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 700_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with
            {
                FontFamilyName = Family, FontSizePx = 40, Stroke = null, Shadow = null,
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
        return Fingerprint(changed);
    }

    internal static ProjectSnapshot Fingerprint(ProjectSnapshot snapshot) => snapshot with
    {
        EffectInputsFingerprint = ProjectEffectInputs.Fingerprint(new()
        {
            Name = snapshot.Name, SchemaVersion = snapshot.SchemaVersion, Composition = snapshot.Composition,
            Lyrics = snapshot.Lyrics, LyricPresentation = snapshot.LyricPresentation, LyricStyle = snapshot.LyricStyle,
            LyricStylePresetId = snapshot.LyricStylePresetId, EnterAnimation = snapshot.EnterAnimation,
            Background = snapshot.Background, AudioPath = snapshot.AudioPath, AudioMedia = snapshot.AudioMedia,
            AudioAnalysis = snapshot.AudioAnalysis, Effects = snapshot.Effects, PresetApplications = snapshot.PresetApplications,
        }),
    };

    internal static string WorkerApphost()
    {
        string? overridePath = Environment.GetEnvironmentVariable("MAKO_B3F_TEST_WORKER_PATH");
        string path = overridePath is not null
            ? RequireOverride(overridePath)
            : Path.Combine(RepositoryRoot(), "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker");
        Assert.True(File.Exists(path)); return path;

        static string RequireOverride(string value)
        {
            Assert.True(Path.IsPathFullyQualified(value), "MAKO_B3F_TEST_WORKER_PATH must be an absolute path.");
            Assert.True(File.Exists(value), "MAKO_B3F_TEST_WORKER_PATH does not exist.");
            return value;
        }
    }

    internal static string RepositoryRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MakoKaraGenerator.slnx"))) root = root.Parent;
        return root?.FullName ?? throw new IOException("Repository root not found.");
    }

    internal static ProjectSnapshot Runaway(string stage)
    {
        EffectPackageContents package = V2ScalePackage(RunawaySource, manifest =>
        {
            manifest["parameters"] = new JsonArray(TextParameter("stage", stage));
            manifest["exports"]![0]!["parameters"] = new JsonArray("stage");
        });
        ProjectSnapshot basis = Create("scale");
        return Fingerprint(basis with { Effects = new([package], [new()
        {
            Id = "actual-runaway", Name = "真实失控作者", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = "scale", Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token"]),
        }]) });

        static JsonObject TextParameter(string id, string value) => new() { ["id"] = id, ["type"] = "text", ["label"] = id, ["default"] = value };
    }

    internal static ProjectSnapshot DiagnosticSource(string kind)
    {
        bool sourceMaterial = kind is "material" or "compile-source";
        ProjectSnapshot snapshot = Create(sourceMaterial ? "material" : "graph");
        EffectPackageContents package = kind.StartsWith("compile", StringComparison.Ordinal)
            ? MixedEffectPackageFixture.Contents(controllerSource: "public sealed class PackEffect { invalid !!! }")
            : kind == "script-material" ? MixedEffectPackageFixture.MaterialContents(background: true, backgroundColor: true)
            : MixedEffectPackageFixture.Contents(pure: sourceMaterial, background: true, backgroundColor: true);
        var instances = snapshot.Effects.Instances.Select(instance => instance with { PackageId = package.PackageId, Revision = package.Revision });
        var style = snapshot.LyricStyle;
        if (sourceMaterial)
        {
            var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":0.3}"));
            style = style with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } };
        }
        return Fingerprint(snapshot with { Effects = new([package], instances), LyricStyle = style });
    }

    internal static ProjectSnapshot RuntimeError()
    {
        EffectPackageContents package = V2ScalePackage("""
            using System;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                    => throw new InvalidOperationException("真实固定包作者错误🎵");
            }
            """, manifest =>
        {
            manifest["exports"]![0]!["id"] = "controller";
            manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project");
        });
        ProjectSnapshot basis = Create("scale");
        return Fingerprint(basis with { Effects = new([package], [new()
        {
            Id = "actual-error", Name = "真实错误作者", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project),
        }]) });
    }

    private const string RunawaySource = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source)
            {
                if (source.Parameters.GetText("stage") == "Declare")
                    while (source.CanvasSize.X >= 0) { }
                return PreparedEffect.Empty;
            }
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                if (frame.Parameters.GetText("stage") == "Evaluate")
                    while (frame.TimeUs >= 0) { }
            }
        }
        """;

    private static EffectPackageContents V2ScalePackage(string source, Action<JsonObject>? configureManifest = null)
    {
        var files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source);
        var manifest = EffectPackageFixture.Manifest(files);
        configureManifest?.Invoke(manifest);
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }

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
        using SKTypeface typeface = SKFontManager.Default.MatchFamily(Family, SKFontStyle.Normal);
        Assert.NotNull(typeface); Assert.Equal(Family, typeface.FamilyName);
        using SKStreamAsset stream = typeface.OpenStream(); Assert.NotNull(stream); Assert.True(stream.Length > 0);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] bytes = new byte[64 * 1024]; int read;
        while ((read = stream.Read(bytes, bytes.Length)) != 0) hash.AppendData(bytes, 0, read);
        Assert.Equal("b76b0433203017ca80401b2ee0dd69350349871c4b19d504c34dbdd80541690a", Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
