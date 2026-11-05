using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Abstractions;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

/// <summary>Actual builder, codec, budgets and filesystem setup; no fake successful codec or opaque byte-array store.</summary>
internal static class MediaFeatureStoreFixture
{
    private static readonly float[] HalfWhitePixel = [0.5f, 0.5f, 0.5f, 0.5f];
    internal const string Owner = "client-store-owner";
    internal static string Job() => "job-" + Guid.NewGuid().ToString("N");
    internal static MediaWorkingSetBudget BuilderBudget() => new(MediaAbCapacityV1.MaximumBuilderWorkingBytes);
    internal static MediaWorkingSetBudget ReaderBudget() => new(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
    internal static MediaFeatureTrackCodec Codec() => new(new MediaMomentKernel(), new MediaAbValueValidator());

    internal static MediaFeatureFileSource Source(MediaSourceKind kind = MediaSourceKind.Still)
    {
        var writer = new MediaAbIdentityWriter();
        var zero = new MediaDigest(new string('0', 64));
        var colorRaw = new MediaColorInterpretation(MediaTransfer.Srgb, MediaMatrix.Rgb, MediaRange.Full,
            MediaAlpha.Straight, MediaChromaLocation.None, MediaNativePixelFormat.Rgba32, false, zero, default);
        var color = new MediaColorInterpretation(colorRaw.Transfer, colorRaw.Matrix, colorRaw.Range, colorRaw.Alpha,
            colorRaw.ChromaLocation, colorRaw.NativePixelFormat, false, zero, writer.ComputeColorDigest(colorRaw));
        var toolRaw = new MediaToolIdentity(zero, zero, zero, default);
        var tool = new MediaToolIdentity(zero, zero, zero, writer.ComputeToolDigest(toolRaw));
        var descriptorRaw = new MediaFilePresentationDescriptor(0, 1, 1, 1, 1,
            kind == MediaSourceKind.Video ? new RationalTimeBase(1, 1_000_000) : null,
            1, 1, MediaQuarterTurn.None, false, color, default);
        var descriptor = new MediaFilePresentationDescriptor(0, 1, 1, 1, 1, descriptorRaw.StreamTimeBase,
            1, 1, MediaQuarterTurn.None, false, color, writer.ComputeDescriptorDigest(descriptorRaw));
        var rawProfile = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline,
            MediaProfilesV1.Color, MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec,
            MediaProfilesV1.Query, MediaProfilesV1.Gpu, tool.Digest, color.Digest, default);
        var profile = new MediaProfileBundle(rawProfile.Presentation, rawProfile.DecoderPipeline, rawProfile.Color,
            rawProfile.Statistics, rawProfile.Quantization, rawProfile.Codec, rawProfile.Query, rawProfile.Gpu,
            tool.Digest, color.Digest, writer.ComputeProfileDigest(rawProfile));
        var rawSource = new MediaFeatureFileSource(default, kind, new MediaFileContent(zero, 1), descriptor, tool, profile);
        return new(writer.ComputeSourceKey(rawSource), kind, rawSource.Content, descriptor, tool, profile);
    }

    internal static async ValueTask<BackendMediaFeatureArtifactStore.Prepared> PrepareAsync(BackendMediaFeatureArtifactStore store,
        MediaFeatureFileSource? source = null, string? job = null,
        Action<BackendMediaFeatureArtifactStore.Prepared>? beforeProducerDrain = null, CancellationToken cancellationToken = default)
    {
        source ??= Source(); job ??= Job(); MediaFeatureTrackCodec codec = Codec();
        BackendMediaFeatureArtifactStore.Reservation reservation = store.TryReserve(Owner, job, source, 1) ??
            throw new InvalidOperationException("Test fixture could not reserve storage.");
        try
        {
            using IDisposable producer = reservation.AcquireProducer();
            MediaWorkingSetBudget builderBudget = BuilderBudget();
            await using IMediaFeatureTrackBuilder builder = codec.CreateBuilder(source, reservation, builderBudget);
            RationalTimestamp? rawPts = source.Kind == MediaSourceKind.Video ? new RationalTimestamp(-1000, new(1, 1_000_000)) : null;
            await builder.OnFrameAsync(new(source.Key, source.Profile.Digest,
                new(new(0), rawPts, 1, 1, source.Presentation.Color.Digest), HalfWhitePixel), CancellationToken.None);
            PresentationInterval interval = source.Kind == MediaSourceKind.Still ? new PresentationInterval.Timeless(new(0)) :
                new PresentationInterval.Timed(new(0), rawPts!.Value, new(1000, new(1, 1_000_000)), 0, 2000, PresentationEndAuthority.StreamEndpoint);
            await builder.OnIntervalAsync(interval, CancellationToken.None);
            var summary = new PresentationIndexSummary(source.Key, source.Profile.Digest, source.Presentation.Digest,
                IntervalDigest(source.Kind), 1, source.Kind == MediaSourceKind.Still ? null : 2000);
            await using IMediaFeatureTrackWriteSource writeSource = await builder.CompleteAsync(summary, CancellationToken.None);
            BackendMediaFeatureArtifactStore.Prepared prepared = await store.PrepareAsync(reservation, writeSource, codec,
                BuilderBudget(), ReaderBudget(), cancellationToken);
            beforeProducerDrain?.Invoke(prepared);
            return prepared;
        }
        catch (Exception primary)
        {
            reservation.RecordFailure(primary);
            try { await reservation.DisposeAsync(); }
            catch (Exception cleanup) { primary.Data["MediaFixtureCleanupFailure"] = cleanup; }
            throw;
        }
    }

    private static MediaDigest IntervalDigest(MediaSourceKind kind)
    {
        Span<byte> record = stackalloc byte[64]; record.Clear();
        if (kind == MediaSourceKind.Still) BinaryPrimitives.WriteUInt32LittleEndian(record[48..], 4);
        else
        {
            BinaryPrimitives.WriteInt64LittleEndian(record, -1000); BinaryPrimitives.WriteInt32LittleEndian(record[8..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], 1_000_000); BinaryPrimitives.WriteInt64LittleEndian(record[16..], 1000);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], 1); BinaryPrimitives.WriteInt32LittleEndian(record[28..], 1_000_000);
            BinaryPrimitives.WriteUInt64LittleEndian(record[40..], 2000); BinaryPrimitives.WriteUInt32LittleEndian(record[48..], 3);
        }
        return new(Convert.ToHexStringLower(SHA256.HashData(record)));
    }
}
