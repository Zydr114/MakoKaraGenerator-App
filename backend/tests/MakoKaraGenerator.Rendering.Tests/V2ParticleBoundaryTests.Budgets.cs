using System.Text.Json;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class V2ParticleBoundaryTests
{
    [Theory]
    [InlineData("wall")]
    [InlineData("memory")]
    [InlineData("cancel")]
    public async Task 真实锚点补算中途预算或取消失败不发布半份memo且同会话能够恢复(string kind)
    {
        var package = await ReplacePackageSource(await LoadPackage("CursorTrail"), "src/CursorTrail.cs", BudgetEmitterSource);
        var snapshot = ProjectSnapshot.FromProject(SamplingProject() with { Effects = new([package], [Instance(package, "budget-owner")]) });
        var compiled = await Compile(snapshot);
        MillisecondClock clock = new();
        var options = new V2RuntimeBudgetOptions(frameBytes: kind == "memory" ? 30_000 : ParticleLimits.MaximumFrameBytes,
            timeProvider: clock);
        using CancellationTokenSource cancellation = new();
        var geometry = new BudgetGeometry();
        var factory = new FrameSessionFactory(compiled.Effects, snapshot.EffectInputsFingerprint!, () => geometry,
            null, null, v2Budgets: options);
        await using var session = await factory.CreateAsync(snapshot, CancellationToken.None);
        geometry.Calls.Clear();
        geometry.OnFrame = timeUs =>
        {
            if (timeUs != 10_000) return;
            if (kind == "wall") clock.Advance(3001);
            if (kind == "cancel") cancellation.Cancel();
        };
        if (kind == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await session.PrepareAsync(30_000,
                OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await Prepare(session, snapshot, 30_000));
            string expected = kind == "wall" ? "FinalAnchorWallBudgetExceeded" : "ParticleFrameMemoryExceeded";
            Assert.True(error.Code == expected, error.ToString());
            Assert.Equal("budget-owner", error.InstanceId.Value);
            Assert.Equal(package.PackageId, error.PackageIdentity!.PackageId);
            Assert.Equal(package.Revision, error.PackageIdentity.Revision);
            Assert.Equal(kind == "wall" ? "ResolveFinalAnchors" : "ReadParticleBirths", error.Stage);
        }
        // Both real birth-time scenes were reached, so cancellation was not
        // requested before entry and the memory failure was not a count gate.
        Assert.Contains(5_000, geometry.Calls); Assert.Contains(10_000, geometry.Calls);
        Assert.True(geometry.Calls.IndexOf(5_000) < geometry.Calls.IndexOf(10_000));
        Assert.DoesNotContain(15_000, geometry.Calls);
        geometry.OnFrame = null;
        long recoveryUs = kind == "memory" ? 5_000 : 30_000;
        var recovered = await Prepare(session, snapshot, recoveryUs);
        int expectedBirths = kind == "memory" ? 1 : 6;
        Assert.Equal(expectedBirths, recovered.V2Particles.Length);
        Assert.Equal(expectedBirths, recovered.V2AnchorStatistics.MemoMisses);
        Assert.Equal(expectedBirths, recovered.V2AnchorStatistics.UniqueTimes);
        Assert.Equal(expectedBirths, recovered.V2AnchorStatistics.TextCalls);
        Assert.Equal(0, recovered.V2AnchorStatistics.MemoHits);
        var freshFactory = new FrameSessionFactory(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline(),
            null, null, v2Budgets: options);
        await using var fresh = await freshFactory.CreateAsync(snapshot, CancellationToken.None);
        var direct = await Prepare(fresh, snapshot, recoveryUs);
        Assert.Equal(direct.V2AnchorStatistics, recovered.V2AnchorStatistics);
        Assert.Equal(ParticleSignature(direct), ParticleSignature(recovered));
        Assert.Equal(Pixels(snapshot, direct), Pixels(snapshot, recovered));
        if (kind == "memory")
        {
            // A failed request cannot silently increase its immutable byte cap.
            var repeated = await Assert.ThrowsAsync<EffectFailureException>(async () => await Prepare(session, snapshot, 30_000));
            Assert.Equal("ParticleFrameMemoryExceeded", repeated.Code);
            Assert.Equal("budget-owner", repeated.InstanceId.Value);
            var recoveredAgain = await Prepare(session, snapshot, 5_000);
            Assert.Equal(1, recoveredAgain.V2AnchorStatistics.MemoMisses);
            Assert.Equal(1, recoveredAgain.V2AnchorStatistics.TextCalls);
            Assert.Equal(ParticleSignature(direct), ParticleSignature(recoveredAgain));
        }
    }

    [Fact]
    public void 内部限额只允许收紧且单调计时仅累计补算作用域并保留三秒整数边界()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new V2RuntimeBudgetOptions(frameBytes: ParticleLimits.MaximumFrameBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new V2RuntimeBudgetOptions(anchorTimes: ParticleLimits.MaximumAnchorTimes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new V2RuntimeBudgetOptions(textCalls: ParticleLimits.MaximumTextCalls + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new V2RuntimeBudgetOptions(futureEndpoints: ParticleLimits.MaximumFutureEndpoints + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new V2RuntimeBudgetOptions(wallMilliseconds: ParticleLimits.MaximumAnchorWallMilliseconds + 1));
        MillisecondClock clock = new();
        var budget = new V2RuntimeBudgetOptions(timeProvider: clock).CreateAnchorQuery(CancellationToken.None);
        clock.Advance(50_000); budget.Check();
        using (budget.Measure()) { clock.Advance(1000); budget.Check(); }
        clock.Advance(50_000); budget.Check();
        using (budget.Measure())
        {
            clock.Advance(1000);
            using (budget.Measure()) { clock.Advance(1000); budget.Check(); }
            budget.Check();
            clock.Advance(1);
            Assert.Equal("FinalAnchorWallBudgetExceeded", Assert.Throws<EffectOutputException>(budget.Check).Code);
        }
    }

    private const string BudgetEmitterSource = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record BudgetData : PreparedEffect;
        public sealed class CursorTrail : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => new BudgetData
            {
                Particles = new([new(new("cursor"), new([new(new(0, 100000), 200, 200)], [new(0, 100000)]),
                    new ParticleSpec { LifetimeUs = 200000, SizePx = 8, Color = new(1, 0, 0, 1), SeedStreamKey = "budget" },
                    CursorAnchorMode.AdvanceCenter, BridgePolicy.Jump)], []),
            };
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
        }
        """;

    private static string ParticleSignature(PreparedRenderFrame frame) => JsonSerializer.Serialize(new
    {
        frame.V2ParticleSnapshots,
        Batches = frame.OverlayBatches.Select(batch => JsonSerializer.Serialize(batch, batch.GetType())).ToArray(),
    });

    // Timestamp units are explicitly milliseconds, with positive fixed frequency
    // and a strictly nondecreasing value; no UTC clock or sleeping is involved.
    private sealed class MillisecondClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(long milliseconds)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
            _milliseconds = checked(_milliseconds + milliseconds);
        }
    }

    private sealed class BudgetGeometry : ILineGeometryProvider, IDisposable
    {
        private readonly SkiaRenderPipeline _pipeline = new();
        public List<long> Calls { get; } = [];
        public Action<long>? OnFrame { get; set; }
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        {
            var frame = _pipeline.GetSceneFrame(snapshot, timeUs, editingLineId);
            Calls.Add(timeUs); OnFrame?.Invoke(timeUs); return frame;
        }
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => _pipeline.GetLineGeometry(snapshot, lineId);
        public void Dispose() => _pipeline.Dispose();
    }
}
