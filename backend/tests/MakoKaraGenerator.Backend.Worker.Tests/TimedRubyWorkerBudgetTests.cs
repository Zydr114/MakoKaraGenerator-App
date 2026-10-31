using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class TimedRubyWorkerBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 注音片段定义与文本均纳入测量预算且超限无截断拒绝(bool text)
    {
        var segments = text ? new[] { RubyTextSegment.Create("large", new string('a', 200_000)) }
            : Enumerable.Range(0, 4096).Select(index => RubyTextSegment.Create("part-" + index, "a")).ToArray();
        byte[] source = StaticProjectSnapshotCodec.Encode(TimedRubyFixture.Create([.. segments]));
        var input = new WorkerSceneMeasurementInput(new("0", "time", "200000", 320, 180, "transparent"),
            WorkerProtocol.ComputeSha256(source), string.Empty);
        input = input with { SampleInputFingerprint = WorkerSceneMeasurementContract.Fingerprint(source, input) };
        byte[] payload = WorkerSceneMeasurementContract.Encode(input, source);
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => SceneMeasurementWorker.ExecuteAsync(payload, CancellationToken.None));
        Assert.Equal("SceneMeasurementBudgetExceeded", failure.Code);
    }
}
