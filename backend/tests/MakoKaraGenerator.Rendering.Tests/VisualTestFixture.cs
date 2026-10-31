using System.Collections.Immutable;
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
    private static readonly Lazy<Task<ScriptCompilationResult>> Artifact = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/VisualProbe.cs", """
            using System;
            using System.Linq;
            using System.Collections.Immutable;
            using MakoKaraGenerator.Scripting;
            public sealed class VisualProbe : EffectBase
            {
                private long shared, visual, lastText;
                private int starts;
                private string mode = "", reason = "none";
                private VisualWriter saved;
                public override void Initialize(EffectInitializationContext context) { mode = context.Parameters.GetEnum("mode"); }
                public override void OnEvent(TimelineEvent evt, EventContext context)
                { if (evt is TokenStarted) starts++; reason = evt.ReplayReason.ToString(); }
                public override void Advance(SimulationStep interval, StateContext context)
                { shared += interval.DurationUs; context.Diagnostics.Report(DiagnosticSeverity.Info, "S", interval.StartUs + ":" + interval.EndUs); }
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    lastText = frame.TimeUs;
                    frame.Diagnostics.Report(DiagnosticSeverity.Info, "T", frame.TimeUs + ":" + shared + ":" + starts);
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.Transforms.ScaleTokenFamily(token.WithRuby, 1 + starts * .2, Pivot.BaseAdvanceCenter);
                }
                public override void AdvanceVisuals(VisualSimulationStep interval, VisualStateContext context)
                {
                    if (!interval.IsValid || shared != interval.EndUs || lastText != interval.EndUs
                        || context.PreviousGeometry.TimeUs != interval.StartUs || context.CurrentGeometry.TimeUs != interval.EndUs
                        || context.PreviousHistory.LastBoundaryUs != interval.StartUs || context.History.LastBoundaryUs != interval.EndUs
                        || context.PreviousHistory.Records.Any(item => item.TimeUs > interval.StartUs))
                        throw new Exception("visual step must follow final text with both exact prefixes");
                    visual += interval.DurationUs;
                    context.Diagnostics.Report(DiagnosticSeverity.Info, "V", interval.StartUs + ":" + interval.EndUs + ":" + starts);
                    Fail("AdvanceVisuals", context.Diagnostics);
                }
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                {
                    if (lastText != frame.TimeUs || shared != frame.LastSimulationBoundaryUs || visual != shared
                        || frame.BoundaryGeometry.TimeUs != frame.LastSimulationBoundaryUs)
                        throw new Exception("visual presentation must not advance state");
                    frame.Diagnostics.Report(DiagnosticSeverity.Info, "P", frame.TimeUs + ":" + shared + ":" + visual + ":" + starts + ":" + frame.FractionalElapsedUs + ":" + reason);
                    Fail("EvaluateVisuals", frame.Diagnostics);
                    if (mode == "expired" && saved != null) saved.Overlay.DrawDots(new("old"), [], OverlayLayer.AboveText);
                    saved = output;
                    if (mode == "geometry")
                    {
                        foreach (var token in frame.FinalGeometry.Tokens)
                            if (token.Ruby.Any(ruby => ruby.Geometry.BaseWorldMatrix != token.BaseGeometry.BaseWorldMatrix))
                                throw new Exception("ruby must use final family matrix");
                        var dots = frame.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)
                            .Select(cursor => new Dot(cursor.Position, 6, new ColorRgba(1, 0, 0, .5))).ToImmutableArray();
                        output.Overlay.DrawDots(new("cursor"), dots, OverlayLayer.AboveText);
                    }
                    else output.Overlay.DrawDots(new("clock"), [new Dot(new Point2(20 + (visual + frame.FractionalElapsedUs) / 1000d, 30), 4, new ColorRgba(1, 0, 0, .5))], OverlayLayer.AboveText);
                }
                private void Fail(string stage, DiagnosticReporter reporter)
                {
                    if (mode == "Error" + stage) { try { reporter.Report(DiagnosticSeverity.Error, "VisualError", "failed", new SourceLocation("src/VisualProbe.cs", 20, 4)); } catch { } }
                    if (mode == "Throw" + stage) throw new Exception("failed");
                    if (mode == "Cancel" + stage) throw new OperationCanceledException();
                }
            }
            public sealed class PureDots : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    if (frame.Parameters.GetEnum("mode") != "mark") return;
                    foreach (var line in frame.Presentations.LinesInBinding.Where(line => line.LineId.Value == "first"))
                    {
                        var basis = frame.LineGeometry.Single(item => item.Target == line.Target).BaseGeometry.BaseWorldMatrix;
                        if (!basis.TryInverse(out var inverse)) throw new Exception("invertible base required");
                        output.Transforms.ApplyAffine(line.Target, inverse.Compose(Affine2.Translation(30, -16)).Compose(basis));
                    }
                    foreach (var mark in frame.Presentations.MarksInBinding) output.Transforms.ApplyAffine(mark.Target, Affine2.Translation(5, -3));
                }
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                {
                    string mode = frame.Parameters.GetEnum("mode");
                    if (mode == "history") { var history = frame.History; }
                    if (mode == "none") return;
                    var mark = mode == "mark" ? frame.FinalGeometry.Marks.First().Geometry : null;
                    var point = mark != null ? mark.BaseWorldMatrix.Map(mark.AdvanceBounds.Center)
                        : mode == "cursor" ? frame.Playback.GetCursors(CursorAnchorMode.AdvanceCenter).Single().Position
                        : new Point2(frame.Parameters.GetNumber("x"), frame.Parameters.GetNumber("y"));
                    double radius = mode == "large" ? 1e100 : mode == "tiny" ? 1e-200 : frame.Parameters.GetNumber("radius");
                    output.Overlay.DrawDots(new("dot"), [new Dot(point, radius, frame.Parameters.GetColor("color"))],
                        mode == "behind" ? OverlayLayer.BehindText : OverlayLayer.AboveText);
                }
            }
            public sealed class UnavailableProbe : EffectBase
            {
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                {
                    if (frame.FinalGeometry.Playback.IsAvailable) throw new Exception("backend did not provide glyph geometry");
                    try { frame.FinalGeometry.Playback.GetCursors(CursorAnchorMode.Baseline); throw new Exception("unavailable must not become empty cursors"); }
                    catch (EffectOutputException error)
                    {
                        if (error.Code != "PlaybackGeometryUnavailable") throw;
                        frame.Diagnostics.Report(DiagnosticSeverity.Info, error.Code, "explicitly unavailable");
                    }
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
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
    internal static async Task<CompiledFrameEffect> Definition(string mode = "normal", string entry = "VisualProbe", Fx.EffectBinding? binding = null,
        Fx.EffectCapabilities capabilities = Fx.EffectCapabilities.Transforms | Fx.EffectCapabilities.Overlay,
        double x = 100, double y = 80, double radius = 20, Fx.ColorRgba? color = null)
        => new(new("visual"), await Artifact.Value, entry, new(new Dictionary<string, Fx.ParameterValue>
        { ["mode"] = new Fx.ParameterValue.Choice(mode), ["x"] = new Fx.ParameterValue.Number(x), ["y"] = new Fx.ParameterValue.Number(y),
            ["radius"] = new Fx.ParameterValue.Number(radius), ["color"] = new Fx.ParameterValue.Color(color ?? new(1, 0, 0, .5)) }),
            binding ?? new Fx.EffectBinding.Project(), capabilities) { Subscription = new Fx.SourceSubscription.Project() };
    internal static TimelineFrameSourceFactory Timeline(ImmutableArray<CompiledFrameEffect> effects, Fx.ReplayReason reason = Fx.ReplayReason.Initial,
        Fx.VisualOutputBudget? budget = null) => new(effects, Fingerprint, static () => new SkiaRenderPipeline(), reason, visualBudget: budget);
    internal static PureFrameSourceFactory Pure(ImmutableArray<CompiledFrameEffect> effects, Fx.VisualOutputBudget? budget = null)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline(), visualBudget: budget);
    internal static RenderTarget Target(ProjectSnapshot snapshot) => RenderTarget.FromComposition(snapshot.Composition);
}
