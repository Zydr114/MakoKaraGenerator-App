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
    [InlineData("summary", 1, 0)]
    [InlineData("detailed", 1, 1)]
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
            await PerformanceTelemetry.ShutdownAsync();
            Assert.Equal(summaries, lines.Count(line => Decode(line).Kind == "summary"));
            Assert.Equal(details, lines.Count(line => Decode(line).Kind == "frame"));
        }
        finally
        {
            await PerformanceTelemetry.ShutdownAsync();
            Environment.SetEnvironmentVariable("MAKO_PERFORMANCE_MODE", previous);
            Environment.SetEnvironmentVariable("MAKO_PERFORMANCE_DETAIL_EVERY", previousSample);
        }
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
            await using var session = await VisualTestFixture.Timeline([effect]).CreateAsync(snapshot);
            await session.PrepareAsync(18_001, MakoKaraGenerator.Scripting.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
            frame.SetOutcome("ok");
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }
        PerformanceEvent detail = Assert.Single(lines.Select(Decode), e => e.Kind == "frame");
        Assert.Contains(detail.Scripts, script => script.Stage == "Initialize");
        Assert.Contains(detail.Scripts, script => script.Stage == "Evaluate");
        Assert.Contains(detail.Scripts, script => script.Stage == "AdvanceVisuals");
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
