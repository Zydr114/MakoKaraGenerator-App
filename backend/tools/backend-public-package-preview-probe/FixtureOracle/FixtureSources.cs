using MakoKaraGenerator.TestSupport;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using SkiaSharp;

namespace PublicPackagePreviewProbe;

internal static class FixtureSources
{
    internal const string Family = "Noto Sans CJK SC";
    internal const string WarningCode = "PackageSnapshotActualWarning";
    internal const string WarningMessage = "真实作者警告：固定包源🎵";

    internal static ProjectSnapshot Create(string kind)
    {
        EffectPackageContents package = kind switch
        {
            "graph" => MixedEffectPackageFixture.Contents(controllerSource: WithWarning(MixedEffectPackageFixture.Source)),
            "material" => MixedEffectPackageFixture.Contents(pure: true),
            "sprite" => SpritePackage(),
            _ => ScalePackage(),
        };
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

    internal static ProjectSnapshot Runaway(string stage, string marker, string nonce, bool ffmpeg)
    {
        var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(RunawaySource);
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["parameters"] = new JsonArray(
            TextParameter("stage", stage), TextParameter("marker", marker), TextParameter("nonce", nonce),
            TextParameter("child", ffmpeg ? "ffmpeg" : "sleep"));
        manifest["exports"]![0]!["parameters"] = new JsonArray("stage", "marker", "nonce", "child");
        EffectPackageContents package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
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
        EffectPackageContents package = MixedEffectPackageFixture.Contents(controllerSource: """
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    // Trigger on the first validation sample so the bounded
                    // HTTP probe can exercise the runtime Error path with its
                    // legal one-frame fixture plan.
                    if (frame.TimeUs >= 0) frame.Diagnostics.Report(DiagnosticSeverity.Error, "ActualPackageRuntimeError", "真实固定包作者错误🎵",
                        new SourceLocation("src/实际作者🎵.cs", 7, 9));
                }
            }
            """);
        ProjectSnapshot basis = Create("scale");
        return Fingerprint(basis with { Effects = new([package], [new()
        {
            Id = "actual-error", Name = "真实错误作者", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project),
        }]) });
    }

    private const string RunawaySource = """
        using System;
        using System.Diagnostics;
        using System.IO;
        using System.Threading;
        using MakoKaraGenerator.Scripting;
        public sealed class PackEffect : EffectBase
        {
            public override void Initialize(EffectInitializationContext context)
            {
                File.WriteAllText(context.Parameters.GetText("marker")+".ready",Environment.ProcessId.ToString());
                if(context.Parameters.GetText("stage")=="Initialize") Hang(context.Parameters);
            }
            public override void Evaluate(FrameContext frame,FrameWriter output)
            {
                if(frame.Parameters.GetText("stage")=="Evaluate") Hang(frame.Parameters);
            }
            private static void Hang(ParameterValues parameters)
            {
                bool ffmpeg=parameters.GetText("child")=="ffmpeg";
                var start=new ProcessStartInfo(ffmpeg ? "/usr/bin/ffmpeg" : "/bin/sleep")
                { UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
                if(ffmpeg)
                {
                    foreach(var argument in new[]{"-hide_banner","-loglevel","error","-re","-f","lavfi","-i","anullsrc=r=8000:cl=mono","-f","null","-"})
                        start.ArgumentList.Add(argument);
                }
                else start.ArgumentList.Add("300");
                var child=Process.Start(start) ?? throw new Exception("ActualOwnedChildFailed");
                File.WriteAllText(parameters.GetText("marker"),Environment.ProcessId+"|"+child.Id+"|"+parameters.GetText("stage")+"|"+parameters.GetText("nonce"));
                while(true) Thread.SpinWait(10000);
            }
        }
        """;

    private static string WithWarning(string source) => source.Replace("public override void Evaluate(FrameContext frame, FrameWriter output)\n    {",
        "public override void Evaluate(FrameContext frame, FrameWriter output)\n    {\n        frame.Diagnostics.Report(DiagnosticSeverity.Warning, \"" + WarningCode + "\", \"" + WarningMessage + "\");", StringComparison.Ordinal);

    private static EffectPackageContents ScalePackage()
    {
        var files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(WithWarning(EffectPackageFixture.EffectSource));
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }

    private static EffectPackageContents SpritePackage()
    {
        const string path = "images/actual.png";
        using var bitmap = new SKBitmap(3, 2); bitmap.Erase(new SKColor(220, 60, 180, 210)); bitmap.SetPixel(1, 0, new(10, 240, 80, 150));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var files = EffectPackageFixture.Files(); files[path] = data.ToArray();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("""
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase
            {
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                {
                    frame.Diagnostics.Report(DiagnosticSeverity.Warning, "PackageSnapshotActualWarning", "真实作者警告：固定包源🎵");
                    output.Overlay.DrawSprites(new("actual-image"),
                        [new(frame.Resources.GetImage("images/actual.png"),new(45,0,0,30,15+frame.TimeUs/50000.0,15),new(1,1,1,.8))],OverlayLayer.AboveText);
                }
            }
            """);
        var manifest = EffectPackageFixture.Manifest(files);
        foreach (var file in manifest["files"]!.AsArray())
            if (file!["path"]!.GetValue<string>() == path) file["role"] = "image";
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project");
        manifest["exports"]![0]!["outputs"]![0]!["kind"] = "Overlay";
        manifest["exports"]![0]!["outputs"]![0]!["id"] = "actual-image";
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }

}
