using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;

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
    [InlineData("Declare")] [InlineData("Evaluate")] [InlineData("EvaluateVisuals")]
    public async Task 真实作者Error经过语义会话阻止编码而不生成占位成功成片(string stage)
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Path, "existing.mp4"); byte[] original = [9, 2, 4];
        await File.WriteAllBytesAsync(output, original);
        var package = await V2ExportPackageHelper.CompileControllerAsync("""
            using System;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class ErrorEffect : IPureKaraokeEffect, IPureVisualEffect
            {
                private sealed record State(string Stage) : PreparedEffect;
                public PreparedEffect Declare(DeclareContext source)
                {
                    string stage = source.Parameters.GetEnum("stage");
                    if (stage == "Declare") throw new InvalidOperationException("requested failure");
                    return new State(stage) { VisualOutputs = [new(new("dots"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] };
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                    => Fail(((State)data).Stage, "Evaluate", frame.TimeUs);
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                    => Fail(((State)data).Stage, "EvaluateVisuals", frame.TimeUs);
                private static void Fail(string expected, string current, long time)
                {
                    if (expected == current && time >= 70000) throw new InvalidOperationException("requested failure");
                }
            }
            """,
            [CapabilityNames.VisualsDots],
            entryType: "ErrorEffect",
            exportId: "error-effect",
            parameters: [new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "stage", ["type"] = "enum", ["default"] = "Evaluate",
                ["values"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["value"] = "Declare", ["label"] = "Declare" },
                    new System.Text.Json.Nodes.JsonObject { ["value"] = "Evaluate", ["label"] = "Evaluate" },
                    new System.Text.Json.Nodes.JsonObject { ["value"] = "EvaluateVisuals", ["label"] = "EvaluateVisuals" }),
                ["label"] = "Stage",
            }],
            outputs: [new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "dots", ["kind"] = "Overlay", ["target"] = "canvas",
                ["requires"] = new System.Text.Json.Nodes.JsonArray(CapabilityNames.VisualsDots),
                ["dependsOn"] = new System.Text.Json.Nodes.JsonArray(),
            }]);
        var project = Project();
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = "diagnostic-export" };
        var effect = package.CreateEffect("error-effect", new("error-effect"), new EffectBinding.Project(), 1,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { stage }));
        PureFrameSourceFactory factory = new([effect], "diagnostic-export", static () => new SkiaRenderPipeline());
        if (stage == "Declare")
        {
            var declareError = await Assert.ThrowsAsync<EffectFailureException>(() => factory.CreateAsync(snapshot).AsTask());
            Assert.Equal("ScriptExecutionFailed", declareError.Code); Assert.Equal("Declare", declareError.Stage); Assert.Equal(0, declareError.TimeUs);
            Assert.Equal(original, await File.ReadAllBytesAsync(output));
            Assert.Equal([output], Directory.GetFiles(directory.Path));
            return;
        }
        await using var source = await factory.CreateAsync(snapshot);
        using SkiaRenderPipeline native = new();
        PreparedPipeline pipeline = new(native, source, snapshot);
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project, new() { OutputPath = output, Overwrite = true }));
        Assert.Equal("ScriptExecutionFailed", error.Code); Assert.Equal(stage, error.Stage); Assert.Equal(100_000, error.TimeUs);
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
