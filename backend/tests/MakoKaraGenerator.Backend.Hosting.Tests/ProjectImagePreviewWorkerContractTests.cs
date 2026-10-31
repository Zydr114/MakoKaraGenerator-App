using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class ProjectImagePreviewWorkerContractTests
{
    [Fact]
    public void 图片能力明确声明独立profile预算与三种基础来源()
    {
        var package = new PackageRenderWorkerCapabilities("engine", PackageProjectSnapshotCodec.Profile, 1,
            "sdk", "shader", new string('a', 64), 1,
            ValidationProfiles: [PackageRenderWorkerContract.ValidationProfile]);
        var value = new ProjectImagePreviewWorkerCapabilities("engine", BackendSnapshotProfiles.ProjectImagePreview,
            [StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.AnalysisProfile, PackageProjectSnapshotCodec.Profile],
            ProjectImagePreviewWorkerContract.ImageDecoderProfile, ProjectImagePreviewWorkerContract.MaximumImageBytes,
            ProjectImagePreviewWorkerContract.MaximumDimension, ProjectImagePreviewWorkerContract.MaximumDecodedBytes,
            ProjectImagePreviewWorkerContract.MaximumSnapshotBytes, ProjectImagePreviewWorkerContract.MaximumBaseSnapshotBytes,
            ProjectImagePreviewWorkerContract.MaximumSourceValues, WorkerPackageDiagnosticsContract.Version, package);

        byte[] bytes = ProjectImagePreviewWorkerContract.EncodeCapabilities(value);
        var decoded = ProjectImagePreviewWorkerContract.DecodeCapabilities(bytes);
        Assert.Equal(value.EngineIdentity, decoded.EngineIdentity);
        Assert.Equal(value.CodecProfile, decoded.CodecProfile);
        Assert.Equal(value.BaseProfiles, decoded.BaseProfiles);
        Assert.Equal(value.ImageDecoderProfile, decoded.ImageDecoderProfile);
        Assert.Equal(value.MaximumImageBytes, decoded.MaximumImageBytes);
        Assert.Equal(value.MaximumDimension, decoded.MaximumDimension);
        Assert.Equal(value.MaximumDecodedBytes, decoded.MaximumDecodedBytes);
        Assert.Equal(value.MaximumSnapshotBytes, decoded.MaximumSnapshotBytes);
        Assert.Equal(value.MaximumBaseSnapshotBytes, decoded.MaximumBaseSnapshotBytes);
        Assert.Equal(value.MaximumSourceValues, decoded.MaximumSourceValues);
        Assert.Equal(value.PackageDiagnosticsVersion, decoded.PackageDiagnosticsVersion);
        Assert.Equal(value.PackageCapabilities.EngineIdentity, decoded.PackageCapabilities.EngineIdentity);
        Assert.Equal(value.PackageCapabilities.CodecProfile, decoded.PackageCapabilities.CodecProfile);
        Assert.Equal(value.PackageCapabilities.PackageVersion, decoded.PackageCapabilities.PackageVersion);
        Assert.Equal(value.PackageCapabilities.SdkVersion, decoded.PackageCapabilities.SdkVersion);
        Assert.Equal(value.PackageCapabilities.ShaderProfile, decoded.PackageCapabilities.ShaderProfile);
        Assert.Equal(value.PackageCapabilities.ScriptRefsIdentity, decoded.PackageCapabilities.ScriptRefsIdentity);
        Assert.Equal(value.PackageCapabilities.ScriptRefsCount, decoded.PackageCapabilities.ScriptRefsCount);
        Assert.Equal(value.PackageCapabilities.ValidationProfiles, decoded.PackageCapabilities.ValidationProfiles);
        Assert.False(BackendSnapshotProfiles.IsSupported(BackendSnapshotProfiles.ProjectImagePreview));
        Assert.NotEqual(PackageRenderWorkerContract.CapabilitiesKind, ProjectImagePreviewWorkerContract.CapabilitiesKind);
        Assert.NotEqual(PackageRenderWorkerContract.OpenKind, ProjectImagePreviewWorkerContract.OpenKind);
    }

    [Fact]
    public void 图片能力拒绝基础profile重复或预算不匹配()
    {
        var package = new PackageRenderWorkerCapabilities("engine", PackageProjectSnapshotCodec.Profile, 1,
            "sdk", "shader", new string('a', 64), 1);
        var value = new ProjectImagePreviewWorkerCapabilities("engine", BackendSnapshotProfiles.ProjectImagePreview,
            [StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.AnalysisProfile, PackageProjectSnapshotCodec.Profile],
            ProjectImagePreviewWorkerContract.ImageDecoderProfile, ProjectImagePreviewWorkerContract.MaximumImageBytes,
            ProjectImagePreviewWorkerContract.MaximumDimension, ProjectImagePreviewWorkerContract.MaximumDecodedBytes,
            ProjectImagePreviewWorkerContract.MaximumSnapshotBytes, ProjectImagePreviewWorkerContract.MaximumBaseSnapshotBytes,
            ProjectImagePreviewWorkerContract.MaximumSourceValues, WorkerPackageDiagnosticsContract.Version, package);
        Assert.Throws<WorkerProtocolException>(() => ProjectImagePreviewWorkerContract.EncodeCapabilities(
            value with { BaseProfiles = [StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.Profile, PackageProjectSnapshotCodec.Profile] }));
        Assert.Throws<WorkerProtocolException>(() => ProjectImagePreviewWorkerContract.EncodeCapabilities(
            value with { MaximumImageBytes = value.MaximumImageBytes - 1 }));
    }
}
