using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class MediaRuntimeSnapshotCodecRejectionTests
{
    [Fact]
    public void 旧source拒绝媒体值而runtime输入以固定授权完整往返()
    {
        var legacy = StaticSnapshotFixture.Create(); byte[] bytes = StaticProjectSnapshotCodec.Encode(legacy);
        Assert.Equal(bytes, StaticProjectSnapshotCodec.Encode(StaticProjectSnapshotCodec.Decode(bytes)));
        var snapshot = legacy with { MediaFeatureInput = Input(legacy) };
        Assert.True(ProjectRuntimeInputCodec.RequiresPreparation(snapshot));
        Assert.Equal("SnapshotMediaFeatureInputUnsupported", Assert.Throws<SnapshotContractException>(() => StaticProjectSnapshotCodec.Encode(snapshot)).Code);
        Assert.Equal("SnapshotMediaFeatureInputUnsupported", Assert.Throws<SnapshotContractException>(() => ProjectRuntimeInputCodec.ValidateSnapshot(bytes, snapshot)).Code);
        byte[] runtime = ProjectRuntimeInputCodec.Encode(snapshot, null);
        var decoded = ProjectRuntimeInputCodec.Decode(runtime).Snapshot;
        Assert.Equal(snapshot.MediaFeatureInput!.InputRevision, decoded.MediaFeatureInput!.InputRevision);
        Assert.Null(ProjectRuntimeInputCodec.ReadMediaGrant(runtime)!.ArtifactPath);
        ProjectRuntimeInputCodec.ValidateSnapshot(runtime, snapshot);
        ProjectRuntimeInputCodec.ValidateSnapshot(runtime, legacy);
        Assert.Throws<SnapshotContractException>(() => ProjectRuntimeInputCodec.Encode(snapshot, null, null, "/tmp/unexpected.mft"));
    }

    private static MediaFeatureInput.NoMedia Input(ProjectSnapshot snapshot)
    {
        var writer = new MediaAbIdentityWriter(); MediaDigest Hash(char value) => new(new string(value, 64));
        var raw = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline, MediaProfilesV1.Color,
            MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu,
            Hash('a'), Hash('b'), default);
        var profile = new MediaProfileBundle(raw.Presentation, raw.DecoderPipeline, raw.Color, raw.Statistics,
            raw.Quantization, raw.Codec, raw.Query, raw.Gpu, raw.DecoderIdentity, raw.ColorInterpretation, writer.ComputeProfileDigest(raw));
        var plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
            snapshot.LyricPresentation, snapshot.PresetApplications);
        var context = MediaInputContext.Create(profile, MediaMapping.CreateSolid(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight),
            plan.QueryCatalogDigest, plan.FixedDependencyDigest);
        return MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.NoBackgroundContribution, Hash('c'));
    }
}
