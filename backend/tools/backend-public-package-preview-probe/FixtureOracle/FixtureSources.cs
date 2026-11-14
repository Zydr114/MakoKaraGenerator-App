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
            "graph" => GraphOrMaterialPackage(isMaterial: false),
            "material" => GraphOrMaterialPackage(isMaterial: true),
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
                Parameters = kind == "graph" ? EffectParameterOverrides.Parse("{\"amount\":0.3}")
                    : kind == "sprite" ? EffectParameterOverrides.Empty
                    : EffectParameterOverrides.Parse("{\"peak\":1.8}"),
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
        // Pure SDK v2 runaway effect: infinite loop inside Declare (Initialize/Declare) or Evaluate.
        const string runawaySource = """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    if (source.Parameters.GetText("stage") is "Initialize" or "Declare")
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
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(runawaySource),
        };
        var parameters = new JsonArray(new JsonObject
        {
            ["id"] = "stage",
            ["type"] = "text",
            ["label"] = "stage",
            ["default"] = stage,
        });
        var exports = new JsonArray(new JsonObject
        {
            ["id"] = "scale",
            ["kind"] = "controller",
            ["name"] = "Runaway",
            ["entryType"] = "PackEffect",
            ["parameters"] = new JsonArray("stage"),
            ["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node"),
            ["outputs"] = new JsonArray(new JsonObject
            {
                ["id"] = "scale",
                ["kind"] = "VisualTransform",
                ["target"] = "tokenFamily",
                ["requires"] = new JsonArray(),
                ["dependsOn"] = new JsonArray(),
            }),
        });
        EffectPackageContents package = CreateV2Package("probe.v2.runaway", files, parameters, exports, ["token.affine"]);
        ProjectSnapshot basis = Create("scale");
        return Fingerprint(basis with { Effects = new([package], [new()
        {
            Id = "actual-runaway", Name = "真实可控v2作者", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = "scale", Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token"]),
        }]) });
    }

    internal static ProjectSnapshot CustomWarningsPackage(int count, Dictionary<string, byte[]>? extraFiles = null)
    {
        EffectPackageContents package = ScalePackage(extraFiles: extraFiles);
        ProjectSnapshot basis = Create("scale");
        return Fingerprint(basis with { Effects = new([package], [basis.Effects.Instances[0] with
        {
            PackageId = package.PackageId, Revision = package.Revision,
        }]) });
    }

    internal static ProjectSnapshot DiagnosticSource(string kind)
    {
        bool sourceMaterial = kind is "material" or "compile-source";
        ProjectSnapshot snapshot = Create(sourceMaterial ? "material" : "graph");
        EffectPackageContents package = kind.StartsWith("compile", StringComparison.Ordinal)
            ? GraphOrMaterialPackage(isMaterial: sourceMaterial, controllerOverride: "public sealed class PackEffect { invalid !!! }")
            : GraphOrMaterialPackage(isMaterial: sourceMaterial, background: true);
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
        const string errorSource = """
            using System;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    // Deterministically fail on the first frame without SDK v1 diagnostic reporting APIs.
                    if (frame.TimeUs >= 0) throw new InvalidOperationException("ActualPackageRuntimeError: 真实固定包作者错误🎵");
                }
            }
            """;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(errorSource),
        };
        var exports = new JsonArray(new JsonObject
        {
            ["id"] = "controller",
            ["kind"] = "controller",
            ["name"] = "Error Controller",
            ["entryType"] = "PackEffect",
            ["parameters"] = new JsonArray(),
            ["bindingKinds"] = new JsonArray("project", "tokens"),
            ["outputs"] = new JsonArray(new JsonObject
            {
                ["id"] = "declared",
                ["kind"] = "Overlay",
                ["target"] = "canvas",
                ["requires"] = new JsonArray(),
                ["dependsOn"] = new JsonArray(),
            }),
        });
        EffectPackageContents package = CreateV2Package("probe.v2.runtime-error", files, new JsonArray(), exports, []);
        ProjectSnapshot basis = Create("scale");
        return Fingerprint(basis with { Effects = new([package], [new()
        {
            Id = "actual-error", Name = "真实错误作者", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project),
        }]) });
    }

    private static EffectPackageContents ScalePackage(string packageId = "probe.v2.scale", Dictionary<string, byte[]>? extraFiles = null)
    {
        const string source = """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    double peak = frame.Parameters.GetNumber("peak");
                    foreach (var token in frame.Presentations.TokensInBinding)
                        foreach (var geometry in frame.Geometry)
                            if (geometry.WithRuby == token.WithRuby)
                                output.AppendTransform(token.WithRuby, Affine2.ScaleAt(peak, geometry.BaseGeometry.AdvanceBounds.Center));
                }
            }
            """;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source),
        };
        if (extraFiles is not null)
            foreach (var pair in extraFiles) files[pair.Key] = pair.Value;
        var parameters = new JsonArray(new JsonObject
        {
            ["id"] = "peak",
            ["type"] = "number",
            ["label"] = "Peak",
            ["default"] = 1.5,
            ["min"] = 1.0,
            ["max"] = 4.0,
        });
        var exports = new JsonArray(new JsonObject
        {
            ["id"] = "scale",
            ["kind"] = "controller",
            ["name"] = "Scale",
            ["entryType"] = "PackEffect",
            ["parameters"] = new JsonArray("peak"),
            ["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node"),
            ["outputs"] = new JsonArray(new JsonObject
            {
                ["id"] = "scale",
                ["kind"] = "VisualTransform",
                ["target"] = "tokenFamily",
                ["requires"] = new JsonArray(),
                ["dependsOn"] = new JsonArray(),
            }),
        });
        return CreateV2Package(packageId, files, parameters, exports, ["token.affine"]);
    }

    private static EffectPackageContents GraphOrMaterialPackage(bool isMaterial, string? controllerOverride = null, bool background = false)
    {
        const string controllerSource = """
            using System.Collections.Generic;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var binding = new GraphBindingValue(frame.Resources.GetGraph("tint"),
                        new ParameterValues(new[] { KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(0.3)) }),
                        GraphUniformValues.Empty, GraphTransformValues.Empty);
                    foreach (var line in frame.Presentations.LinesInBinding)
                        output.BindGraph(line.Target, binding);
                }
            }
            """;
        string sksl = background
            ? "uniform float amount; uniform shader original; uniform shader background; half4 main(float2 p) { half4 c = original.eval(p); half4 b = background.eval(p); return half4(mix(c.rgb, b.rgb, amount), c.a); }"
            : isMaterial
                ? "uniform float amount; half4 main(float2 p) { return half4(0.2 * amount, 0.5, 0.8, 1.0); }"
                : "uniform float amount; uniform shader original; half4 main(float2 p) { half4 c = original.eval(p); return half4(c.rgb * (1.0 - 0.5 * amount), c.a); }";
        var childrenProgram = new JsonArray();
        if (!isMaterial || background) childrenProgram.Add(new JsonObject { ["name"] = "original", ["type"] = "color" });
        if (background) childrenProgram.Add(new JsonObject { ["name"] = "background", ["type"] = "color" });
        var program = new JsonObject
        {
            ["programVersion"] = 1,
            ["source"] = "shaders/probe.sksl",
            ["profile"] = ShaderProgramDefinition.RasterProfile,
            ["uniforms"] = new JsonArray(new JsonObject { ["name"] = "amount", ["type"] = "float" }),
            ["children"] = childrenProgram,
        };
        var graphInputs = new JsonArray();
        var nodeChildren = new JsonObject();
        if (!isMaterial || background)
        {
            graphInputs.Add(new JsonObject
            {
                ["id"] = "original",
                ["source"] = "TargetColor",
                ["type"] = "color",
                ["availability"] = "required",
                ["backgroundRequirement"] = "none",
                ["space"] = "canvas",
                ["capture"] = new JsonObject
                {
                    ["includeRuby"] = true, ["includeStroke"] = true, ["includeShadow"] = true,
                    ["includeDescendantGroups"] = true, ["includeOwnedOverlay"] = false, ["region"] = "fillAndStroke",
                },
            });
            nodeChildren["original"] = "input:original";
        }
        if (background)
        {
            graphInputs.Add(new JsonObject
            {
                ["id"] = "background",
                ["source"] = "BackgroundMedia",
                ["type"] = "color",
                ["availability"] = "required",
                ["backgroundRequirement"] = "media",
                ["space"] = "canvas",
            });
            nodeChildren["background"] = "input:background";
        }
        var graph = new JsonObject
        {
            ["graphVersion"] = 1,
            ["id"] = "probe",
            ["inputs"] = graphInputs,
            ["parameters"] = new JsonArray("amount"),
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["id"] = "probe",
                ["op"] = "RuntimeShader",
                ["program"] = "shaders/probe.program.json",
                ["children"] = nodeChildren,
                ["uniforms"] = new JsonObject { ["amount"] = "parameter:amount" },
                ["outputType"] = "color",
                ["bounds"] = new JsonObject { ["expandPx"] = 0, ["maxExpandPx"] = 0, ["maxSampleOffsetPx"] = 0 },
                ["edge"] = "transparent",
            }),
            ["output"] = "node:probe",
            ["anchorMode"] = "ColorOnly",
            ["bounds"] = new JsonObject { ["space"] = "canvas", ["source"] = "targetRegion", ["maxExpandPx"] = 0 },
            ["composite"] = new JsonObject { ["mode"] = "sourceOver" },
        };
        if (!isMaterial) graph["bypassInput"] = "input:original";

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["graphs/probe.graph.json"] = Encoding.UTF8.GetBytes(graph.ToJsonString()),
            ["shaders/probe.program.json"] = Encoding.UTF8.GetBytes(program.ToJsonString()),
            ["shaders/probe.sksl"] = Encoding.UTF8.GetBytes(sksl),
        };
        if (!isMaterial || controllerOverride is not null)
            files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(controllerOverride ?? controllerSource);

        var parameters = new JsonArray(new JsonObject
        {
            ["id"] = "amount",
            ["type"] = "number",
            ["label"] = "Amount",
            ["default"] = 0.5,
            ["min"] = 0.0,
            ["max"] = 1.0,
        });
        var exports = new JsonArray();
        if (isMaterial)
        {
            if (controllerOverride is not null)
            {
                exports.Add(new JsonObject
                {
                    ["id"] = "controller",
                    ["kind"] = "controller",
                    ["name"] = "Controller",
                    ["entryType"] = "PackEffect",
                    ["parameters"] = new JsonArray("amount"),
                    ["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node"),
                    ["outputs"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "color", ["kind"] = "ImageGraph", ["target"] = "line",
                        ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray(),
                    }),
                });
            }
            exports.Add(new JsonObject
            {
                ["id"] = "ink",
                ["kind"] = "material",
                ["name"] = "Ink",
                ["graph"] = "graphs/probe.graph.json",
                ["parameters"] = new JsonArray("amount"),
                ["regions"] = new JsonArray("fill", "stroke"),
                ["fallback"] = new JsonObject { ["kind"] = "solid", ["color"] = "#3380CCBF" },
                ["coverageMode"] = "unmasked",
                ["outputs"] = new JsonArray(new JsonObject
                {
                    ["id"] = "color",
                    ["kind"] = "ImageGraph",
                    ["target"] = "region",
                    ["requires"] = new JsonArray(),
                    ["dependsOn"] = new JsonArray(),
                    ["bypass"] = new JsonObject { ["kind"] = "materialFallback", ["mapping"] = "identity" },
                }),
            });
            return CreateV2Package("probe.v2.material", files, parameters, exports, controllerOverride is not null ? ["image.graph"] : []);
        }

        exports.Add(new JsonObject
        {
            ["id"] = "controller",
            ["kind"] = "controller",
            ["name"] = "Controller",
            ["entryType"] = "PackEffect",
            ["parameters"] = new JsonArray("amount"),
            ["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node"),
            ["outputs"] = new JsonArray(new JsonObject
            {
                ["id"] = "color", ["kind"] = "ImageGraph", ["target"] = "line",
                ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray(),
            }),
        });
        exports.Add(new JsonObject
        {
            ["id"] = "tint",
            ["kind"] = "graph",
            ["name"] = "Tint",
            ["graph"] = "graphs/probe.graph.json",
            ["parameters"] = new JsonArray("amount"),
            ["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node"),
            ["outputs"] = new JsonArray(new JsonObject
            {
                ["id"] = "color",
                ["kind"] = "ImageGraph",
                ["target"] = "line",
                ["requires"] = new JsonArray(),
                ["dependsOn"] = new JsonArray(),
                ["bypass"] = new JsonObject { ["kind"] = "graphInput", ["input"] = "input:original", ["mapping"] = "identity" },
            }),
        });
        return CreateV2Package("probe.v2.graph", files, parameters, exports, ["image.graph"]);
    }

    private static EffectPackageContents SpritePackage()
    {
        const string path = "images/actual.png";
        using var bitmap = new SKBitmap(3, 2);
        bitmap.Erase(new SKColor(220, 60, 180, 210));
        bitmap.SetPixel(1, 0, new(10, 240, 80, 150));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        const string spriteSource = """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty with
                {
                    VisualOutputs = [new(new("actual-image"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText)],
                };
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    output.DrawSprites(new("actual-image"),
                        [new(frame.Source.Resources.GetImage("images/actual.png"), new(45, 0, 0, 30, 15 + frame.TimeUs / 50000.0, 15), new(1, 1, 1, .8))],
                        OverlayLayer.AboveText);
                }
            }
            """;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(spriteSource),
            [path] = data.ToArray(),
        };
        var exports = new JsonArray(new JsonObject
        {
            ["id"] = "scale",
            ["kind"] = "controller",
            ["name"] = "Sprite",
            ["entryType"] = "PackEffect",
            ["parameters"] = new JsonArray(),
            ["bindingKinds"] = new JsonArray("project"),
            ["outputs"] = new JsonArray(new JsonObject
            {
                ["id"] = "actual-image",
                ["kind"] = "Overlay",
                ["target"] = "canvas",
                ["requires"] = new JsonArray("visuals.sprites"),
                ["dependsOn"] = new JsonArray(),
            }),
        });
        return CreateV2Package("probe.v2.sprite", files, new JsonArray(), exports, ["visuals.sprites"]);
    }

    private static EffectPackageContents CreateV2Package(string packageId, Dictionary<string, byte[]> files,
        JsonArray parameters, JsonArray exports, string[] requires)
    {
        var fileEntries = new JsonArray();
        foreach (var pair in files.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            string role = pair.Key.EndsWith(".cs", StringComparison.Ordinal) ? "csharp"
                : pair.Key.EndsWith(".graph.json", StringComparison.Ordinal) ? "graph"
                : pair.Key.EndsWith(".program.json", StringComparison.Ordinal) ? "shaderProgram"
                : pair.Key.EndsWith(".sksl", StringComparison.Ordinal) ? "shader"
                : pair.Key.EndsWith(".png", StringComparison.Ordinal) ? "image"
                : "asset";
            fileEntries.Add(new JsonObject
            {
                ["path"] = pair.Key,
                ["role"] = role,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
            });
        }
        var requiresArray = new JsonArray();
        foreach (string item in requires) requiresArray.Add(item);
        var manifest = new JsonObject
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion,
            ["packageId"] = packageId,
            ["name"] = "Fixture " + packageId,
            ["version"] = "2.0.0",
            ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile,
            ["files"] = fileEntries,
            ["parameters"] = parameters,
            ["exports"] = exports,
            ["requires"] = requiresArray,
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 },
            ["emitters"] = new JsonArray(),
        };
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var inputs = files.Select(static pair => new KeyValuePair<string, ReadOnlyMemory<byte>>(pair.Key, pair.Value));
        return EffectPackageContents.Create(manifestBytes, inputs, EffectPackageProfiles.V2);
    }
}
