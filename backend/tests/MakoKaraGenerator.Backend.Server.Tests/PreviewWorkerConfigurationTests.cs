using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed class PreviewWorkerConfigurationTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task 只公布实际成功握手的独立来源且包失败保留静态(bool preset, bool package)
    {
        Capability staticCapability = new(preset), packageCapability = new(package);
        PreviewWorkerConfiguration configured = PreviewWorkerConfiguration.Resolve("/definitely-not-a-worker");
        PreviewWorkerConfiguration result = await PreviewWorkerConfiguration.ResolveProfilesAsync(configured,
            staticCapability, packageCapability);
        Assert.Equal(new[] { BackendSnapshotProfiles.StaticLyricsScenePresets }, staticCapability.Requests);
        Assert.Equal(new[] { PackageProjectSnapshotCodec.Profile, PackagePresetProjectSnapshotCodec.Profile, ProjectRuntimeInputWorkerContract.Profile }, packageCapability.Requests);
        Assert.Contains(BackendSnapshotProfiles.StaticLyricsScene, result.Info.SourceProfiles!);
        Assert.Contains(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, result.Info.SourceProfiles!);
        Assert.Equal(preset, result.Info.SourceProfiles!.Contains(BackendSnapshotProfiles.StaticLyricsScenePresets));
        Assert.Equal(package, result.Info.SourceProfiles.Contains(PackageProjectSnapshotCodec.Profile));
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

    private sealed class Capability(bool enabled, bool throws = false) : IBackendPreviewProfileCapability
    {
        internal List<string> Requests { get; } = [];
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
        {
            Requests.Add(profile);
            if (throws) throw new IOException("probe failed");
            return ValueTask.FromResult<BackendErrorResponse?>(enabled ? null : new(BackendErrorCode.PreviewUnavailable, "unavailable"));
        }
    }
}
