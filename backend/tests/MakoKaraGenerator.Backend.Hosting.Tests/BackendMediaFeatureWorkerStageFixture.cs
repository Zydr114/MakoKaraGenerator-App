using System.Buffers.Binary;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

/// <summary>Real file, builder and codec fixture; local file writing does not simulate process drain.</summary>
internal sealed class BackendMediaFeatureWorkerStageFixture : IAsyncDisposable
{
    private BackendMediaFeatureArtifactStore.WorkerStageWriter? _writer;
    internal BackendMediaFeatureWorkerStageFixture(BackendMediaFeatureArtifactStore store, MediaFeatureFileSource? source = null,
        long maximumFrames = 1)
    {
        Store = store; source ??= MediaFeatureStoreFixture.Source();
        Reservation = store.TryReserve(MediaFeatureStoreFixture.Owner, MediaFeatureStoreFixture.Job(), source, maximumFrames)!;
        Scope = Reservation.BeginWorkerWrite();
    }
    internal BackendMediaFeatureArtifactStore Store { get; }
    internal BackendMediaFeatureArtifactStore.Reservation Reservation { get; }
    internal BackendMediaFeatureArtifactStore.WorkerWriteScope Scope { get; }
    internal BackendMediaFeatureArtifactStore.WorkerStageGrant Grant => Scope.Grant;
    internal BackendMediaFeatureArtifactStore.WorkerStageWriter Writer => _writer ??= Grant.OpenLocalWriter();
    internal string Candidate => Path.Combine(Grant.StageDirectory, "candidate.mft");

    internal async ValueTask<MediaArtifactIdentity> WriteCandidateAsync(int frameCount = 1)
    {
        var codec = MediaFeatureStoreFixture.Codec(); MediaFeatureFileSource source = Reservation.Source;
        if (frameCount <= 0 || frameCount > Reservation.MaximumFrames || (source.Kind == MediaSourceKind.Still && frameCount != 1))
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        var builderBudget = MediaFeatureStoreFixture.BuilderBudget();
        await using (IMediaFeatureTrackBuilder builder = codec.CreateBuilder(source, Writer, builderBudget))
        {
            for (int frame = 0; frame < frameCount; frame++)
            {
                long startUs = checked(frame * 2000L), endUs = checked((frame + 1L) * 2000L);
                RationalTimestamp? raw = source.Kind == MediaSourceKind.Video ? new RationalTimestamp(startUs - 1000, new(1, 1_000_000)) : null;
                float level = frame % 2 == 0 ? 0.5f : 0.25f;
                await builder.OnFrameAsync(new(source.Key, source.Profile.Digest,
                    new(new(frame), raw, 1, 1, source.Presentation.Color.Digest), new float[] { level, level, level, 0.5f }), CancellationToken.None);
                PresentationInterval interval = source.Kind == MediaSourceKind.Still ? new PresentationInterval.Timeless(new(frame)) :
                    new PresentationInterval.Timed(new(frame), raw!.Value, new(endUs - 1000, new(1, 1_000_000)), startUs, endUs,
                        frame == frameCount - 1 ? PresentationEndAuthority.StreamEndpoint : PresentationEndAuthority.NextPts);
                await builder.OnIntervalAsync(interval, CancellationToken.None);
            }
            var summary = new PresentationIndexSummary(source.Key, source.Profile.Digest, source.Presentation.Digest,
                IntervalDigest(source.Kind, frameCount), frameCount, source.Kind == MediaSourceKind.Still ? null : checked(frameCount * 2000L));
            await using IMediaFeatureTrackWriteSource sourceBytes = await builder.CompleteAsync(summary, CancellationToken.None);
            await using BackendMediaFeatureBoundedStream candidate = Writer.CreateCandidate();
            MediaArtifactIdentity identity = await codec.WriteCanonicalAsync(sourceBytes, candidate,
                MediaFeatureStoreFixture.BuilderBudget(), CancellationToken.None);
            await candidate.FlushAsync(CancellationToken.None); candidate.FlushToDisk();
            return identity;
        }
        // The caller separately closes Writer, then attests actual executor drain. No payload bool exists.
    }

    internal async ValueTask<MediaArtifactIdentity> WriteAndDrainFilesAsync(int frameCount = 1)
    {
        MediaArtifactIdentity identity = await WriteCandidateAsync(frameCount);
        await Writer.DisposeAsync();
        return identity;
    }

    internal ValueTask<BackendMediaFeatureArtifactStore.Prepared> ImportAsync(MediaArtifactIdentity identity,
        CancellationToken cancellationToken = default) => Store.PrepareWorkerCandidateAsync(Reservation, Scope, identity,
            MediaFeatureStoreFixture.Codec(), MediaFeatureStoreFixture.ReaderBudget(), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) await _writer.DisposeAsync();
        Scope.AttestExecutorDrained();
        await Reservation.DisposeAsync();
    }

    internal static MediaArtifactIdentity WithHash(MediaArtifactIdentity identity, byte[] bytes) => new(
        new(Convert.ToHexStringLower(SHA256.HashData(bytes))), identity.ByteLength, identity.Format,
        identity.SourceKey, identity.ProfileDigest, identity.SourceDescriptorDigest,
        identity.PresentationIndexDigest, identity.FrameCount, identity.VideoDurationUs);

    private static MediaDigest IntervalDigest(MediaSourceKind kind, int frameCount)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[64];
        for (int frame = 0; frame < frameCount; frame++)
        {
            record.Clear();
            if (kind == MediaSourceKind.Still) BinaryPrimitives.WriteUInt32LittleEndian(record[48..], 4);
            else
            {
                long startUs = checked(frame * 2000L), endUs = checked((frame + 1L) * 2000L);
                BinaryPrimitives.WriteInt64LittleEndian(record, startUs - 1000); BinaryPrimitives.WriteInt32LittleEndian(record[8..], 1);
                BinaryPrimitives.WriteInt32LittleEndian(record[12..], 1_000_000); BinaryPrimitives.WriteInt64LittleEndian(record[16..], endUs - 1000);
                BinaryPrimitives.WriteInt32LittleEndian(record[24..], 1); BinaryPrimitives.WriteInt32LittleEndian(record[28..], 1_000_000);
                BinaryPrimitives.WriteUInt64LittleEndian(record[32..], checked((ulong)startUs));
                BinaryPrimitives.WriteUInt64LittleEndian(record[40..], checked((ulong)endUs));
                BinaryPrimitives.WriteUInt32LittleEndian(record[48..], frame == frameCount - 1 ? 3u : 1u);
            }
            hash.AppendData(record);
        }
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
