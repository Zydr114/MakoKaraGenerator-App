using MakoKaraGenerator.Core.Media;
using Xunit;

namespace MakoKaraGenerator.Core.Tests.Media;

/// <summary>Independent frozen byte vectors and forged nested identity regressions.</summary>
public sealed class MediaAbIdentityTests
{
    /// <summary>Each expected hash comes from the independently encoded documented byte preimage.</summary>
    [Fact]
    public void KnownCompositeVectorsMatchStrictFieldOrder()
    {
        MediaFeatureFileSource still = MediaFoundationFixtures.Source();
        Assert.Equal("2567cfc898490c08f9c60e1058d8a9f954596f5f122cb40e719f0225660fc588", still.Presentation.Color.Digest.LowerHex);
        Assert.Equal("b238b2c15097384fdd01a09aa112c36960d681b13710664d20e8fdcb335a134f", still.Tool.Digest.LowerHex);
        Assert.Equal("943bb54376f97500fead16637f2c17d43d1f136f5a8ad724bd34d0eff77204b9", still.Profile.Digest.LowerHex);
        Assert.Equal("c71074ad0362323643fd15cf4f254ed247100e82389eee1ad7803bfb86505efc", still.Presentation.Digest.LowerHex);
        Assert.Equal("1b6be1803c5ad307675fcc0342dd6cf07db16801c01bc8b496b6ea860f83911b", still.Key.Digest.LowerHex);
        var videoDescriptor = MediaFoundationFixtures.Descriptor(MediaSourceKind.Video, rotation: MediaQuarterTurn.Clockwise90, mirror: true);
        MediaFeatureFileSource video = MediaFoundationFixtures.Source(MediaSourceKind.Video, videoDescriptor);
        Assert.Equal("ed6529be97deade0dfd0333551b806f601eb6e764f1d29b154373bbff7f4d693", videoDescriptor.Digest.LowerHex);
        Assert.Equal("de2fb4ae76f131e56bb28aa37602466f3f57e6446a25e811fd61939d606a5cb1", video.Key.Digest.LowerHex);
    }

    /// <summary>Digest self-fields cannot introduce cycles, but accepted boundaries recompute them.</summary>
    [Fact]
    public void SelfDigestIsExcludedAndForgedNestedDigestIsRefused()
    {
        MediaColorInterpretation original = MediaFoundationFixtures.Color();
        MediaColorInterpretation forged = MediaFoundationFixtures.Color(ownDigest: MediaFoundationFixtures.Hash('f'));
        Assert.Equal(MediaFoundationFixtures.Writer.ComputeColorDigest(original), MediaFoundationFixtures.Writer.ComputeColorDigest(forged));
        Assert.Equal(MediaAbFailure.ColorUnsupported, Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Validator.ValidateColor(forged)).Failure);
        var descriptor = MediaFoundationFixtures.Descriptor(color: forged);
        var source = MediaFoundationFixtures.Source(descriptor: descriptor);
        Assert.Equal(MediaAbFailure.ColorUnsupported, Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Validator.ValidateSource(source)).Failure);
    }

    /// <summary>Rehashing a bundle does not excuse a wrong decoder or color binding.</summary>
    [Fact]
    public void ValidLookingRehashedCrossBindingIsRefused()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source();
        var wrongDecoder = MediaFoundationFixtures.Profile(MediaFoundationFixtures.Hash('9'), source.Presentation.Color.Digest);
        var forged = MediaFoundationFixtures.Source(profile: wrongDecoder);
        Assert.Equal(MediaAbFailure.ProfileUnsupported, Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Validator.ValidateSource(forged)).Failure);
        var wrongColor = MediaFoundationFixtures.Profile(source.Tool.Digest, MediaFoundationFixtures.Hash('9'));
        Assert.Equal(MediaAbFailure.ColorUnsupported, Assert.Throws<MediaAbException>(() =>
            MediaFoundationFixtures.Validator.ValidateSource(MediaFoundationFixtures.Source(profile: wrongColor))).Failure);
    }

    /// <summary>Supplementary Unicode sorts by UTF-8 bytes rather than UTF-16 code units.</summary>
    [Fact]
    public void ToolManifestUsesUtf8OrderAndDomainSeparation()
    {
        MediaToolManifestEntry[] entries = [new("\U00010000", 2, MediaFoundationFixtures.Hash('2')),
            new("\uE000", 1, MediaFoundationFixtures.Hash('1'))];
        var writer = MediaFoundationFixtures.Writer;
        Assert.Equal("8bf3fa2f070ee5130334404bceb7f667239571aceee556cb5880cd924de376d8", writer.ComputeExecutableManifestDigest(entries).LowerHex);
        Assert.Equal("c6c0cd0829dde590038f35f8d0fb9da2d0c3b6d0cc26df67b15417acd134ab8e", writer.ComputeDependencyManifestDigest(entries).LowerHex);
        Assert.Equal(writer.ComputeExecutableManifestDigest(entries), writer.ComputeExecutableManifestDigest([entries[1], entries[0]]));
    }

    /// <summary>Invalid Unicode, duplicate logical IDs and filesystem paths are never normalized into identity.</summary>
    [Theory]
    [InlineData("bad\0id")]
    [InlineData("/host/tool")]
    [InlineData("C:\\tool")]
    [InlineData("../tool")]
    [InlineData("\uFEFFtool")]
    public void InvalidManifestIdentifiersAreRefused(string logicalId)
    {
        Assert.Equal(MediaAbFailure.ProfileUnsupported, Assert.Throws<MediaAbException>(() =>
            MediaFoundationFixtures.Writer.ComputeExecutableManifestDigest([new(logicalId, 1, MediaFoundationFixtures.Hash('0'))])).Failure);
    }

    /// <summary>Unpaired UTF-16 surrogates are supplied at runtime so attribute metadata cannot repair them.</summary>
    [Fact]
    public void UnpairedUtf16IsRefusedWithoutReplacement()
    {
        foreach (char invalid in new[] { '\ud800', '\udc00' })
        {
            string logicalId = new(invalid, 1);
            Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Writer.ComputeExecutableManifestDigest(
                [new(logicalId, 1, MediaFoundationFixtures.Hash('0'))]));
        }
    }

    /// <summary>The same logical ID cannot appear twice, and hash text never accepts upper-case or prefixes.</summary>
    [Fact]
    public void DuplicateAndMalformedDigestAreRefused()
    {
        var entry = new MediaToolManifestEntry("decoder", 1, MediaFoundationFixtures.Hash('0'));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Writer.ComputeDependencyManifestDigest([entry, entry]));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Writer.ComputeExecutableManifestDigest([new("decoder", 1, new(new string('A', 64)))]));
        Assert.Throws<MediaAbException>(() => MediaFoundationFixtures.Writer.ComputeExecutableManifestDigest([new("decoder", 1, new("sha256:" + new string('0', 64)))]));
    }

    /// <summary>Cancellation preserves its own exception identity before sorting or hashing.</summary>
    [Fact]
    public void CancelledManifestHashIsNeverTranslatedToMediaFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MediaFoundationFixtures.Writer.ComputeExecutableManifestDigest([], cancellation.Token));
    }

    /// <summary>An oversized advertised count is refused before allocating a snapshot or reading an entry.</summary>
    [Fact]
    public void ManifestCountIsAdmittedBeforeCopyingEntries()
    {
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() =>
            MediaFoundationFixtures.Writer.ComputeExecutableManifestDigest(new OversizedManifest())).Failure);
        MediaToolManifestEntry[] atLimit = Enumerable.Range(0, MediaAbCapacityV1.MaximumToolManifestEntries)
            .Select(index => new MediaToolManifestEntry("entry" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                1, MediaFoundationFixtures.Hash('0'))).ToArray();
        Assert.Equal(64, MediaFoundationFixtures.Writer.ComputeExecutableManifestDigest(atLimit).LowerHex.Length);
    }

    /// <summary>One identifier is limited by encoded bytes rather than UTF-16 character count.</summary>
    [Fact]
    public void LogicalIdentifierCapacityCountsStrictUtf8Bytes()
    {
        var atLimit = new MediaToolManifestEntry(new string('\u00e9', 128), 1, MediaFoundationFixtures.Hash('0'));
        Assert.Equal(64, MediaFoundationFixtures.Writer.ComputeDependencyManifestDigest([atLimit]).LowerHex.Length);
        var overLimit = new MediaToolManifestEntry(new string('\u00e9', 129), 1, MediaFoundationFixtures.Hash('0'));
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() =>
            MediaFoundationFixtures.Writer.ComputeDependencyManifestDigest([overLimit])).Failure);
        var tooManyCodeUnits = new MediaToolManifestEntry(new string('x', 257), 1, MediaFoundationFixtures.Hash('0'));
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() =>
            MediaFoundationFixtures.Writer.ComputeDependencyManifestDigest([tooManyCodeUnits])).Failure);
    }

    /// <summary>Many individually valid identifiers still share one manifest text ceiling.</summary>
    [Fact]
    public void ManifestTextCapacityIncludesAllIdentifiers()
    {
        MediaToolManifestEntry[] atLimit = Enumerable.Range(0, 256)
            .Select(index => new MediaToolManifestEntry(
                "entry-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 247),
                1, MediaFoundationFixtures.Hash('0'))).ToArray();
        Assert.Equal(64, MediaFoundationFixtures.Writer.ComputeDependencyManifestDigest(atLimit).LowerHex.Length);
        MediaToolManifestEntry[] overLimit = [.. atLimit, new("z", 1, MediaFoundationFixtures.Hash('0'))];
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() =>
            MediaFoundationFixtures.Writer.ComputeDependencyManifestDigest(overLimit)).Failure);
    }

    private sealed class OversizedManifest : IReadOnlyList<MediaToolManifestEntry>
    {
        /// <inheritdoc/>
        public int Count => int.MaxValue;
        /// <inheritdoc/>
        public MediaToolManifestEntry this[int index] => throw new InvalidOperationException("Entries must not be read.");
        /// <inheritdoc/>
        public IEnumerator<MediaToolManifestEntry> GetEnumerator() => throw new InvalidOperationException("Entries must not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
