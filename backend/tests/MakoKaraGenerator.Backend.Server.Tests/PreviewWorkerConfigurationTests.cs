using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed class PreviewWorkerConfigurationTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)] [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, true)]
    public async Task 只公布实际成功握手的独立来源且包失败保留静态(bool preset, bool package, bool image)
    {
        Capability staticCapability = new(preset), packageCapability = new(package, image: image);
        PreviewWorkerConfiguration configured = PreviewWorkerConfiguration.Resolve("/definitely-not-a-worker");
        PreviewWorkerConfiguration result = await PreviewWorkerConfiguration.ResolveProfilesAsync(configured,
            staticCapability, packageCapability);
        Assert.Equal(new[] { BackendSnapshotProfiles.StaticLyricsScenePresets }, staticCapability.Requests);
        Assert.Equal(new[] { PackageProjectSnapshotCodec.Profile, PackagePresetProjectSnapshotCodec.Profile, ProjectImagePreviewSnapshotCodec.Profile, ProjectRuntimeInputWorkerContract.Profile }, packageCapability.Requests);
        Assert.Contains(BackendSnapshotProfiles.StaticLyricsScene, result.Info.SourceProfiles!);
        Assert.Contains(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, result.Info.SourceProfiles!);
        Assert.Equal(preset, result.Info.SourceProfiles!.Contains(BackendSnapshotProfiles.StaticLyricsScenePresets));
        Assert.Equal(package, result.Info.SourceProfiles.Contains(PackageProjectSnapshotCodec.Profile));
        Assert.Equal(image, result.Info.SourceProfiles.Contains(ProjectImagePreviewSnapshotCodec.Profile));
        Assert.Equal(package, result.Info.SourceProfiles.Contains(ProjectRuntimeInputWorkerContract.Profile));
    }

    [Fact]
    public async Task 探测异常不会宣告包可用或删除原静态能力()
    {
        PreviewWorkerConfiguration result = await PreviewWorkerConfiguration.ResolveProfilesAsync(
            PreviewWorkerConfiguration.Resolve("/definitely-not-a-worker"), new Capability(true), new Capability(false, throws: true));
        Assert.Equal(new[] { BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
            BackendSnapshotProfiles.StaticLyricsScenePresets }, result.Info.SourceProfiles!);
    }

    private sealed class Capability(bool enabled, bool throws = false, bool image = false) : IBackendPreviewProfileCapability
    {
        internal List<string> Requests { get; } = [];
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
        {
            Requests.Add(profile);
            if (throws) throw new IOException("probe failed");
            bool success = profile == ProjectImagePreviewSnapshotCodec.Profile ? image : enabled;
            return ValueTask.FromResult<BackendErrorResponse?>(success ? null : new(BackendErrorCode.PreviewUnavailable, "unavailable"));
        }
    }
}
