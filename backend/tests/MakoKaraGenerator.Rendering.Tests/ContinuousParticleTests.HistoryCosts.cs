using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ContinuousParticleTests
{
    private static readonly JsonSerializerOptions HistoryCostJsonOptions = new() { WriteIndented = true };
    private sealed record HistoryCost(string Phase, long AllocatedBytes, double Milliseconds, int IndependentBackends);

    [Fact]
    public async Task 历史共享后过去未来独立端点与桥接首次查询缓存命中分开计量()
    {
        var snapshot = Snapshot(twoRows: true);
        ImmutableArray<CompiledFrameEffect> effects = [await BridgeState(), await Effect(entry: "BridgeOnly", bridgeUs: 45001)];
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var edge = TimelineProjection.Create(snapshot, Fx.ReplayReason.Initial).CursorTransitions.Single(item => item.GapUs > 0);
        List<HistoryCost> results = [];
        for (int repeat = -1; repeat < 3; repeat++)
        {
            int created = 0;
            ILineGeometryProvider Geometry() { created++; return new SkiaRenderPipeline(); }
            SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, Geometry);
            async Task<Fx.CursorEndpoint> Endpoint(Fx.CursorIdentity identity, Fx.CursorEndpointKind kind, string phase)
            {
                long allocated = GC.GetTotalAllocatedBytes(precise: true); int backends = created; var watch = Stopwatch.StartNew();
                var endpoint = await query.ResolveAsync(identity, kind, Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target);
                watch.Stop();
                if (repeat >= 0) results.Add(new(phase, GC.GetTotalAllocatedBytes(precise: true) - allocated, watch.Elapsed.TotalMilliseconds, created - backends));
                Assert.True(endpoint.IsAvailable); Assert.Equal(1, created - backends);
                Assert.StartsWith(kind == Fx.CursorEndpointKind.NextStart ? "3/" : "2/",
                    endpoint.Diagnostics.Last(item => item.Diagnostic.Code == "BridgeStateCount").Diagnostic.Message, StringComparison.Ordinal);
                return endpoint;
            }
            var past = await Endpoint(edge.From, Fx.CursorEndpointKind.PreviousEndLimit, "past-independent-replay");
            var future = await Endpoint(edge.To, Fx.CursorEndpointKind.NextStart, "future-independent-replay");
            Assert.Equal(399999, past.TimeUs); Assert.Equal(500000, future.TimeUs);
            TimelineFrameSourceFactory factory = new(effects, Fingerprint, Geometry, Fx.ReplayReason.Initial);
            await using var main = await factory.CreateAsync(snapshot);
            await Frame(main, snapshot, 441666);
            async Task<PreparedRenderFrame> Advance(long time, string phase, int expectedBackends)
            {
                long allocated = GC.GetTotalAllocatedBytes(precise: true); int backends = created; var watch = Stopwatch.StartNew();
                var frame = await Frame(main, snapshot, time); watch.Stop();
                if (repeat >= 0) results.Add(new(phase, GC.GetTotalAllocatedBytes(precise: true) - allocated, watch.Elapsed.TotalMilliseconds, created - backends));
                Assert.Equal(expectedBackends, created - backends);
                Assert.StartsWith("2/", frame.Diagnostics.Last(item => item.Diagnostic.Code == "BridgeStateCount").Diagnostic.Message, StringComparison.Ordinal);
                return frame;
            }
            await Advance(450000, "bridge-first-new-boundary", 2);
            await Advance(458333, "bridge-cache-new-boundary", 0);
            var bridge = await Frame(main, snapshot, 499999);
            Assert.All(bridge.Particles, particle => Assert.IsType<Fx.ParticleSource.Bridge>(particle.Source));
            Assert.Equal(2, bridge.Particles.Length);
        }
        string? output = Environment.GetEnvironmentVariable("MAKO_HISTORY_COST_OUTPUT");
        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                productVersion = ProductVersion.Current, runtime = Environment.Version.ToString(), results,
                note = "One complete fixture warmup excluded. Three sequential samples in one isolated test process; script compile and main prefill excluded. Bridge measures one new boundary, including endpoint replay for first use. No time budget assertion.",
            }, HistoryCostJsonOptions));
        }
    }
}
