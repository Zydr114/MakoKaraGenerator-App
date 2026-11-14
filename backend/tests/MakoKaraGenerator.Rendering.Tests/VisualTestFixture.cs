using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

internal static class VisualTestFixture
{
    internal const string Fingerprint = "R2-visual-stage";
    private static readonly Lazy<Task<CompiledEffectPackage>> Compiled = new(() =>
    {
        var parameters = new JsonArray(
            V2RenderPackageHelper.EnumParameter("mode", "normal",
                "normal", "geometry", "ThrowEvaluate", "CancelEvaluate", "ThrowEvaluateVisuals", "CancelEvaluateVisuals",
                "above", "behind", "none", "mark", "large", "tiny", "cursor", "call-cursors"),
            V2RenderPackageHelper.NumberParameter("x", 100, -1e6, 1e6),
            V2RenderPackageHelper.NumberParameter("y", 80, -1e6, 1e6),
            V2RenderPackageHelper.NumberParameter("radius", 20, 0, 1e6),
            V2RenderPackageHelper.ColorParameter("color", 1, 0, 0, .5));
        var contents = V2RenderPackageHelper.CreateControllerContents("""
            using System;
            using System.Collections.Immutable;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;

            public sealed record VisualState : PreparedEffect;

            public sealed class VisualPure : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    string mode = source.Parameters.GetEnum("mode");
                    return new VisualState
                    {
                        VisualOutputs = [new(new(mode == "geometry" ? "cursor" : "clock"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)],
                    };
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    string mode = frame.Parameters.GetEnum("mode");
                    if (mode == "ThrowEvaluate") throw new InvalidOperationException("failed");
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.AppendTransform(new TextTarget.Family(token.WithRuby), Affine2.Translation(12, 0));
                }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    string mode = frame.Parameters.GetEnum("mode");
                    if (mode == "ThrowEvaluateVisuals") throw new InvalidOperationException("failed");
                    if (mode == "geometry")
                    {
                        foreach (var token in frame.FinalGeometry.Tokens)
                            foreach (var ruby in token.Ruby)
                                if (ruby.Geometry.BaseWorldMatrix != token.BaseGeometry.BaseWorldMatrix)
                                    throw new InvalidOperationException("ruby must use final family matrix");
                        var builder = ImmutableArray.CreateBuilder<Dot>();
                        foreach (var cursor in frame.FinalGeometry.Playback.GetCursors(CursorAnchorMode.AdvanceCenter))
                            builder.Add(new Dot(cursor.Position, 6, new ColorRgba(1, 0, 0, .5)));
                        output.DrawDots(new("cursor"), builder.ToImmutable(), OverlayLayer.AboveText);
                    }
                    else
                        output.DrawDots(new("clock"),
                            [new Dot(new Point2(20 + frame.TimeUs / 1000d, 30), 4, new ColorRgba(1, 0, 0, .5))],
                            OverlayLayer.AboveText);
                }
            }

            public sealed class PureDots : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    string mode = source.Parameters.GetEnum("mode");
                    return new VisualState
                    {
                        VisualOutputs = mode == "none"
                            ? ImmutableArray<VisualOutputDeclaration>.Empty
                            : [new(new("dot"), VisualPrimitiveKind.Dots, mode == "behind" ? OverlayLayer.BehindText : OverlayLayer.AboveText)],
                    };
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    if (frame.Parameters.GetEnum("mode") != "mark") return;
                    foreach (var line in frame.Presentations.LinesInBinding)
                    {
                        if (line.LineId.Value != "first") continue;
                        Affine2 basis = Affine2.Identity;
                        bool found = false;
                        foreach (var item in frame.LineGeometry)
                            if (item.Target == line.Target) { basis = item.BaseGeometry.BaseWorldMatrix; found = true; break; }
                        Affine2 inverse;
                        if (!found || !basis.TryInverse(out inverse)) throw new InvalidOperationException("invertible base required");
                        output.AppendTransform(new TextTarget.Line(line.Target), inverse.Compose(Affine2.Translation(30, -16)).Compose(basis));
                    }
                    foreach (var mark in frame.Presentations.MarksInBinding)
                        output.AppendTransform(new TextTarget.Mark(mark.Target), Affine2.Translation(5, -3));
                }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    string mode = frame.Parameters.GetEnum("mode");
                    if (mode == "none") return;
                    Point2 point;
                    if (mode == "mark")
                    {
                        var mark = frame.FinalGeometry.Marks[0].Geometry;
                        point = mark.BaseWorldMatrix.Map(mark.AdvanceBounds.Center);
                    }
                    else if (mode == "cursor")
                        point = frame.FinalGeometry.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)[0].Position;
                    else
                        point = new Point2(frame.Parameters.GetNumber("x"), frame.Parameters.GetNumber("y"));
                    double radius = mode == "large" ? 1e100 : mode == "tiny" ? 1e-200 : frame.Parameters.GetNumber("radius");
                    output.DrawDots(new("dot"), [new Dot(point, radius, frame.Parameters.GetColor("color"))],
                        mode == "behind" ? OverlayLayer.BehindText : OverlayLayer.AboveText);
                }
            }

            public sealed class UnavailableProbe : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                    => new VisualState { VisualOutputs = [new(new("dot"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] };
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    if (frame.FinalGeometry.Playback.IsAvailable)
                        throw new InvalidOperationException("backend did not provide glyph geometry");
                    if (frame.Parameters.GetEnum("mode") == "call-cursors")
                        frame.FinalGeometry.Playback.GetCursors(CursorAnchorMode.Baseline);
                }
            }
            """,
            [Fx.V2.CapabilityNames.LineAffine, Fx.V2.CapabilityNames.TokenAffine, Fx.V2.CapabilityNames.MarkAffine, Fx.V2.CapabilityNames.VisualsDots],
            entryType: "VisualPure",
            exportId: "VisualPure",
            parameters: parameters,
            outputs:
            [
                V2RenderPackageHelper.Output("token", "VisualTransform", "tokenFamily", Fx.V2.CapabilityNames.TokenAffine),
                V2RenderPackageHelper.Output("clock", "Overlay", "canvas", Fx.V2.CapabilityNames.VisualsDots),
                V2RenderPackageHelper.Output("cursor", "Overlay", "canvas", Fx.V2.CapabilityNames.VisualsDots),
            ],
            packageId: "demo.visual.stage.v2",
            manifestChange: manifest =>
            {
                var paramIds = new JsonArray(parameters.Select(static p => JsonValue.Create(p!["id"]!.GetValue<string>())).ToArray());
                var bindings = new JsonArray("project", "node", "branch", "lines", "tokens", "clips");
                var exports = manifest["exports"]!.AsArray();
                exports.Add(new JsonObject
                {
                    ["id"] = "PureDots",
                    ["kind"] = "controller",
                    ["name"] = "PureDots",
                    ["entryType"] = "PureDots",
                    ["parameters"] = JsonNode.Parse(paramIds.ToJsonString()),
                    ["bindingKinds"] = JsonNode.Parse(bindings.ToJsonString()),
                    ["outputs"] = new JsonArray(
                        V2RenderPackageHelper.Output("line", "VisualTransform", "line", Fx.V2.CapabilityNames.LineAffine),
                        V2RenderPackageHelper.Output("mark", "VisualTransform", "textMark", Fx.V2.CapabilityNames.MarkAffine),
                        V2RenderPackageHelper.Output("dot", "Overlay", "canvas", Fx.V2.CapabilityNames.VisualsDots)),
                });
                exports.Add(new JsonObject
                {
                    ["id"] = "UnavailableProbe",
                    ["kind"] = "controller",
                    ["name"] = "UnavailableProbe",
                    ["entryType"] = "UnavailableProbe",
                    ["parameters"] = JsonNode.Parse(paramIds.ToJsonString()),
                    ["bindingKinds"] = JsonNode.Parse(bindings.ToJsonString()),
                    ["outputs"] = new JsonArray(
                        V2RenderPackageHelper.Output("dot", "Overlay", "canvas", Fx.V2.CapabilityNames.VisualsDots)),
                });
            });
        return V2RenderPackageHelper.CompileAsync(contents);
    });
    internal static ProjectSnapshot Snapshot()
    {
        var line = LyricLine.Create("row", "田田", new(0, 100_000), TimingStatus.Edited,
            [LyricToken.Create("a", "田", new(1_001, 10_000), TimingStatus.Edited),
             LyricToken.Create("b", "田", new(10_000, 40_000), TimingStatus.Edited)])
            with { RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "た")] };
        var project = Project.Create("visual", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 100_000), LyricsDocument.Create([line]));
        var tree = project.LyricPresentation; var root = tree.FindNode(tree.FindProgram(project.Lyrics.PrimaryBranch!.Id)!.RootContainerId)!;
        project = project with { LyricStyle = project.LyricStyle with { SungColor = new(255, 255, 255), UnsungColor = new(255, 255, 255), Opacity = 1 },
            LyricPresentation = tree with { Nodes = tree.Nodes.Replace(root, root with
                { Placement = ScenePlacement.Create(new(.5, .5), rotationDegrees: 12, scale: .9) }) } };
        return ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
    }
    internal static async Task<CompiledFrameEffect> Definition(string mode = "normal", string entry = "VisualPure", Fx.EffectBinding? binding = null,
        Fx.EffectCapabilities? capabilities = null,
        double x = 100, double y = 80, double radius = 20, Fx.ColorRgba? color = null)
    {
        var compiled = await Compiled.Value;
        var effect = compiled.CreateEffect(entry, new("visual"), binding ?? new Fx.EffectBinding.Project(), 0) with
        {
            Parameters = new Fx.ParameterValues(new Dictionary<string, Fx.ParameterValue>
            {
                ["mode"] = new Fx.ParameterValue.Choice(mode),
                ["x"] = new Fx.ParameterValue.Number(x),
                ["y"] = new Fx.ParameterValue.Number(y),
                ["radius"] = new Fx.ParameterValue.Number(radius),
                ["color"] = new Fx.ParameterValue.Color(color ?? new(1, 0, 0, .5)),
            }),
            Subscription = new Fx.SourceSubscription.Project(),
        };
        return capabilities.HasValue ? effect with { Capabilities = capabilities.Value } : effect;
    }
    internal static PureFrameSourceFactory Pure(ImmutableArray<CompiledFrameEffect> effects, Fx.VisualOutputBudget? budget = null)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline(), visualBudget: budget);
    internal static RenderTarget Target(ProjectSnapshot snapshot) => RenderTarget.FromComposition(snapshot.Composition);
}
