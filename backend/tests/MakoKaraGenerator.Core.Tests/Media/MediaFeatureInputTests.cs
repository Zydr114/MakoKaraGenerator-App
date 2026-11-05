using System.Reflection;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using Xunit;

namespace MakoKaraGenerator.Core.Tests.Media;

public sealed class MediaFeatureInputTests
{
    private static readonly string[] FrozenInputDigests =
    [
        "6432835c3f6c9a6770d20a90a2f0da6da83901e37e1cb5f59c345af952739c52",
        "65b68cc0c9977de112f039052b85dd0c6b420e8528e57d3bef3e732240f114dc",
        "e9e5f4a717c0455bb062498f97a2c2dda6d3092034eed94ee8fbda22d74c8110",
        "d17d02470e94c75bcf4ae7dc6d98e4f7a47a0dadba536f370375b55fdee54116",
        "d1c410c02b8191c336671280e6069a70c20942419f700bb36a5d82767a717c41",
    ];
    private static MediaDigest H(char digit) => MediaFoundationFixtures.Hash(digit);
    private static MediaMapping Mapping(MediaOutputMode output = MediaOutputMode.Normal) =>
        MediaMapping.Create(320, 240, BackgroundFit.Contain, BackgroundPlaybackMode.Loop, new(.125, .25, .375, 1), output);
    private static MediaInputContext Context(MediaProfileBundle profile, MediaMapping? mapping = null, MediaDigest? queries = null, MediaDigest? dependencies = null) =>
        MediaInputContext.Create(profile, mapping ?? Mapping(), queries ?? H('6'), dependencies ?? H('7'));
    private static MediaFailure Failure(char id = '9', string code = "MediaDecodeFailed", string detail = "Controlled decoder failed.") =>
        MediaFailure.Create(new(H(id)), code, detail);

    [Fact]
    public void AllFiveBranchesMatchIndependentByteOracleAndHaveExplicitState()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source();
        MediaInputContext context = Context(source.Profile);
        MediaInputContext solidContext = Context(source.Profile, MediaMapping.CreateSolid(320, 240));
        MediaInputContext transparent = Context(source.Profile, Mapping(MediaOutputMode.Transparent));
        var solid = MediaFeatureSolidSource.Create(source.Profile, new(.25, .125, 0, .5));
        MediaFeatureInput[] inputs = [MediaFeatureInput.CreatePending(context, source, new(H('8'))),
            MediaFeatureInput.CreateAvailableFile(context, source, MediaFoundationFixtures.Artifact(source)),
            MediaFeatureInput.CreateAvailableSolid(solidContext, solid),
            MediaFeatureInput.CreateUnavailable(context, source, new(H('8')), Failure()),
            MediaFeatureInput.CreateNoMedia(transparent, MediaNoMediaReason.TransparentOutput, H('a'))];
        Assert.Equal(new[] { MediaInputState.Pending, MediaInputState.Available, MediaInputState.Available,
            MediaInputState.Unavailable, MediaInputState.NoMedia }, inputs.Select(i => i.State).ToArray());
        Assert.Equal(5, inputs.Select(i => i.InputRevision).Distinct().Count());
        foreach (MediaFeatureInput input in inputs)
        {
            MediaFeatureInputValidator.Validate(input);
            Assert.Equal(MediaFeatureInputOracle.Input(input), input.InputRevision.Digest.LowerHex);
            Assert.Same(source.Profile, input.Context.RequestedProfile);
        }
        Assert.Equal(MediaFeatureInputOracle.Mapping(context.Mapping), context.Mapping.Key.Digest.LowerHex);
        Assert.Equal(MediaFeatureInputOracle.Solid(solid), solid.Key.Digest.LowerHex);
        // Frozen vectors were generated from literal byte preimages by tools/encode_core_vectors.py.
        Assert.Equal("9cfc70560ca93a4d6846fcbb5a19d613c752109318598ba662dae62eed354b1c", context.Mapping.Key.Digest.LowerHex);
        Assert.Equal("69800b7aeba97f122f4728a9209373ecbf28aef7c77b521257faf050b1ad9017", solid.Key.Digest.LowerHex);
        Assert.Equal(FrozenInputDigests,
            inputs.Select(i => i.InputRevision.Digest.LowerHex).ToArray());
    }

    [Fact]
    public void MappingEveryFieldAndBinary64ColorBitParticipatesInIdentity()
    {
        MediaMapping baseline = Mapping();
        MediaMapping[] variants = [MediaMapping.Create(321, 240, baseline.Fit, baseline.Playback, baseline.BackColor, baseline.OutputMode),
            MediaMapping.Create(320, 241, baseline.Fit, baseline.Playback, baseline.BackColor, baseline.OutputMode),
            MediaMapping.Create(320, 240, BackgroundFit.Cover, baseline.Playback, baseline.BackColor, baseline.OutputMode),
            MediaMapping.Create(320, 240, baseline.Fit, BackgroundPlaybackMode.FreezeLastFrame, baseline.BackColor, baseline.OutputMode),
            MediaMapping.Create(320, 240, baseline.Fit, baseline.Playback, new(Math.BitIncrement(.125), .25, .375, 1), baseline.OutputMode),
            MediaMapping.Create(320, 240, baseline.Fit, baseline.Playback, new(.125, Math.BitIncrement(.25), .375, 1), baseline.OutputMode),
            MediaMapping.Create(320, 240, baseline.Fit, baseline.Playback, new(.125, .25, Math.BitIncrement(.375), 1), baseline.OutputMode),
            MediaMapping.Create(320, 240, baseline.Fit, baseline.Playback, new(.125, .25, .375, Math.BitDecrement(1)), baseline.OutputMode),
            Mapping(MediaOutputMode.Transparent)];
        Assert.Equal(10, variants.Append(baseline).Select(m => m.Key).Distinct().Count());
        foreach (MediaMapping mapping in variants.Append(baseline))
        {
            MediaFeatureInputValidator.ValidateMapping(mapping);
            Assert.Equal(MediaFeatureInputOracle.Mapping(mapping), mapping.Key.Digest.LowerHex);
        }
    }

    [Fact]
    public void PendingAndUnavailableAttemptsAreFixedButAvailableContentCanBeReused()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source(); MediaInputContext context = Context(source.Profile);
        var pending = MediaFeatureInput.CreatePending(context, source, new(H('8')));
        var retry = MediaFeatureInput.CreatePending(context, source, new(H('b')));
        var failed = MediaFeatureInput.CreateUnavailable(context, source, pending.Request, Failure());
        var retryFailed = MediaFeatureInput.CreateUnavailable(context, source, retry.Request, Failure());
        Assert.NotEqual(pending.InputRevision, retry.InputRevision);
        Assert.NotEqual(failed.InputRevision, retryFailed.InputRevision);
        MediaArtifactIdentity artifact = MediaFoundationFixtures.Artifact(source);
        var first = MediaFeatureInput.CreateAvailableFile(context, source, artifact);
        var second = MediaFeatureInput.CreateAvailableFile(context, source, artifact);
        Assert.Equal(first.InputRevision, second.InputRevision);
        Assert.Equal(MediaInputState.Pending, pending.State); Assert.Equal(MediaInputState.Unavailable, failed.State);
        Assert.DoesNotContain(typeof(MediaFeatureInput.AvailableFile).GetProperties(), p => p.Name == "Request");
        Assert.DoesNotContain(typeof(MediaFeatureInput.AvailableSolid).GetProperties(), p => p.Name == "Request");
    }

    [Fact]
    public void FailureIdAndCodeEnterIdentityButHumanDetailDoesNot()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source(); MediaInputContext context = Context(source.Profile);
        MediaInputRevision Make(MediaFailure failure) => MediaFeatureInput.CreateUnavailable(context, source, new(H('8')), failure).InputRevision;
        MediaInputRevision original = Make(Failure());
        Assert.Equal(original, Make(Failure(detail: "Another safe localized detail.")));
        Assert.NotEqual(original, Make(Failure(id: 'a')));
        Assert.NotEqual(original, Make(Failure(code: "MediaPresentationInvalid")));
    }

    [Fact]
    public void NoMediaNeedsNoFileSourceAndStillBindsRequestedProfileAndDeclaration()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source();
        var context = Context(source.Profile, Mapping(MediaOutputMode.Transparent));
        var absent = MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.TransparentOutput, H('a'));
        Assert.Same(source.Profile, absent.Context.RequestedProfile);
        Assert.DoesNotContain(typeof(MediaFeatureInput.NoMedia).GetProperties(), p => p.Name is "Source" or "Artifact" or "Request" or "Failure");
        Assert.NotEqual(absent.InputRevision, MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.TransparentOutput, H('b')).InputRevision);
        MediaProfileBundle otherProfile = MediaFoundationFixtures.Profile(H('c'), source.Presentation.Color.Digest);
        var changed = MediaFeatureInput.CreateNoMedia(Context(otherProfile, context.Mapping), MediaNoMediaReason.TransparentOutput, H('a'));
        Assert.NotEqual(absent.InputRevision, changed.InputRevision);
        var noContribution = MediaFeatureInput.CreateNoMedia(Context(source.Profile), MediaNoMediaReason.NoBackgroundContribution, H('a'));
        MediaFeatureInputValidator.Validate(noContribution);
    }

    [Fact]
    public void QueryDependencyMappingAndArtifactChangesInvalidateInputIdentity()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source(); MediaArtifactIdentity artifact = MediaFoundationFixtures.Artifact(source);
        MediaInputRevision Make(MediaInputContext context, MediaArtifactIdentity? a = null) => MediaFeatureInput.CreateAvailableFile(context, source, a ?? artifact).InputRevision;
        MediaInputRevision baseline = Make(Context(source.Profile));
        Assert.NotEqual(baseline, Make(Context(source.Profile, queries: H('c'))));
        Assert.NotEqual(baseline, Make(Context(source.Profile, dependencies: H('c'))));
        Assert.NotEqual(baseline, Make(Context(source.Profile, MediaMapping.Create(320, 240, BackgroundFit.Cover,
            BackgroundPlaybackMode.Loop, new(.125, .25, .375, 1), MediaOutputMode.Normal))));
        var changedArtifact = new MediaArtifactIdentity(H('c'), artifact.ByteLength, artifact.Format, artifact.SourceKey,
            artifact.ProfileDigest, artifact.SourceDescriptorDigest, artifact.PresentationIndexDigest, artifact.FrameCount, artifact.VideoDurationUs);
        Assert.NotEqual(baseline, Make(Context(source.Profile), changedArtifact));
    }

    [Fact]
    public void TransparentOutputOnlyAllowsExplicitNoMediaBeforePreparation()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source();
        MediaInputContext context = Context(source.Profile, Mapping(MediaOutputMode.Transparent));
        Reject(() => MediaFeatureInput.CreatePending(context, source, new(H('8'))));
        Reject(() => MediaFeatureInput.CreateUnavailable(context, source, new(H('8')), Failure()));
        Reject(() => MediaFeatureInput.CreateAvailableFile(context, source, MediaFoundationFixtures.Artifact(source)));
        Reject(() => MediaFeatureInput.CreateAvailableSolid(context, MediaFeatureSolidSource.Create(source.Profile, new(0, 0, 0, 1))));
        Reject(() => MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.NoBackgroundContribution, H('a')));
        Reject(() => MediaFeatureInput.CreateNoMedia(Context(source.Profile), MediaNoMediaReason.TransparentOutput, H('a')));
        Reject(() => MediaFeatureInput.CreateNoMedia(context, (MediaNoMediaReason)2, H('a')));
    }

    [Fact]
    public void EveryBranchRejectsInvalidContextProfileOrMissingRequiredIdentity()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source(); MediaInputContext context = Context(source.Profile);
        MediaProfileBundle invalid = MediaFoundationFixtures.Profile(source.Tool.Digest, source.Presentation.Color.Digest, H('f'));
        Reject(() => Context(invalid));
        Reject(() => Context(source.Profile, queries: default(MediaDigest)));
        Reject(() => Context(source.Profile, dependencies: default(MediaDigest)));
        Reject(() => MediaFeatureInput.CreatePending(context, source, default));
        Reject(() => MediaFeatureInput.CreateUnavailable(context, source, default, Failure()));
        Reject(() => MediaFeatureInput.CreateNoMedia(Context(source.Profile, Mapping(MediaOutputMode.Transparent)), MediaNoMediaReason.TransparentOutput, default));
        Assert.Throws<ArgumentNullException>(() => MediaFeatureInput.CreatePending(null!, source, new(H('8'))));
        Assert.Throws<ArgumentNullException>(() => MediaFeatureInput.CreateUnavailable(context, source, new(H('8')), null!));
        Assert.Throws<ArgumentNullException>(() => MediaFeatureInput.CreateAvailableFile(context, source, null!));
        Assert.Throws<ArgumentNullException>(() => MediaFeatureInput.CreateAvailableSolid(Context(source.Profile, MediaMapping.CreateSolid(320, 240)), null!));
        Assert.Throws<ArgumentNullException>(() => MediaInputContext.Create(null!, Mapping(), H('6'), H('7')));
    }

    [Fact]
    public void RehashedNestedSourceAndProfileCrossBindingsAreRejectedByFactories()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source();
        MediaProfileBundle different = MediaFoundationFixtures.Profile(H('a'), source.Presentation.Color.Digest);
        var context = Context(different);
        Reject(() => MediaFeatureInput.CreatePending(context, source, new(H('8'))));
        Reject(() => MediaFeatureInput.CreateUnavailable(context, source, new(H('8')), Failure()));
        Reject(() => MediaFeatureInput.CreateAvailableFile(context, source, MediaFoundationFixtures.Artifact(source)));
        Reject(() => MediaFeatureInput.CreateAvailableSolid(Context(different, MediaMapping.CreateSolid(320, 240)),
            MediaFeatureSolidSource.Create(source.Profile, new(.25, 0, 0, .5))));
        MediaFeatureFileSource wrongDecoder = MediaFoundationFixtures.Source(profile: different);
        Reject(() => MediaFeatureInput.CreatePending(context, wrongDecoder, new(H('8'))));
        MediaFeatureFileSource wrongNestedDigest = MediaFoundationFixtures.Source(descriptor:
            MediaFoundationFixtures.Descriptor(color: MediaFoundationFixtures.Color(ownDigest: H('f'))));
        Reject(() => MediaFeatureInput.CreatePending(Context(source.Profile), wrongNestedDigest, new(H('8'))));
    }

    [Fact]
    public void AvailableFileRevalidatesAllArtifactBindingsRatherThanOnlyHashText()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source(); MediaInputContext context = Context(source.Profile);
        MediaArtifactIdentity a = MediaFoundationFixtures.Artifact(source);
        MediaArtifactIdentity[] invalid = [new(a.ContentHash, a.ByteLength + 1, a.Format, a.SourceKey, a.ProfileDigest, a.SourceDescriptorDigest, a.PresentationIndexDigest, a.FrameCount, a.VideoDurationUs),
            new(a.ContentHash, a.ByteLength, "unknown", a.SourceKey, a.ProfileDigest, a.SourceDescriptorDigest, a.PresentationIndexDigest, a.FrameCount, a.VideoDurationUs),
            new(a.ContentHash, a.ByteLength, a.Format, new(H('a')), a.ProfileDigest, a.SourceDescriptorDigest, a.PresentationIndexDigest, a.FrameCount, a.VideoDurationUs),
            new(a.ContentHash, a.ByteLength, a.Format, a.SourceKey, H('a'), a.SourceDescriptorDigest, a.PresentationIndexDigest, a.FrameCount, a.VideoDurationUs),
            new(a.ContentHash, a.ByteLength, a.Format, a.SourceKey, a.ProfileDigest, H('a'), a.PresentationIndexDigest, a.FrameCount, a.VideoDurationUs),
            new(a.ContentHash, a.ByteLength, a.Format, a.SourceKey, a.ProfileDigest, a.SourceDescriptorDigest, default, a.FrameCount, a.VideoDurationUs),
            new(a.ContentHash, a.ByteLength, a.Format, a.SourceKey, a.ProfileDigest, a.SourceDescriptorDigest, a.PresentationIndexDigest, a.FrameCount, 1)];
        foreach (MediaArtifactIdentity bad in invalid) Reject(() => MediaFeatureInput.CreateAvailableFile(context, source, bad));
    }

    [Fact]
    public void AnalyticSolidUsesItsOwnSourceAndCanonicalMappingWithoutDoubleBottom()
    {
        MediaFeatureFileSource file = MediaFoundationFixtures.Source();
        MediaFeatureSolidSource solid = MediaFeatureSolidSource.Create(file.Profile, new(.25, .125, 0, .5));
        MediaFeatureInputValidator.ValidateSolidSource(solid);
        Assert.NotEqual(file.Key, solid.Key);
        Reject(() => MediaFeatureInput.CreateAvailableSolid(Context(file.Profile), solid));
        foreach (MediaMapping bad in new[] { MediaMapping.Create(320, 240, BackgroundFit.Cover, BackgroundPlaybackMode.Loop, default, MediaOutputMode.Normal),
            MediaMapping.Create(320, 240, BackgroundFit.Contain, BackgroundPlaybackMode.FreezeLastFrame, default, MediaOutputMode.Normal) })
            Reject(() => MediaFeatureInput.CreateAvailableSolid(Context(file.Profile, bad), solid));
        var input = MediaFeatureInput.CreateAvailableSolid(Context(file.Profile, MediaMapping.CreateSolid(320, 240)), solid);
        Assert.Equal(default, input.Context.Mapping.BackColor);
        Assert.DoesNotContain(typeof(MediaFeatureInput.AvailableSolid).GetProperties(), p => p.Name == "Artifact");
        MediaFeatureSolidSource changed = MediaFeatureSolidSource.Create(file.Profile, new(Math.BitIncrement(.25), .125, 0, .5));
        Assert.NotEqual(solid.Key, changed.Key);
    }

    [Fact]
    public void InvalidMappingsAndNoncanonicalColorsAreNeverRepaired()
    {
        foreach (LinearPremulRgba color in new[] { new LinearPremulRgba(-0d, 0, 0, 1), new(double.NaN, 0, 0, 1), new(1, 0, 0, .5), new(0, 0, 0, 1.001) })
        {
            Reject(() => MediaMapping.Create(320, 240, BackgroundFit.Contain, BackgroundPlaybackMode.Loop, color, MediaOutputMode.Normal));
            Reject(() => MediaFeatureSolidSource.Create(MediaFoundationFixtures.Source().Profile, color));
        }
        Reject(() => MediaMapping.Create(0, 240, BackgroundFit.Contain, BackgroundPlaybackMode.Loop, default, MediaOutputMode.Normal));
        Reject(() => MediaMapping.Create(8193, 1, BackgroundFit.Contain, BackgroundPlaybackMode.Loop, default, MediaOutputMode.Normal));
        Reject(() => MediaMapping.Create(320, 240, (BackgroundFit)3, BackgroundPlaybackMode.Loop, default, MediaOutputMode.Normal));
        Reject(() => MediaMapping.Create(320, 240, BackgroundFit.Contain, (BackgroundPlaybackMode)2, default, MediaOutputMode.Normal));
        Reject(() => MediaMapping.Create(320, 240, BackgroundFit.Contain, BackgroundPlaybackMode.Loop, default, (MediaOutputMode)2));
    }

    [Fact]
    public void FailureCodesDetailsAndIdsHaveStrictFiniteSafeBounds()
    {
        Reject(() => MediaFailure.Create(default, "MediaDecodeFailed", "Safe detail."));
        foreach (string code in new[] { "", new string('a', 65), "bad/code", "bad:code", "坏码", "bad\ncode", "bad code" })
            Reject(() => Failure(code: code));
        foreach (string detail in new[] { "", " ", new string('a', 1025), "path/to/file", "C:\\file", "bad\ntext", "\uFEFFbad", new string('\ud800', 1) })
            Reject(() => Failure(detail: detail));
        Assert.Equal(64, Failure(code: new string('a', 64)).Code.Length);
        Assert.Equal(1024, Failure(detail: new string('a', 1024)).SafeDetail.Length);
    }

    [Fact]
    public void ValidatorsRecomputeNestedKeysAndFinalRevisionAtAcceptance()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source(); MediaInputContext context = Context(source.Profile);
        MediaFeatureInput input = MediaFeatureInput.CreatePending(context, source, new(H('8')));
        // Test-only field corruption models a malformed deserializer or unsafe boundary; no production mutator exists.
        Corrupt(context.Mapping, "Key", new MediaMappingKey(H('a')));
        Reject(() => MediaFeatureInputValidator.Validate(input));
        Reject(() => MediaInputContext.Create(source.Profile, context.Mapping, H('6'), H('7')));

        MediaFeatureSolidSource solid = MediaFeatureSolidSource.Create(source.Profile, new(.25, 0, 0, .5));
        Corrupt(solid, "Key", new MediaSourceKey(H('a')));
        Reject(() => MediaFeatureInputValidator.ValidateSolidSource(solid));
        Reject(() => MediaFeatureInput.CreateAvailableSolid(Context(source.Profile, MediaMapping.CreateSolid(320, 240)), solid));

        input = MediaFeatureInput.CreatePending(Context(source.Profile), source, new(H('8')));
        Corrupt(input, "InputRevision", new MediaInputRevision(H('a')), typeof(MediaFeatureInput));
        Reject(() => MediaFeatureInputValidator.Validate(input));
        input = MediaFeatureInput.CreatePending(Context(source.Profile), source, new(H('8')));
        Corrupt(input, "State", MediaInputState.Available, typeof(MediaFeatureInput));
        Reject(() => MediaFeatureInputValidator.Validate(input));
    }

    [Fact]
    public void PublicInputApiOffersNoMutablePropertiesOrPublicBranchConstructors()
    {
        Type[] types = [typeof(MediaFeatureInput), typeof(MediaFeatureInput.Pending), typeof(MediaFeatureInput.AvailableFile),
            typeof(MediaFeatureInput.AvailableSolid), typeof(MediaFeatureInput.Unavailable), typeof(MediaFeatureInput.NoMedia),
            typeof(MediaMapping), typeof(MediaFeatureSolidSource), typeof(MediaInputContext), typeof(MediaFailure)];
        foreach (Type type in types)
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.DoesNotContain(type.GetProperties(), p => p.SetMethod?.IsPublic == true);
        }
    }

    [Fact]
    public void Binary64QueryIsTheOnlyAcceptedProfileAndHasIndependentUpdatedHashVectors()
    {
        MediaFeatureFileSource source = MediaFoundationFixtures.Source();
        Assert.Equal("media-grid-area-binary64-v1", MediaProfilesV1.Query);
        Assert.Equal(MediaFeatureInputOracle.Profile(source.Profile), source.Profile.Digest.LowerHex);
        Assert.Equal("943bb54376f97500fead16637f2c17d43d1f136f5a8ad724bd34d0eff77204b9", source.Profile.Digest.LowerHex);
        MediaProfileBundle old = new(source.Profile.Presentation, source.Profile.DecoderPipeline, source.Profile.Color,
            source.Profile.Statistics, source.Profile.Quantization, source.Profile.Codec,
            "media-grid-area-f32-coordinates-v1", source.Profile.Gpu,
            source.Profile.DecoderIdentity, source.Profile.ColorInterpretation, source.Profile.Digest);
        Reject(() => MediaFoundationFixtures.Validator.ValidateProfile(old));
        Reject(() => Context(old));
        Reject(() => MediaFeatureSolidSource.Create(old, new(0, 0, 0, 1)));
        MediaFeatureInput absent = MediaFeatureInput.CreateNoMedia(Context(source.Profile, Mapping(MediaOutputMode.Transparent)),
            MediaNoMediaReason.TransparentOutput, H('a'));
        Assert.Equal(MediaProfilesV1.Query, absent.Context.RequestedProfile.Query);
    }

    private static void Corrupt(object target, string property, object value, Type? declaringType = null)
    {
        FieldInfo field = (declaringType ?? target.GetType()).GetField("<" + property + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(field); field.SetValue(target, value);
    }
    private static void Reject(Action action) => Assert.Throws<MediaAbException>(action);
}
