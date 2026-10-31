using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Export.Tests;

public sealed class EffectFailureExportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 效果失败和准备帧错误中断真实编码且保留原输出(bool protocol)
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Path, "existing.mp4"); byte[] original = [7, 3, 1, 9];
        await File.WriteAllBytesAsync(output, original);
        var failure = protocol ? (Exception)new PreparedFrameException("MissingPreparedFrame", "missing")
            : new EffectFailureException(new EffectInstanceId("broken"), "artifact", "Evaluate", 0, "InvalidTransform", new InvalidOperationException("bad"));
        using SkiaRenderPipeline native = new();
        var pipeline = new FailingPipeline(native, failure);
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => exporter.ExportAsync(Project(), new() { OutputPath = output, Overwrite = true }));
        Assert.Same(failure, thrown); Assert.Equal(1, pipeline.RenderCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Equal([output], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task 普通后端错误仍按原契约占位并记录而不会与效果失败混淆()
    {
        using var directory = new TemporaryDirectory(); using SkiaRenderPipeline native = new();
        var pipeline = new FailingPipeline(native, new InvalidOperationException("ordinary backend error"));
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var report = await exporter.ExportAsync(Project(), new() { OutputPath = Path.Combine(directory.Path, "normal.mp4") });
        Assert.Equal(1, report.FailedFrames); Assert.True(report.IsVerified); Assert.Equal(6, pipeline.RenderCount);
    }

    [Theory]
    [InlineData("Evaluate")] [InlineData("AdvanceVisuals")] [InlineData("EvaluateVisuals")]
    public async Task 真实作者Error经过语义会话阻止编码而不生成占位成功成片(string stage)
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Path, "existing.mp4"); byte[] original = [9, 2, 4];
        await File.WriteAllBytesAsync(output, original);
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var artifact = await compiler.CompileAsync([new("src/ErrorEffect.cs", """
            using MakoKaraGenerator.Scripting;
            public sealed class ErrorEffect : EffectBase
            {
                private string stage = "";
                public override void Initialize(EffectInitializationContext context) { stage = context.Parameters.GetEnum("stage"); }
                public override void Evaluate(FrameContext frame, FrameWriter writer)
                {
                    Fail("Evaluate", frame.TimeUs, frame.Diagnostics);
                }
                public override void AdvanceVisuals(VisualSimulationStep interval, VisualStateContext context) { Fail("AdvanceVisuals", interval.EndUs, context.Diagnostics); }
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter writer) { Fail("EvaluateVisuals", frame.TimeUs, frame.Diagnostics); }
                private void Fail(string current, long time, DiagnosticReporter reporter)
                {
                    if (stage == current && time >= 70000)
                        reporter.Report(DiagnosticSeverity.Error, "AuthorFailed", "requested failure", new SourceLocation("src/ErrorEffect.cs", 7, 5));
                }
            }
            """)]);
        Assert.True(artifact.Succeeded, string.Join('\n', artifact.Diagnostics));
        var project = Project();
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = "diagnostic-export" };
        TimelineFrameSourceFactory factory = new([new(new("error-effect"), artifact, "ErrorEffect", new(new Dictionary<string, ParameterValue> { ["stage"] = new ParameterValue.Choice(stage) }),
            new EffectBinding.Project(), EffectCapabilities.None)], "diagnostic-export", static () => new SkiaRenderPipeline(), ReplayReason.Export);
        await using var source = await factory.CreateAsync(snapshot);
        using SkiaRenderPipeline native = new();
        PreparedPipeline pipeline = new(native, source, snapshot);
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project, new() { OutputPath = output, Overwrite = true }));
        Assert.Equal("AuthorFailed", error.Code); Assert.Equal(stage, error.Stage); Assert.Equal(75_000, error.TimeUs);
        Assert.Equal(new SourceLocation("src/ErrorEffect.cs", 7, 5), error.Diagnostic!.Diagnostic.Location);
        Assert.Equal(3, pipeline.RenderedCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Equal([output], Directory.GetFiles(directory.Path));
    }
    // 受控注入 R2 准备接缝；生产持久实例／异步导出工厂在 R4／R5 接入。
    private sealed class PreparedPipeline(IRenderPipeline native, IEvaluatedFrameSource source, ProjectSnapshot snapshot) : IRenderPipeline
    {
        public string BackendName => "controlled prepared frame export";
        public int RenderedCount { get; private set; }
        public RenderFrame Render(RenderRequest request)
        {
            var frame = source.PrepareAsync(request.TimeUs, OutputMode.Opaque, request.ResolveTarget()).AsTask().GetAwaiter().GetResult();
            var result = native.Render(request with { Project = snapshot, PreparedFrame = frame });
            RenderedCount++; return result;
        }
    }

    private static Core.Projects.Project Project()
    {
        var line = LyricLine.Create("row", "歌", new(0, 200_000), TimingStatus.Edited,
            [LyricToken.Create("word", "歌", new(0, 200_000), TimingStatus.Edited)]);
        return Core.Projects.Project.Create("error gate", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 200_000), LyricsDocument.Create([line]));
    }
    private sealed class FailingPipeline(IRenderPipeline native, Exception failure) : IRenderPipeline
    {
        public string BackendName => "failure gate";
        public int RenderCount { get; private set; }
        public RenderFrame Render(RenderRequest request)
        {
            if (++RenderCount == 1) throw failure;
            return native.Render(request);
        }
    }
}
