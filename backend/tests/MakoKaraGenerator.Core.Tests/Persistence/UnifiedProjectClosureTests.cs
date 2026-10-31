using System.Security.Cryptography;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class UnifiedProjectClosureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 工程及备份入口统一持有歌词媒体分析与图材质包并可搬移重开(bool backup)
    {
        using TemporaryDirectory directory = new();
        string audio = directory.FilePath("原曲.wav");
        string background = directory.FilePath("背景.mp4");
        File.WriteAllBytes(audio, [1, 2, 3, 4]);
        File.WriteAllBytes(background, [5, 6, 7, 8]);

        var package = MixedEffectPackageFixture.MaterialContents();
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink",
            EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567}"));
        var lyrics = LyricsDocument.Create([LyricLine.Create("line", "歌", new(0, 1_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌", new(0, 1_000_000), TimingStatus.Edited)])]);
        var style = LyricStyle.Default with
        {
            UnsungFill = new FillSpec { Material = material },
            SungFill = new FillSpec { Material = material },
        };
        var analysis = new AudioAnalysisRevision(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(audio))), new string('a', 64), 961,
            [new(0, .1, .2, null, 0), new(20_000, .2, .3, .4, .5)]);
        var project = Project.Create("统一闭包", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 1_000_000), lyrics,
            lyricStyle: style, audioPath: audio,
            background: BackgroundSource.ForVideo(background, BackgroundFit.Cover, BackgroundPlaybackMode.FreezeLastFrame)) with
        {
            Effects = new([package], []),
            AudioAnalysis = ProjectAudioAnalysis.Available(analysis),
        };

        string saved = backup
            ? AutoSaveStore.Save(project, directory.FilePath("backups"), DateTimeOffset.UtcNow)
            : directory.FilePath("song.mkgproj");
        string moved = directory.FilePath("moved.mkgproj");
        string copy = directory.FilePath("copy.mkgproj");
        if (!backup) ProjectStore.Save(project, saved);
        Directory.Move(saved, moved);
        File.Delete(audio);
        File.Delete(background);

        var reopened = ProjectStore.Load(moved);
        Assert.True(reopened.Loaded, reopened.Report.ToSummary());
        var restored = reopened.Project!;
        Assert.Equal(project.Lyrics, restored.Lyrics);
        Assert.Equal(project.LyricStyle, restored.LyricStyle);
        Assert.Equal(project.Effects, restored.Effects);
        Assert.Equal("原曲.wav", restored.AudioMedia!.OriginalName);
        Assert.Equal("背景.mp4", restored.Background.Media!.OriginalName);
        Assert.Equal(analysis, restored.AudioAnalysis!.Data);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(restored.AudioPath!));
        Assert.Equal([5, 6, 7, 8], File.ReadAllBytes(restored.Background.Path!));

        ProjectStore.Save(restored, copy);
        Assert.True(ProjectStore.Load(copy).Loaded);
    }

    [Fact]
    public void 统一闭包中后置包发布失败不切换旧入口()
    {
        using TemporaryDirectory directory = new();
        string saved = directory.FilePath("song.mkgproj");
        var old = Project.Create("旧入口");
        ProjectStore.Save(old, saved);
        byte[] root = File.ReadAllBytes(Path.Combine(saved, ProjectJson.ProjectFileName));

        string audio = directory.FilePath("new.wav");
        string background = directory.FilePath("new.mp4");
        File.WriteAllBytes(audio, [9, 8, 7, 6]);
        File.WriteAllBytes(background, [6, 7, 8, 9]);
        var package = MixedEffectPackageFixture.MaterialContents();
        string packagePath = Path.Combine(saved, ProjectEffectsJson.PackagePath(package.Revision));
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(Path.Combine(packagePath, "unexpected"), "occupied");
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink", EffectParameterOverrides.Empty);
        var next = Project.Create("新入口", audioPath: audio, background: BackgroundSource.ForVideo(background),
            lyricStyle: LyricStyle.Default with { UnsungFill = new FillSpec { Material = material } }) with
        {
            Effects = new([package], []),
        };

        Assert.Throws<EffectPackageException>(() => ProjectStore.Save(next, saved));
        Assert.Equal(root, File.ReadAllBytes(Path.Combine(saved, ProjectJson.ProjectFileName)));
        Assert.Equal("旧入口", ProjectStore.Load(saved).Project!.Name);
        Assert.Empty(Directory.GetFiles(saved, "*.tmp", SearchOption.AllDirectories));
    }
}
