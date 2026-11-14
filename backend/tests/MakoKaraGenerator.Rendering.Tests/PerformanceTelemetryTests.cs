using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Rendering.Tests;

[CollectionDefinition("PerformanceTelemetry", DisableParallelization = true)]
public sealed class PerformanceTelemetrySerialTests;

[Collection("PerformanceTelemetry")]
public sealed class PerformanceTelemetryTests
{
    private static PerformanceEvent Decode(string line) => JsonSerializer.Deserialize<PerformanceEvent>(line[PerformanceTelemetry.Prefix.Length..])!;

    [Fact]
    public async Task BackgroundDrawAndSubmitPhasesReachCaptureDetailAndSummary()
    {
        ConcurrentBag<string> lines = [];
        List<PerformanceEvent> captures = [];
        PerformanceTelemetry.Configure(lines.Add, detailEvery: 1, intervalSeconds: 3600, maxEventsPerSecond: 100);
        try
        {
            for (int i = 0; i < 2; i++)
            {
                using var frame = PerformanceTelemetry.BeginFrame("session", "gpu", i, 2, 2, capture: true);
                foreach (var phase in new[] { PerformancePhase.BackgroundAcquire, PerformancePhase.BackgroundCopy, PerformancePhase.Draw, PerformancePhase.GpuSubmit })
                    using (PerformanceTelemetry.Measure(phase)) Thread.Sleep(1);
                frame.SetOutcome("ok");
                captures.Add(frame.Capture()!);
            }
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }

        PerformanceEvent[] details = lines.Select(Decode).Where(e => e.Kind == "frame").OrderBy(e => e.Sequence).ToArray();
        PerformanceEvent summary = Assert.Single(lines.Select(Decode), e => e.Kind == "summary");
        Assert.Equal(2, details.Length);
        Assert.Equal(2, summary.Frames);
        foreach (Func<PerformanceEvent, double> phase in new Func<PerformanceEvent, double>[]
        {
            e => e.BackgroundAcquireMs, e => e.BackgroundCopyMs, e => e.DrawMs, e => e.GpuSubmitMs
        })
        {
            Assert.All(captures, e => Assert.True(phase(e) > 0));
            Assert.Equal(captures.Select(phase), details.Select(phase));
            Assert.Equal(details.Sum(phase), phase(summary));
        }
        Assert.Equal(0, summary.RasterMs);
        Assert.All(lines, line => Assert.StartsWith("MAKO_PERFORMANCE_V1 ", line));
    }

    [Fact]
    public async Task RateOneRotatesBackendsAndPreservesDeferredWindow()
    {
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, intervalSeconds: 1, maxEventsPerSecond: 1);
        try
        {
            for (int round = 0; round < 4; round++)
            {
                using (var frame = PerformanceTelemetry.BeginFrame("cpu", "cpu", round, 1, 1)) frame.SetOutcome("ok");
                using (var frame = PerformanceTelemetry.BeginFrame("gpu", "gpu", round, 1, 1)) frame.SetOutcome("ok");
                await Task.Delay(1100);
            }
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }
        PerformanceEvent[] summaries = lines.Select(Decode).Where(e => e.Kind == "summary" && e.Frames > 0).ToArray();
        Assert.Contains(summaries, e => e.Backend == "cpu");
        Assert.Contains(summaries, e => e.Backend == "gpu" && e.Frames >= 2 && e.WindowMs >= 1500);
    }

    [Theory]
    [InlineData("off", 0, 0)]
    [InlineData("summary", 2, 0)]
    [InlineData("detailed", 2, 1)]
    public async Task EnvironmentModesAreApplied(string mode, int summaries, int details)
    {
        string? previous = Environment.GetEnvironmentVariable("MAKO_PERFORMANCE_MODE");
        string? previousSample = Environment.GetEnvironmentVariable("MAKO_PERFORMANCE_DETAIL_EVERY");
        ConcurrentBag<string> lines = [];
        try
        {
            Environment.SetEnvironmentVariable("MAKO_PERFORMANCE_MODE", mode);
            Environment.SetEnvironmentVariable("MAKO_PERFORMANCE_DETAIL_EVERY", null);
            PerformanceTelemetry.ConfigureFromEnvironment(lines.Add);
            using (var frame = PerformanceTelemetry.BeginFrame("s", "cpu", 1, 1, 1)) frame.SetOutcome("ok");
            using (PerformanceTelemetry.MeasureExportQueueWait("s", 1)) { }
            await PerformanceTelemetry.ShutdownAsync();
            Assert.Equal(summaries, lines.Count(line => Decode(line).Kind == "summary"));
            Assert.Equal(details, lines.Count(line => Decode(line).Kind == "frame"));
            Assert.Equal(details, lines.Count(line => Decode(line).Kind == "exportQueueWait"));
            if (mode == "off") Assert.Empty(lines);
            else
            {
                Assert.Equal(1, lines.Select(Decode).Where(e => e.Kind == "summary").Sum(e => e.Frames));
                Assert.Contains(lines.Select(Decode), e => e is { Kind: "summary", Backend: "unknown", Frames: 0, ExportQueueWaitCalls: 1 });
            }
        }
        finally
        {
            await PerformanceTelemetry.ShutdownAsync();
            Environment.SetEnvironmentVariable("MAKO_PERFORMANCE_MODE", previous);
            Environment.SetEnvironmentVariable("MAKO_PERFORMANCE_DETAIL_EVERY", previousSample);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ExportQueueWaitSurvivesOperationOnlyWindow(int detailEvery)
    {
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, detailEvery: detailEvery, intervalSeconds: 3600, maxEventsPerSecond: 100);
        try
        {
            // No ambient frame is required. Copying a scope must not count the wait twice.
            using (PerformanceTelemetry.BeginRequest("request-secret"))
            {
                var wait = PerformanceTelemetry.MeasureExportQueueWait("session-secret", 1);
                var copy = wait;
                await Task.Delay(1);
                wait.Dispose(); copy.Dispose();
                Assert.Throws<OperationCanceledException>((Action)(() =>
                {
                    using var cancelled = PerformanceTelemetry.MeasureExportQueueWait("session-secret", 2);
                    throw new OperationCanceledException();
                }));
            }
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }

        PerformanceEvent summary = Assert.Single(lines.Select(Decode), e => e.Kind == "summary");
        Assert.Equal("unknown", summary.Backend);
        Assert.Equal(2, summary.ExportQueueWaitCalls);
        Assert.True(summary.ExportQueueWaitMs > 0);
        Assert.Equal(0, summary.Frames);
        Assert.Equal(0, summary.Errors);
        Assert.Equal(0, summary.Cancelled);
        Assert.Equal(0, summary.WallMs);
        Assert.Equal(0, summary.MaxWallMs);
        Assert.Equal(0, summary.ActiveFrames);
        PerformanceEvent[] details = lines.Select(Decode).Where(e => e.Kind == "exportQueueWait").ToArray();
        Assert.Equal(detailEvery == 0 ? 0 : 2, details.Length);
        if (detailEvery > 0)
        {
            Assert.Equal(details.Sum(e => e.ExportQueueWaitMs), summary.ExportQueueWaitMs);
            Assert.All(details, e =>
            {
                Assert.Equal("unknown", e.Backend);
                Assert.Equal(1, e.ExportQueueWaitCalls);
                Assert.Equal(16, e.Request.Length);
                Assert.Equal(16, e.Session.Length);
                Assert.Equal(0, e.Frames);
                Assert.Equal(0, e.WallMs);
                Assert.Equal(0, e.MaxWallMs);
            });
        }
        Assert.DoesNotContain(lines, line => line.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 单帧捕获不依赖全局收集器且不写日志()
    {
        await PerformanceTelemetry.ShutdownAsync();
        Assert.False(PerformanceTelemetry.IsEnabled);
        using (var off = PerformanceTelemetry.BeginFrame("s", "cpu", 1, 2, 2)) Assert.Null(off.Capture());
        PerformanceEvent captured;
        using (var frame = PerformanceTelemetry.BeginFrame("s", "cpu", 2, 2, 2, capture: true))
        {
            using (PerformanceTelemetry.Measure(PerformancePhase.Prepare)) Thread.Sleep(5);
            using (PerformanceTelemetry.MeasureScriptIdentity(PerformanceTelemetry.CreateScriptIdentity("hash|fx|pkg"), "Advance")) { }
            frame.SetOutcome("ok");
            captured = frame.Capture()!;
        }
        Assert.NotNull(captured);
        Assert.True(captured.PrepareMs >= 4);
        Assert.Equal(1, captured.ScriptCalls);
        Assert.Equal(PerformanceTelemetry.CreateScriptIdentity("hash|fx|pkg"), Assert.Single(captured.Scripts).Identity);
        Assert.False(PerformanceTelemetry.IsEnabled);

        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, intervalSeconds: 1, maxEventsPerSecond: 100);
        try
        {
            using (var frame = PerformanceTelemetry.BeginFrame("s", "cpu", 3, 2, 2, capture: true))
            {
                frame.SetOutcome("ok");
                Assert.NotNull(frame.Capture());
            }
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }
        // 单帧捕获照常计入全局摘要，但不额外产生明细行（detailEvery=0）。
        Assert.DoesNotContain(lines, line => Decode(line).Kind == "frame");
    }

    [Fact]
    public async Task HeartbeatCoversIdleAndActiveFrames()
    {
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, intervalSeconds: 1, maxEventsPerSecond: 100);
        try
        {
            await Task.Delay(1300);
            Assert.Contains(lines, line => Decode(line) is { Kind: "summary", Frames: 0, ActiveFrames: 0, WindowMs: > 0 });
            using var frame = PerformanceTelemetry.BeginFrame("session", "gpu", 1, 2, 2);
            await Task.Delay(1300);
            Assert.Contains(lines, line => Decode(line) is { Kind: "summary", ActiveFrames: > 0, LongestActiveFrameMs: > 0, ProcessWorkingSetBytes: > 0 });
            frame.SetOutcome("cancelled");
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }
        Assert.Contains(lines, line => Decode(line) is { Kind: "summary", Cancelled: 1 });
    }

    [Fact]
    public async Task AsyncFramesAreIsolatedAndScopesAreIdempotent()
    {
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, detailEvery: 1, intervalSeconds: 3600, maxEventsPerSecond: 100);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
            {
                using var frame = PerformanceTelemetry.BeginFrame("session", "cpu", i, 1, 1);
                var phase = PerformanceTelemetry.Measure(PerformancePhase.Graph);
                var script = PerformanceTelemetry.MeasureScript("identity", "Evaluate");
                await Task.Yield();
                script.Dispose(); script.Dispose(); phase.Dispose(); phase.Dispose();
                using (var nested = PerformanceTelemetry.BeginFrame("nested", "gpu", i, 1, 1)) nested.SetOutcome("ok");
                PerformanceTelemetry.RecordReadback(true, 4);
                frame.SetOutcome("ok"); frame.Dispose(); frame.Dispose();
            }));
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }
        PerformanceEvent[] frames = lines.Select(Decode).Where(e => e.Kind == "frame").ToArray();
        Assert.Equal(16, frames.Length);
        Assert.All(frames.Where(e => e.Backend == "cpu"), e => { Assert.Equal(1, e.ScriptCalls); Assert.Equal(1, e.ImageReads); });
        Assert.All(frames.Where(e => e.Backend == "gpu"), e => { Assert.Equal(0, e.ScriptCalls); Assert.Equal(0, e.ImageReads); });
    }

    [Fact]
    public async Task SlowAndThrowingSinkCannotBlockFrameAndLossIsReported()
    {
        using ManualResetEventSlim entered = new(), release = new();
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(line => { entered.Set(); release.Wait(); lines.Add(line); }, detailEvery: 1, intervalSeconds: 1, maxEventsPerSecond: 2);
        try
        {
            using (var frame = PerformanceTelemetry.BeginFrame("session", "cpu", 0, 1, 1)) frame.SetOutcome("ok");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Stopwatch wall = Stopwatch.StartNew();
            for (int i = 0; i < 300; i++) using (PerformanceTelemetry.BeginFrame("session", "cpu", i, 1, 1)) { }
            Assert.True(wall.Elapsed < TimeSpan.FromSeconds(2));
            release.Set();
            await Task.Delay(1500);
        }
        finally { release.Set(); await PerformanceTelemetry.ShutdownAsync(); }
        Assert.Contains(lines, line => Decode(line) is { Kind: "summary", Dropped: > 0 });
        PerformanceTelemetry.Configure(_ => throw new InvalidOperationException("sink"), intervalSeconds: 1);
        using (PerformanceTelemetry.BeginFrame("session", "cpu", 1, 1, 1)) { }
        await PerformanceTelemetry.ShutdownAsync();
    }

    [Fact]
    public async Task AuthorCallbacksAreAttributedToBoundedHashedIdentities()
    {
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, detailEvery: 1, intervalSeconds: 3600, maxEventsPerSecond: 100);
        try
        {
            var snapshot = VisualTestFixture.Snapshot();
            var effect = await VisualTestFixture.Definition();
            using var frame = PerformanceTelemetry.BeginFrame("session", "cpu", 1, 1, 1);
            await using var session = await VisualTestFixture.Pure([effect]).CreateAsync(snapshot);
            await session.PrepareAsync(18_001, MakoKaraGenerator.Scripting.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
            frame.SetOutcome("ok");
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }
        PerformanceEvent detail = Assert.Single(lines.Select(Decode), e => e.Kind == "frame");
        Assert.Contains(detail.Scripts, script => script.Stage == "other");
        Assert.Contains(detail.Scripts, script => script.Stage == "Evaluate");
        Assert.Contains(detail.Scripts, script => script.Stage == "EvaluateVisuals");
        Assert.All(detail.Scripts, script => Assert.Equal(16, script.Identity.Length));
    }

    [Fact]
    public async Task ScriptIdentityOverflowAndDisposedChildAreBounded()
    {
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, detailEvery: 1, intervalSeconds: 3600, maxEventsPerSecond: 100);
        try
        {
            var frame = PerformanceTelemetry.BeginFrame("session", "cpu", 1, 1, 1);
            for (int i = 0; i < 20; i++) using (PerformanceTelemetry.MeasureScript($"id{i}", "Evaluate")) { }
            var child = PerformanceTelemetry.MeasureScript("late", "Evaluate");
            frame.Dispose();
            child.Dispose();
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }
        PerformanceEvent detail = Assert.Single(lines.Select(Decode), e => e.Kind == "frame");
        Assert.Equal(16, detail.Scripts.Length);
        Assert.Equal(4, detail.ScriptOverflow);
        Assert.Equal(20, detail.ScriptCalls);
        Assert.Equal(1, detail.Errors);
    }

    [Fact]
    public async Task ShutdownIsBoundedWithBlockedSink()
    {
        using ManualResetEventSlim entered = new(), release = new(), finished = new();
        PerformanceTelemetry.Configure(_ => { entered.Set(); release.Wait(); finished.Set(); }, detailEvery: 1, maxEventsPerSecond: 10);
        try
        {
            using (var frame = PerformanceTelemetry.BeginFrame("session", "cpu", 1, 1, 1)) frame.SetOutcome("ok");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Stopwatch elapsed = Stopwatch.StartNew();
            await PerformanceTelemetry.ShutdownAsync();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); Assert.True(finished.Wait(TimeSpan.FromSeconds(3))); await PerformanceTelemetry.ShutdownAsync(); }
    }

    [Fact]
    public async Task FailedAndCancelledFramesCloseWithDistinctOutcomes()
    {
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, intervalSeconds: 3600, maxEventsPerSecond: 100);
        try
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var frame = PerformanceTelemetry.BeginFrame("session", "cpu", 1, 1, 1);
                using var phase = PerformanceTelemetry.Measure(PerformancePhase.Prepare);
                throw new InvalidOperationException("original");
            }));
            Assert.Throws<OperationCanceledException>((Action)(() =>
            {
                using var frame = PerformanceTelemetry.BeginFrame("session", "cpu", 2, 1, 1);
                try { throw new OperationCanceledException(); }
                catch (OperationCanceledException) { frame.SetOutcome("cancelled"); throw; }
            }));
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }
        Assert.Contains(lines, line => Decode(line) is { Kind: "summary", Frames: 2, Errors: 1, Cancelled: 1 });
    }

    [Fact]
    public async Task DetailOffEmitsSummaryOnly()
    {
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, detailEvery: 0, intervalSeconds: 3600, maxEventsPerSecond: 100);
        try
        {
            using PerformanceTelemetry.FrameScope frame = PerformanceTelemetry.BeginFrame("session", "gpu", 1, 1, 1);
            frame.SetOutcome("ok");
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }

        Assert.DoesNotContain(lines, line => line.Contains("\"Kind\":\"frame\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"Kind\":\"summary\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SummaryAndDetailCloseNestedScopesAndDrainTail()
    {
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, detailEvery: 1, intervalSeconds: 3600, maxEventsPerSecond: 100);
        try
        {
            using (PerformanceTelemetry.BeginRequest("request-secret"))
            using (PerformanceTelemetry.FrameScope frame = PerformanceTelemetry.BeginFrame("session-secret", "cpu", 7, 16, 9))
            {
                using (PerformanceTelemetry.Measure(PerformancePhase.Prepare))
                using (PerformanceTelemetry.Measure(PerformancePhase.Script))
                using (PerformanceTelemetry.MeasureScript("package-secret|revision-secret|instance-secret", "Evaluate"))
                {
                    PerformanceTelemetry.RecordReadback(true, 16 * 9 * 4);
                    await Task.Yield();
                }
                frame.SetOutcome("ok");
            }
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }

        string frameLine = Assert.Single(lines, line => line.Contains("\"Kind\":\"frame\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"Kind\":\"summary\"", StringComparison.Ordinal));
        PerformanceEvent frameEvent = JsonSerializer.Deserialize<PerformanceEvent>(frameLine[PerformanceTelemetry.Prefix.Length..])!;
        Assert.Equal(1, frameEvent.Frames);
        Assert.Equal(1, frameEvent.ScriptCalls);
        Assert.Equal(1, frameEvent.ImageReads);
        Assert.Equal(16 * 9 * 4, frameEvent.ImageBytes);
        Assert.Single(frameEvent.Scripts);
        Assert.Equal(frameEvent.WallMs, frameEvent.MaxWallMs);
        Assert.Equal(16, frameEvent.Request.Length);
        Assert.Equal(16, frameEvent.Session.Length);
        Assert.DoesNotContain("secret", frameLine, StringComparison.OrdinalIgnoreCase);
    }
}
