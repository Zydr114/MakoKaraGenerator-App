using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class ProjectRuntimeInputCodecTests
{
    [Fact]
    public async Task 大字体包固定目录授权不占消息预算且还原原完整包身份()
    {
        using TemporaryDirectory temp = new();
        var package = PackageFontFixture.Contents();
        Assert.True(package.Files.Single(file => file.Role == "font").Content.Length > StaticProjectSnapshotCodec.MaximumBytes);
        var project = PackageSnapshotContractFixture.ToProject(StaticSnapshotFixture.Create()) with { Effects = new([package]) };
        var source = ProjectSnapshot.FromProject(project);
        Assert.True(ProjectRuntimeInputCodec.RequiresPreparation(source));
        string path = temp.FilePath("package"); EffectPackageStorage.Publish(path, package);
        var grants = new ProjectRuntimePackageGrant[] { new(package.PackageId, package.Revision, path) };
        byte[] bytes = ProjectRuntimeInputCodec.Encode(source, null, grants);
        Assert.True(bytes.Length < 64 * 1024);
        Assert.Equal(ProjectRuntimeInputCodec.Profile, ProjectRuntimeInputCodec.ValidateEnvelope(bytes));
        ProjectRuntimeInputCodec.ValidateSnapshot(bytes, source);
        Assert.Equal(source.Composition, ProjectRuntimeInputCodec.ReadComposition(bytes));
        var loaded = await EffectPackageStorage.ReadDirectoryAsync(path, package.Profile);
        var restored = ProjectRuntimeInputCodec.Decode(bytes, new Dictionary<(string, string), EffectPackageContents>
            { [(loaded.PackageId, loaded.Revision)] = loaded }).Snapshot;
        Assert.Equal(source, restored);
        Assert.Equal(package.Revision, restored.Effects.Packages[0].Revision);
        Assert.Equal(package.Files.Single(file => file.Role == "font").Content.ToArray(),
            restored.Effects.Packages[0].Files.Single(file => file.Role == "font").Content.ToArray());
    }

    [Theory]
    [InlineData(BackgroundKind.Image, BackgroundFit.Contain, BackgroundPlaybackMode.Loop)]
    [InlineData(BackgroundKind.Video, BackgroundFit.Cover, BackgroundPlaybackMode.FreezeLastFrame)]
    public void 固定媒体与高级预设共用完整输入且不读取私有文件(BackgroundKind kind, BackgroundFit fit, BackgroundPlaybackMode playback)
    {
        var source = AdvancedPresetSnapshotCodecTests.Create();
        var media = new MediaFileRevision(new string('a', 64), 12345, kind == BackgroundKind.Image ? "背景.png" : "背景.mp4");
        source = source with { Background = new() { Kind = kind, Path = "/host/only/背景", Media = media, Fit = fit, Playback = playback } };
        source = source with { EffectInputsFingerprint = MakoKaraGenerator.Core.Effects.ProjectEffectInputs.Fingerprint(ProjectSnapshotSourceProject.Restore(source)) };
        var grant = new ProjectRuntimeBackgroundGrant(kind, media.RelativePath, "/private/immutable/input", media.Hash,
            media.ByteLength, media.OriginalName, fit, playback);
        byte[] bytes = ProjectRuntimeInputCodec.Encode(source, grant);
        Assert.True(ProjectRuntimeInputCodec.IsRuntimeInput(bytes));
        var copy = ProjectRuntimeInputCodec.Decode(bytes);
        Assert.Equal(ProjectRuntimeInputCodec.Profile, copy.SourceProfile);
        Assert.Equal(grant, copy.Grant);
        Assert.Equal(source with { Background = source.Background with { Path = media.RelativePath } }, copy.Snapshot);
        Assert.Equal(bytes, ProjectRuntimeInputCodec.Encode(copy.Snapshot, grant));
    }

    [Fact]
    public void 原五来源保持原字节且非法授权不能覆盖媒体身份()
    {
        var source = AdvancedPresetSnapshotCodecTests.Create();
        Assert.Equal(ProjectSnapshotSourceProfile.Encode(source), ProjectRuntimeInputCodec.Encode(source, null));
        var copy = ProjectRuntimeInputCodec.Decode(ProjectRuntimeInputCodec.Encode(source, null));
        Assert.Null(copy.Grant); Assert.Equal(source, copy.Snapshot);
        Assert.Throws<SnapshotContractException>(() => ProjectRuntimeInputCodec.Encode(source,
            new(BackgroundKind.Video, "invalid", "/private/input", new string('a', 64), 123, "x.mp4", BackgroundFit.Cover, BackgroundPlaybackMode.Loop)));
    }
}
