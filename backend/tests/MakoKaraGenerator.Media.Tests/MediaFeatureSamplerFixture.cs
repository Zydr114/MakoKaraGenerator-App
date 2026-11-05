using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Media.Tests;

internal static class MediaFeatureSamplerFixture
{
    internal static MediaDigest Hash(char value) => new(new string(value, 64));

    internal static Project Project(long[]? windows = null, int instances = 1, string parameters = "{}", bool material = false)
    {
        windows ??= [0, 2_000];
        EffectPackageContents original = material ? MixedEffectPackageFixture.Contents(pure: true, background: true) : EffectPackageFixture.Contents();
        JsonObject manifest = JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("media.features", "image.graph");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        JsonArray queries = [];
        for (int i = 0; i < windows.Length; i++)
            foreach (string space in new[] { "sourceNormalized", "canvasPixels" })
                queries.Add(new JsonObject { ["id"] = (space == "sourceNormalized" ? "source-" : "canvas-") + i,
                    ["space"] = space, ["window"] = new JsonObject { ["kind"] = "literal", ["durationUs"] = windows[i] } });
        manifest["mediaQueries"] = queries;
        foreach (JsonNode? export in manifest["exports"]!.AsArray())
        {
            JsonArray references = [];
            foreach (JsonNode? query in queries)
            {
                JsonObject State() => new() { ["kind"] = material ? "outputBypass" : "authorBranch",
                    ["outputs"] = new JsonArray([.. export!["outputs"]!.AsArray().Select(output => (JsonNode?)JsonValue.Create(output!["id"]!.GetValue<string>()))]) };
                references.Add(new JsonObject { ["query"] = query!["id"]!.GetValue<string>(),
                    ["fallback"] = new JsonObject { ["pending"] = State(), ["unavailable"] = State(), ["noMedia"] = State() } });
            }
            export!["mediaQueries"] = references;
        }
        EffectPackageContents package = EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest),
            original.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
        Project project = MakoKaraGenerator.Core.Projects.Project.Create("CPU provider fixture") with
        {
            Composition = new() { CanvasWidth = 320, CanvasHeight = 240 },
            Effects = new([package], material ? [] : Enumerable.Range(0, instances).Select(i => new ProjectEffectInstance
            {
                Id = "owner-" + i, Name = "Owner " + i, PackageId = package.PackageId, Revision = package.Revision,
                ExportId = "scale", Parameters = EffectParameterOverrides.Parse(parameters),
            })),
        };
        return material ? project with { LyricStyle = LyricStyle.Default with
        {
            UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") },
        } } : project;
    }

    internal static ProjectMediaDependency Dependency(ProjectMediaDependencyPlan plan, string query, int owner = 0) =>
        plan.Dependencies.Single(value => ReferenceEquals(value.Owner, plan.Owners[owner]) && value.Query.Id == query);

    internal static MediaInputContext Context(ProjectMediaDependencyPlan plan, MediaFeatureFileSource source, MediaMapping? mapping = null) =>
        MediaInputContext.Create(source.Profile, mapping ?? Mapping(), plan.QueryCatalogDigest, plan.FixedDependencyDigest);

    internal static MediaMapping Mapping(BackgroundFit fit = BackgroundFit.Contain, BackgroundPlaybackMode playback = BackgroundPlaybackMode.Loop,
        MediaOutputMode output = MediaOutputMode.Normal) => MediaMapping.Create(320, 240, fit, playback, new(.125, .25, .375, 1), output);

    internal static MediaFeatureFileSource Source(MediaSourceKind kind = MediaSourceKind.Video, int width = 32, int height = 18,
        int sarNumerator = 1, int sarDenominator = 1, MediaQuarterTurn rotation = MediaQuarterTurn.None, bool mirror = false)
    {
        var writer = new MediaAbIdentityWriter(); var old = Mft1IndependentFixture.MakeSource(kind);
        bool swapped = rotation is MediaQuarterTurn.Clockwise90 or MediaQuarterTurn.Clockwise270;
        var raw = new MediaFilePresentationDescriptor(0, width, height, swapped ? height : width, swapped ? width : height,
            old.Presentation.StreamTimeBase, sarNumerator, sarDenominator, rotation, mirror, old.Presentation.Color, default);
        var descriptor = new MediaFilePresentationDescriptor(raw.StreamIndex, raw.EncodedWidth, raw.EncodedHeight,
            raw.CanonicalWidth, raw.CanonicalHeight, raw.StreamTimeBase, raw.SarNumerator, raw.SarDenominator, raw.Rotation,
            raw.MirrorHorizontallyBeforeRotation, raw.Color, writer.ComputeDescriptorDigest(raw));
        var source = new MediaFeatureFileSource(default, kind, old.Content, descriptor, old.Tool, old.Profile);
        return new(writer.ComputeSourceKey(source), kind, source.Content, descriptor, source.Tool, source.Profile);
    }

    internal static float[] Pixels(int phase = 0)
    {
        float[][] palette = [[0, 0, 0, 0], [1, 1, 1, 1], [.5f, 0, .25f, .5f], [.25f, .125f, .5f, .75f]];
        return Enumerable.Range(0, 576).SelectMany(i => palette[(i % 32 + 3 * (i / 32) + phase) % palette.Length]).ToArray();
    }

    internal static async Task<Opened> BuildAndOpenAsync(MediaFeatureFileSource source, float[][] pixels,
        IMediaWorkingSetBudget runtimeBudget, long frameUs = 2_000)
    {
        var codec = new MediaFeatureTrackCodec(new MediaMomentKernel(), new MediaAbValueValidator());
        var buildBudget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumBuilderWorkingBytes);
        await using var scratch = new Mft1TestScratch(8_000_000);
        await using IMediaFeatureTrackBuilder builder = codec.CreateBuilder(source, scratch, buildBudget);
        var timeBase = new RationalTimeBase(1, 1_000_000);
        for (int i = 0; i < pixels.Length; i++)
        {
            var start = new RationalTimestamp(-7_000_000 + i * frameUs, timeBase);
            await builder.OnFrameAsync(new(source.Key, source.Profile.Digest,
                new(new(i), source.Kind == MediaSourceKind.Video ? start : null,
                    source.Presentation.CanonicalWidth, source.Presentation.CanonicalHeight, source.Presentation.Color.Digest), pixels[i]), CancellationToken.None);
            PresentationInterval interval = source.Kind == MediaSourceKind.Still ? new PresentationInterval.Timeless(new(i))
                : new PresentationInterval.Timed(new(i), start, new(-7_000_000 + (i + 1) * frameUs, timeBase), i * frameUs, (i + 1) * frameUs,
                    i == pixels.Length - 1 ? PresentationEndAuthority.StreamEndpoint : PresentationEndAuthority.NextPts);
            await builder.OnIntervalAsync(interval, CancellationToken.None);
        }
        // Independent handwritten canonical interval bytes supply the expected complete index hash.
        var index = new Mft1IndependentFixture(source.Kind, pixels.Length, frameUs);
        var summary = new PresentationIndexSummary(source.Key, source.Profile.Digest, source.Presentation.Digest,
            index.Identity.PresentationIndexDigest, pixels.Length, source.Kind == MediaSourceKind.Still ? null : pixels.Length * frameUs);
        await using IMediaFeatureTrackWriteSource write = await builder.CompleteAsync(summary, CancellationToken.None);
        using var target = new MemoryStream();
        MediaArtifactIdentity artifact = await codec.WriteCanonicalAsync(write, target, buildBudget, CancellationToken.None);
        byte[] bytes = target.ToArray();
        IMediaFeatureTrackReader reader = await codec.OpenValidatedAsync(new MemoryStream(bytes, writable: false), source, artifact,
            runtimeBudget, CancellationToken.None);
        return new(reader, artifact, bytes);
    }

    internal sealed record Opened(IMediaFeatureTrackReader Reader, MediaArtifactIdentity Artifact, byte[] Bytes) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Reader.DisposeAsync();
    }

    // Used only for deterministic read-count, error, cancellation and lifetime checks, never as numerical acceptance evidence.
    internal sealed class ControlledReader(MediaArtifactIdentity artifact) : IMediaFeatureTrackReader
    {
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }
        internal Action<MediaTemporalQuery, Memory<MediaMomentValues>, CancellationToken>? OnRead { get; set; }
        internal PresentationIndexSummary? OverrideSummary { get; set; }
        public MediaArtifactIdentity Artifact { get; } = artifact;
        public PresentationIndexSummary Summary => OverrideSummary ?? new(Artifact.SourceKey, Artifact.ProfileDigest,
            Artifact.SourceDescriptorDigest, Artifact.PresentationIndexDigest, Artifact.FrameCount, Artifact.VideoDurationUs);
        public void ReadTemporalGrid(MediaTemporalQuery query, Memory<MediaMomentValues> destination, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this); cancellationToken.ThrowIfCancellationRequested(); Reads++;
            if (OnRead is not null) OnRead(query, destination, cancellationToken);
            else destination.Span.Fill(new(0, 0, 0, 1, 0, 0, 1));
        }
        public ValueTask ReadTemporalGridAsync(MediaTemporalQuery query, Memory<MediaMomentValues> destination, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Sampler must call the formal synchronous reader entry.");
        public ValueTask<PresentationInterval> ReadIntervalAsync(PresentationFrameId id, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Sampler must not independently inspect intervals.");
        public ValueTask<PresentationFrameId> SelectAsync(long sourceTimeUs, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Sampler must not independently select frames.");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
