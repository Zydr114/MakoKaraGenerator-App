using System;
using System.Threading.Tasks;
using MakoKaraGenerator.Core.Media;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Pure fake-source lifetime tests with no filesystem or process access.</summary>
public sealed class MediaEnvelopeFileLeaseTests
{
    [Fact]
    /// <summary>Checks CallerDisposalDoesNotReleaseRealSourceWhileNativeHoldLives.</summary>
    public async Task CallerDisposalDoesNotReleaseRealSourceWhileNativeHoldLives()
    {
        var real = new Source();
        var wrapper = new MediaEnvelopeFileLease(real);
        IAsyncDisposable nativeHold = wrapper.AcquireHold();
        await wrapper.DisposeAsync();
        Assert.Equal(0, real.Releases);
        Assert.Throws<ObjectDisposedException>(() => wrapper.AcquireHold());
        await nativeHold.DisposeAsync();
        Assert.Equal(1, real.Releases);
        await nativeHold.DisposeAsync();
        await wrapper.DisposeAsync();
        Assert.Equal(1, real.Releases);
    }

    [Fact]
    /// <summary>Checks MultipleHoldsMustAllDrainBeforeTransferredSourceIsReleased.</summary>
    public async Task MultipleHoldsMustAllDrainBeforeTransferredSourceIsReleased()
    {
        var real = new Source();
        var wrapper = new MediaEnvelopeFileLease(real);
        IAsyncDisposable first = wrapper.AcquireHold(), quarantine = wrapper.AcquireHold();
        await first.DisposeAsync();
        await wrapper.DisposeAsync();
        Assert.Equal(0, real.Releases);
        // This hold represents the supervisor's failed-terminal retention.
        await quarantine.DisposeAsync();
        Assert.Equal(1, real.Releases);
    }

    private sealed class Source : IMediaFileLease
    {
        public MediaFileContent Content { get; } = new(new MediaDigest(new string('a', 64)), 7);
        public string TrustedLocalPath => "/private/not-opened";
        internal int Releases;
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
}
