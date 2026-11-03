using MakoKaraGenerator.Scripting.Compilation;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using SkiaSharp;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class TimedRubyWorkerIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task 真实静态与包Worker保持独立注音像素PNG及回退寻址且脚本只能读取副本(bool package, bool transparent)
    {
        PackageWorkerRuntimeFixture.AssertFixedFont();
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var snapshot = Source(package);
        byte[] bytes = package ? PackageProjectSnapshotCodec.Encode(snapshot) : StaticProjectSnapshotCodec.Encode(snapshot);
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos, ReplayReason.Initial);
        await using var expected = await runtime.CreateAsync(snapshot);
        await using WorkerProcessProxy worker = new(new(PackageWorkerRuntimeFixture.WorkerApphost()));
        var handle = package ? await worker.OpenPackageSessionAsync(bytes, TimeSpan.FromSeconds(30))
            : await worker.OpenStaticSessionAsync(bytes, TimeSpan.FromSeconds(30));
        int pid = Assert.IsType<int>(worker.ProcessId);
        using var process = Process.GetProcessById(pid);
        var group = OwnedProcessGroup.TryCapture(process); Assert.NotNull(group);
        long sequence = 0; Dictionary<long, byte[]> frames = [];
        foreach (long time in new long[] { 0, 150_000, 250_000, 500_000, 800_000, 250_000 })
        {
            var request = WorkerPackageSessionIntegrationTests.Request(snapshot, time, transparent);
            using var direct = await expected.RenderAsync(request);
            var frameRequest = new StaticWorkerFrameRequest(handle.SessionId, handle.SourceSha256, 320, 180, time, transparent, ++sequence);
            var actual = package ? await worker.RenderPackageSessionAsync(frameRequest, TimeSpan.FromSeconds(20))
                : await worker.RenderStaticSessionAsync(frameRequest, TimeSpan.FromSeconds(20));
            Assert.Equal(pid, worker.ProcessId); Assert.Equal(time, actual.FrameTimeUs);
            Assert.Equal(direct.PixelMemory.ToArray(), actual.Payload);
            using var transported = new RenderFrame(actual.Payload, direct.Target, direct.Format, time);
            Assert.Equal(Png(direct), Png(transported));
            if (frames.TryGetValue(time, out var repeated)) Assert.Equal(repeated, actual.Payload);
            frames[time] = actual.Payload;
            if (package) Assert.Contains(actual.Diagnostics!.Occurrences, value => value.Diagnostic.Code == "TimedRubyReadOnly"
                && value.Diagnostic.Message == "early:100000:300000:せ");
        }
        Assert.False(frames[150_000].AsSpan().SequenceEqual(frames[250_000]), "Ruby changes pixels before the base token starts.");
        if (package) await worker.ClosePackageSessionAsync(handle, ++sequence, TimeSpan.FromSeconds(20));
        else await worker.CloseStaticSessionAsync(handle, ++sequence, TimeSpan.FromSeconds(20));
        var reopened = package ? await worker.OpenPackageSessionAsync(bytes, TimeSpan.FromSeconds(30))
            : await worker.OpenStaticSessionAsync(bytes, TimeSpan.FromSeconds(30));
        Assert.NotEqual(handle.SessionId, reopened.SessionId); Assert.Equal(pid, worker.ProcessId);
        var repeatRequest = new StaticWorkerFrameRequest(reopened.SessionId, reopened.SourceSha256, 320, 180, 250_000, transparent, 1);
        var repeat = package ? await worker.RenderPackageSessionAsync(repeatRequest, TimeSpan.FromSeconds(20))
            : await worker.RenderStaticSessionAsync(repeatRequest, TimeSpan.FromSeconds(20));
        Assert.Equal(frames[250_000], repeat.Payload);
        if (package) await worker.ClosePackageSessionAsync(reopened, 2, TimeSpan.FromSeconds(20));
        else await worker.CloseStaticSessionAsync(reopened, 2, TimeSpan.FromSeconds(20));
        await worker.StopAsync(); Assert.Null(worker.ProcessId); Assert.Empty(group.ObserveMembers());
        Assert.False(Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture)));
        output.WriteLine($"Timed ruby real Worker={group.Leader}; package={package}, transparent={transparent}, 7 byteexact BGRA/PNG frames, seek/reopen, group gone.");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 真实Worker注音同帧PNG与几何逐项匹配且时间只改变颜色(bool transparent)
    {
        var snapshot = Source(false); byte[] source = StaticProjectSnapshotCodec.Encode(snapshot);
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos, ReplayReason.Initial);
        await using var expected = await runtime.CreateAsync(snapshot);
        var measurable = Assert.IsAssignableFrom<ISceneMeasurementSession>(expected);
        ProjectGeometryPointDto[]? previous = null; byte[]? previousPng = null;
        foreach (long time in new long[] { 150_000, 250_000 })
        {
            await using WorkerProcessProxy worker = new(new(PackageWorkerRuntimeFixture.WorkerApphost()));
            var request = new SceneMeasurementRequest("0", "time", time.ToString(CultureInfo.InvariantCulture), 320, 180, transparent ? "transparent" : "opaque");
            var input = new WorkerSceneMeasurementInput(request, WorkerProtocol.ComputeSha256(source), string.Empty);
            input = input with { SampleInputFingerprint = WorkerSceneMeasurementContract.Fingerprint(source, input) };
            var result = await worker.InvokeAsync(new(WorkerSceneMeasurementContract.RequestKind, WorkerSceneMeasurementContract.Encode(input, source)));
            int pid = worker.ProcessId!.Value;
            using var process = Process.GetProcessById(pid);
            var group = OwnedProcessGroup.TryCapture(process); Assert.NotNull(group);
            var actual = JsonSerializer.Deserialize<WorkerSceneMeasurementOutput>(result.Payload, SceneMeasurementContract.JsonOptions)!.Measurement;
            using var direct = await measurable.MeasureAsync(WorkerPackageSessionIntegrationTests.Request(snapshot, time, transparent), false);
            Assert.Equal(Png(direct.Frame), actual.Png);
            Assert.Equal(WorkerProtocol.ComputeSha256(actual.Png), actual.PngSha256);
            Assert.Equal(input.SampleInputFingerprint, actual.SampleInputFingerprint);
            var ruby = Assert.Single(actual.Elements, element => element.RubyId == "ruby");
            var expectedRuby = Assert.Single(direct.Scene.Elements, element => element.SourceBinding.RubyId == "ruby");
            Assert.Equal(expectedRuby.WorldCorners.Select(point => new ProjectGeometryPointDto(point.X, point.Y)), ruby.WorldCorners);
            Assert.True(ruby.Visible); Assert.Equal("measuredInk", ruby.GeometryStatus);
            if (previous is not null) { Assert.Equal(previous, ruby.WorldCorners); Assert.False(previousPng!.AsSpan().SequenceEqual(actual.Png)); }
            previous = ruby.WorldCorners; previousPng = actual.Png;
            await worker.StopAsync(); Assert.Empty(group.ObserveMembers()); Assert.False(Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture)));
            output.WriteLine($"Timed ruby same-frame geometry/PNG Worker={pid}; transparent={transparent}, time={time}; exited.");
        }
    }

    private static ProjectSnapshot Source(bool package)
    {
        var snapshot = TimedRubyFixture.Create();
        if (!package) return snapshot;
        var files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("""
            using System;
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    var ruby = frame.Project.Content.GetRuby(new RubyId("ruby"));
                    var first = ruby.Segments[0];
                    var changed = first with { Text = "local copy" };
                    if (ruby.Segments.Length != 2 || ruby.Text != "せん" || first.Id != "early"
                        || first.TimeRange.Value.StartUs != 100000 || first.TimeRange.Value.EndUs != 300000
                        || first.TimingStatus != SourceTimingStatus.Imported || ruby.Segments[0].Text != "せ"
                        || changed.Text == ruby.Segments[0].Text) throw new Exception("TimedRubyProjectionMismatch");
                    frame.Diagnostics.Report(DiagnosticSeverity.Warning, "TimedRubyReadOnly", "early:100000:300000:せ");
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.Transforms.ScaleTokenFamily(token.WithRuby, 1.2, Pivot.BaseAdvanceCenter);
                }
            }
            """);
        var contents = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)),
            EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var original = PackageWorkerRuntimeFixture.Create("scale").Effects.Instances[0];
        return PackageWorkerRuntimeFixture.Fingerprint(snapshot with { Effects = new([contents],
            [original with { PackageId = contents.PackageId, Revision = contents.Revision }]) });
    }

    private static byte[] Png(RenderFrame frame)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var pixels = frame.PixelMemory.ToArray(); Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
