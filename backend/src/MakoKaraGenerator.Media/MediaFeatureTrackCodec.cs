using System.Security.Cryptography;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>Canonical MFT1 streaming writer and complete validation gate.</summary>
/// <remarks>No method publishes a file, authorizes a source path, or releases C-owned leases.</remarks>
public sealed class MediaFeatureTrackCodec : IMediaFeatureTrackCodec
{
    private readonly IMediaMomentKernel _kernel;
    private readonly IMediaAbValueValidator _values;

    /// <summary>Creates a codec using the shared source validator and source-space numerical kernel.</summary>
    /// <param name="kernel">The canonical moment kernel; its definitions remain owned by the kernel slice.</param>
    /// <param name="values">The common accepted-boundary identity validator.</param>
    public MediaFeatureTrackCodec(IMediaMomentKernel kernel, IMediaAbValueValidator values)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(values);
        _kernel = kernel; _values = values;
    }

    /// <inheritdoc/>
    public IMediaFeatureTrackBuilder CreateBuilder(MediaFeatureFileSource source, IMediaTrackScratchReservation scratch,
        IMediaWorkingSetBudget builderBudget)
    {
        ArgumentNullException.ThrowIfNull(scratch); _values.ValidateSource(source);
        return new MediaFeatureTrackBuilder(source, scratch, builderBudget, _kernel);
    }

    /// <inheritdoc/>
    public async ValueTask<MediaArtifactIdentity> WriteCanonicalAsync(IMediaFeatureTrackWriteSource source, Stream target,
        IMediaWorkingSetBudget codecBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        MediaFeatureFileSource fileSource = source.Source;
        PresentationIndexSummary presentation = source.Presentation;
        _values.ValidateSource(fileSource);
        Mft1Wire.ValidateSummary(fileSource, presentation);
        Mft1Wire.CheckBudget(codecBudget, MediaAbCapacityV1.MaximumBuilderWorkingBytes);
        Mft1Wire.Require(target.CanWrite && (!target.CanSeek || (target.Length == 0 && target.Position == 0)),
            "Canonical target must be an empty writable stream at its origin.");
        // Nonseekable emptiness is a caller precondition. No extra seekability restriction is introduced.
        IMediaMemoryReservation reservation = codecBudget.Reserve(MediaTrackLayoutV1.HeaderBytes + 256,
            MediaWorkingSetPurpose.CodecValidation);
        bool completed = false;
        try
        {
            byte[] header = new byte[MediaTrackLayoutV1.HeaderBytes];
            Mft1Layout layout = Mft1Layout.Create(fileSource.Kind, presentation.FrameCount);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var indexHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var output = new Mft1HashWriteStream(target, hash);
            Mft1Wire.EncodeHeader(header, fileSource, presentation, layout);
            output.BeginSection(header.Length);
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false); output.EndSection();
            output.BeginSection(layout.IntervalBytes, indexHash);
            await source.CopyIntervalsToAsync(output, cancellationToken).ConfigureAwait(false); output.EndSection();
            Mft1Wire.Require(Mft1Wire.Digest(indexHash.GetHashAndReset()) == presentation.IndexDigest,
                "Written interval section differs from its declared index hash.");
            output.BeginSection(layout.GridBytes);
            await source.CopyGridsToAsync(output, cancellationToken).ConfigureAwait(false); output.EndSection();
            output.BeginSection(layout.PrefixBytes);
            await source.CopyPrefixesToAsync(output, cancellationToken).ConfigureAwait(false); output.EndSection();
            cancellationToken.ThrowIfCancellationRequested();
            Mft1Wire.Require(output.Count == layout.Length, "Written artifact length differs from its exact canonical layout.");
            if (target.CanSeek) Mft1Wire.CheckLength(target, layout.Length);
            var artifact = new MediaArtifactIdentity(Mft1Wire.Digest(hash.GetHashAndReset()), layout.Length, MediaProfilesV1.Codec,
                fileSource.Key, fileSource.Profile.Digest, fileSource.Presentation.Digest,
                presentation.IndexDigest, presentation.FrameCount, presentation.VideoDurationUs);
            _values.ValidateArtifactIdentity(fileSource, artifact);
            completed = true;
            return artifact;
        }
        finally
        {
            if (completed) reservation.Dispose(); else Mft1Wire.DisposeAfterFailure(reservation);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IMediaFeatureTrackReader> OpenValidatedAsync(Stream seekableSource, MediaFeatureFileSource source,
        MediaArtifactIdentity expected, IMediaWorkingSetBudget readerBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seekableSource); cancellationToken.ThrowIfCancellationRequested();
        _values.ValidateArtifactIdentity(source, expected);
        Mft1Wire.CheckBudget(readerBudget, MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
        Mft1Wire.Require(seekableSource.CanRead && seekableSource.CanSeek && !seekableSource.CanWrite,
            "Artifact source must be immutable readable seekable storage.");
        Mft1Wire.CheckLength(seekableSource, expected.ByteLength);
        // Admission precedes all arrays; this same charge and backing transfer to the reader only on success.
        IMediaMemoryReservation reservation = readerBudget.Reserve(MediaFeatureTrackReader.WorkingBytes,
            MediaWorkingSetPurpose.ReaderCache);
        Mft1ReaderBacking? backing = null;
        try
        {
            backing = new Mft1ReaderBacking();
            Mft1Layout layout = Mft1Layout.Create(source.Kind, expected.FrameCount);
            await Mft1Wire.ReadAtAsync(seekableSource, 0, backing.Copy.AsMemory(0, MediaTrackLayoutV1.HeaderBytes), cancellationToken).ConfigureAwait(false);
            Mft1Wire.ValidateHeader(backing.Copy.AsSpan(0, MediaTrackLayoutV1.HeaderBytes), source, expected, layout);
            await ValidateFullHashAsync(seekableSource, expected, backing.Copy, cancellationToken).ConfigureAwait(false);
            await ValidateRecordsAsync(seekableSource, source, expected, layout, backing, cancellationToken).ConfigureAwait(false);
            Mft1Wire.CheckLength(seekableSource, expected.ByteLength); cancellationToken.ThrowIfCancellationRequested();
            return new MediaFeatureTrackReader(seekableSource, source.Kind, expected, layout, backing, reservation);
        }
        catch
        {
            backing?.Release(); Mft1Wire.DisposeAfterFailure(reservation); // Input ownership has not transferred.
            throw;
        }
    }

    private static async ValueTask ValidateFullHashAsync(Stream input, MediaArtifactIdentity expected, byte[] buffer, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        input.Position = 0; long remaining = expected.ByteLength;
        while (remaining > 0)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
            int read = await input.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (read == 0) throw Mft1Wire.Invalid("Artifact ended before its complete external identity length.");
            hash.AppendData(buffer, 0, read); remaining -= read;
        }
        Mft1Wire.CheckLength(input, expected.ByteLength);
        Mft1Wire.Require(Mft1Wire.Digest(hash.GetHashAndReset()) == expected.ContentHash,
            "Complete artifact hash does not match its external identity.");
    }

    private static async ValueTask ValidateRecordsAsync(Stream input, MediaFeatureFileSource source, MediaArtifactIdentity expected,
        Mft1Layout layout, Mft1ReaderBacking buffers, CancellationToken token)
    {
        MediaSourceKind kind = source.Kind;
        var validator = new Mft1IntervalValidator(source, MediaAbFailure.ArtifactInvalid);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (kind == MediaSourceKind.Video)
        {
            await Mft1Wire.ReadAtAsync(input, layout.PrefixOffset, buffers.PrefixA, token).ConfigureAwait(false);
            Mft1Wire.ValidatePrefix(buffers.PrefixA, 0, 0, buffers.Sums);
        }
        long prefixOrdinal = 1;
        for (long frame = 0; frame < layout.FrameCount; frame++)
        {
            await Mft1Wire.ReadAtAsync(input, MediaTrackLayoutV1.HeaderBytes + frame * MediaTrackLayoutV1.IntervalBytes,
                buffers.Interval, token).ConfigureAwait(false);
            hash.AppendData(buffers.Interval);
            PresentationInterval interval = Mft1Wire.DecodeInterval(buffers.Interval, kind, frame);
            validator.Accept(interval);
            await Mft1Wire.ReadAtAsync(input, layout.GridOffset + frame * MediaTrackLayoutV1.FrameGridBytes,
                buffers.Grid, token).ConfigureAwait(false);
            Mft1Wire.ValidateGrid(buffers.Grid);
            if (interval is PresentationInterval.Timed timed)
            {
                Mft1Wire.AddGrid(buffers.Grid, buffers.Sums, checked((ulong)(timed.EndUs - timed.StartUs)));
                if ((frame + 1) % MediaTrackLayoutV1.BlockFrames == 0 || frame + 1 == layout.FrameCount)
                {
                    await Mft1Wire.ReadAtAsync(input, layout.PrefixOffset + prefixOrdinal * MediaTrackLayoutV1.PrefixEntryBytes,
                        buffers.PrefixA, token).ConfigureAwait(false);
                    Mft1Wire.ValidatePrefix(buffers.PrefixA, frame + 1, timed.EndUs, buffers.Sums); prefixOrdinal++;
                }
            }
        }
        validator.Complete(expected.FrameCount, expected.VideoDurationUs);
        Mft1Wire.Require(kind == MediaSourceKind.Still || prefixOrdinal == layout.PrefixCount,
            "Recomputed prefix count differs from the exact layout.");
        Mft1Wire.Require(Mft1Wire.Digest(hash.GetHashAndReset()) == expected.PresentationIndexDigest,
            "Presentation index bytes do not match their external digest.");
        Array.Clear(buffers.Sums);
    }
}
