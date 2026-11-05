using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using Xunit;
using static MakoKaraGenerator.Media.Tests.MediaPresentationFixture;
using static MakoKaraGenerator.Media.Tests.MediaPresentationDecodePumpTests;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaPresentationFrameBufferAllocationTests
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types",
        Justification = "Injects allocation failure at each backing array without exhausting the test process heap.")]
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EachBackingAllocationFailureRollsBackRealManagedChargeAndAllowsSourceRelease(int failAt)
    {
        var sourceHold = new CountingSource(); var operation = new DecodeOperationBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes,
            1024 * 1024, [1024 * 1024], 2, sourceHold); var managed = new MediaPresentationManagedBudget(operation);
        MediaDigest hash = Hash([1]); MediaLoadedToolFile gate = new("/controlled/gate", new("gate", 1, hash)), helper = new("/controlled/helper", new("helper", 1, hash));
        var deployment = new MediaPresentationDeployment(gate.TrustedPath, helper.TrustedPath, [gate, helper], []);
        MediaNativePresentationMetadata frame = Metadata(0, 0, 0, 0, hash);
        MediaFeatureFileSource source = MediaPresentationInterpretation.Create(new(hash, 1), new(MediaSourceKind.Video, 0, null), frame, null, deployment.Tool);
        int allocations = 0; var expected = new OutOfMemoryException("controlled allocation failure without real heap pressure");
        void BeforeAllocation() { Assert.True(operation.LiveManagedBytes > 0); if (++allocations == failAt) throw expected; }
        OutOfMemoryException actual = Assert.Throws<OutOfMemoryException>(() => MediaPresentationDecodePump.FrameBuffers.Create(frame, source, managed,
            bytes => { BeforeAllocation(); return new byte[bytes]; },
            floats => { BeforeAllocation(); return new float[floats]; }));
        Assert.Same(expected, actual); Assert.Equal(failAt, allocations); Assert.Equal(0, operation.LiveManagedBytes);
        Assert.Equal(0, operation.ActiveBorrows); await operation.ReleaseSourceAsync(); Assert.Equal(1, sourceHold.Releases);
    }

    private sealed class CountingSource : IAsyncDisposable
    {
        internal int Releases;
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
}
