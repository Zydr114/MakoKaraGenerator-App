using MakoKaraGenerator.Core.Media;
using Xunit;
using static MakoKaraGenerator.Media.Tests.MediaPresentationFixture;
using static MakoKaraGenerator.Media.Tests.MediaPresentationDecodePumpTests;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaPresentationWireTests
{
    [Fact]
    public async Task FramesRequireRealLoadedClosureAndExactManifestHashes()
    {
        var file = new MediaLoadedToolFile("/controlled/helper", new("helper", 1, Hash([1]))); var budget = new Budget();
        using (var noClosure = new MemoryStream())
        {
            Record(noClosure, 2, FrameBytes(Metadata(0, 0, 0, 0, Hash([1])))); noClosure.Position = 0;
            using var wire = new MediaPresentationWire([file], budget); await Assert.ThrowsAsync<MediaAbException>(() => wire.ReadNextAsync(noClosure, default).AsTask());
        }
        using (var changed = new MemoryStream())
        {
            Loaded(changed, new(file.TrustedPath, new("helper", 1, Hash([2])))); Record(changed, 5, []); changed.Position = 0;
            using var wire = new MediaPresentationWire([file], budget); await Assert.ThrowsAsync<MediaAbException>(() => wire.ReadNextAsync(changed, default).AsTask());
        }
        Assert.Equal(0, budget.Live);
    }
    [Fact]
    public async Task TruncationAndCancellationCannotProduceAFrame()
    {
        var file = new MediaLoadedToolFile("/controlled/helper", new("helper", 1, Hash([1]))); var budget = new Budget();
        using (var truncated = new MemoryStream([1, 2, 3]))
        using (var wire = new MediaPresentationWire([file], budget))
            await Assert.ThrowsAsync<MediaAbException>(() => wire.ReadNextAsync(truncated, default).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using (var wire = new MediaPresentationWire([file], budget))
        { using var empty = new MemoryStream(); OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wire.ReadNextAsync(empty, cancelled.Token).AsTask()); Assert.Equal(cancelled.Token, error.CancellationToken); }
        Assert.Equal(0, budget.Live);
    }
}
