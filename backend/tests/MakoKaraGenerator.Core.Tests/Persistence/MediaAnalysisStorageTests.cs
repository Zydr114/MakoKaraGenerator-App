using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class MediaAnalysisStorageTests
{
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Project Source(TemporaryDirectory directory)
    {
        string audio = directory.FilePath("原曲.wav"), background = directory.FilePath("背景.mp4");
        File.WriteAllBytes(audio, [1, 2, 3, 4]); File.WriteAllBytes(background, [5, 6, 7, 8]);
        var data = new AudioAnalysisRevision(Hash(File.ReadAllBytes(audio)), new string('a', 64), 1921,
            [new(0, .12345678901234567, .4, null, 0), new(20000, .3, .2, .75, .8), new(40000, 0, 0, null, 0)]);
        return Project.Create("媒体闭包", audioPath: audio, background: BackgroundSource.ForVideo(background, BackgroundFit.Cover, BackgroundPlaybackMode.FreezeLastFrame))
            with { AudioAnalysis = ProjectAudioAnalysis.Available(data) };
    }
    private static JsonObject Entry(string directory) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "project.json")))!.AsObject();
    private static void Change(string directory, Action<JsonObject> edit)
    { var root = Entry(directory); edit(root); File.WriteAllText(Path.Combine(directory, "project.json"), root.ToJsonString()); }

    [Fact]
    public void 独立另存为及备份携带音频视频与精确分析且不改源工程()
    {
        using TemporaryDirectory original = new(); using TemporaryDirectory copy = new(); using TemporaryDirectory backups = new();
        var project = Source(original); string saved = original.FilePath("song.mkgproj"); ProjectStore.Save(project, saved); ProjectStore.Save(project, copy.Path);
        string backup = AutoSaveStore.Save(project, backups.Path, DateTimeOffset.UtcNow);
        Assert.Null(project.AudioMedia); Assert.Null(project.Background.Media); Assert.Equal(original.FilePath("原曲.wav"), project.AudioPath);
        File.Delete(project.AudioPath!); File.Delete(project.Background.Path!); Directory.Delete(saved, true);
        foreach (string directory in new[] { copy.Path, backup })
        {
            var loaded = ProjectStore.Load(directory); Assert.True(loaded.Loaded, loaded.Report.ToSummary()); var restored = loaded.Project!;
            Assert.Equal(new byte[] {1, 2, 3, 4}, File.ReadAllBytes(restored.AudioPath!)); Assert.Equal(new byte[] {5, 6, 7, 8}, File.ReadAllBytes(restored.Background.Path!));
            Assert.Equal("原曲.wav", restored.AudioMedia!.OriginalName); Assert.Equal("背景.mp4", restored.Background.Media!.OriginalName);
            Assert.Equal(project.AudioAnalysis, restored.AudioAnalysis); Assert.Equal(40021, restored.AudioAnalysis!.Data!.DurationUs);
            var originalAnalysis = project.AudioAnalysis!.Data!;
            var restoredAnalysis = restored.AudioAnalysis.Data!;
            Assert.Equal(originalAnalysis.ContentHash, restoredAnalysis.ContentHash);
            Assert.Equal(originalAnalysis.Points.ToArray(), restoredAnalysis.Points.ToArray());
            Assert.Equal("available", Entry(directory)["audioAnalysis"]!["state"]!.GetValue<string>());
            Assert.Equal(BackgroundFit.Cover, restored.Background.Fit); Assert.Equal(BackgroundPlaybackMode.FreezeLastFrame, restored.Background.Playback);
        }
    }
    [Fact]
    public void 已有修订不重写而同文件名不同内容产生新地址并保留旧资源()
    {
        using TemporaryDirectory directory = new(); var project = Source(directory); string saved = directory.FilePath("song"); ProjectStore.Save(project, saved);
        var first = ProjectStore.Load(saved).Project!; string analysisFile = Path.Combine(saved, first.AudioAnalysis!.Data!.RelativePath);
        DateTime stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(first.AudioPath!, stamp); File.SetLastWriteTimeUtc(analysisFile, stamp);
        ProjectStore.Save(project, saved); Assert.Equal(stamp, File.GetLastWriteTimeUtc(first.AudioPath!)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(analysisFile));
        File.WriteAllBytes(project.AudioPath!, [4, 3, 2, 1]);
        ProjectStore.Save(project with { AudioAnalysis = null }, saved); var second = ProjectStore.Load(saved).Project!;
        Assert.NotEqual(first.AudioPath, second.AudioPath); Assert.Equal("原曲.wav", second.AudioMedia!.OriginalName); Assert.Null(second.AudioAnalysis);
        Assert.True(File.Exists(first.AudioPath)); Assert.True(File.Exists(analysisFile));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("staleAnalysis")]
    [InlineData("damagedAddress")]
    [InlineData("limit")]
    public void 新资源失败保留旧入口与旧资源且不留暂存(string kind)
    {
        using TemporaryDirectory directory = new(); string saved = directory.FilePath("song"); Project old = Project.Create("旧入口"); ProjectStore.Save(old, saved);
        byte[] root = File.ReadAllBytes(Path.Combine(saved, "project.json")); var next = Source(directory);
        switch (kind)
        {
            case "missing": File.Delete(next.Background.Path!); break;
            case "staleAnalysis": File.WriteAllBytes(next.AudioPath!, [9, 9]); break;
            case "damagedAddress": string path = Path.Combine(saved, "data/media/" + Hash(File.ReadAllBytes(next.AudioPath!)) + ".media"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "wrong"); break;
        }
        Assert.ThrowsAny<IOException>(() => ProjectStore.Save(next, saved, limits: kind == "limit" ? new() { MaximumMediaBytes = 3 } : null));
        Assert.Equal(root, File.ReadAllBytes(Path.Combine(saved, "project.json"))); Assert.Equal("旧入口", ProjectStore.Load(saved).Project!.Name);
        Assert.Empty(Directory.GetFiles(saved, "*.tmp", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("path")]
    [InlineData("name")]
    [InlineData("link")]
    [InlineData("missingTable")]
    [InlineData("colorMedia")]
    public void 媒体坏资源结构不回退为空工程(string kind)
    {
        using TemporaryDirectory directory = new(); var source = Source(directory); string saved = directory.FilePath("song"); ProjectStore.Save(source, saved);
        var restored = ProjectStore.Load(saved).Project!;
        if (kind == "missing") File.Delete(restored.AudioPath!);
        else if (kind == "hash") File.WriteAllBytes(restored.AudioPath!, [9, 9, 9, 9]);
        else if (kind == "link") { File.Delete(restored.AudioPath!); File.CreateSymbolicLink(restored.AudioPath!, source.AudioPath!); }
        else Change(saved, root =>
        {
            switch (kind) { case "length": root["media"]!["audio"]!["byteLength"] = 5; break; case "path": root["audioPath"] = "../原曲.wav"; break;
                case "name": root["media"]!["audio"]!["originalName"] = "../bad"; break; case "missingTable": root.Remove("media"); break;
                case "colorMedia": root["background"]!["kind"] = "color"; break; }
        });
        byte[] root = File.ReadAllBytes(Path.Combine(saved, "project.json")); var loaded = ProjectStore.Load(saved);
        Assert.Null(loaded.Project); Assert.Contains(loaded.Report.Issues, i => i.Code == DiagnosticCode.InvalidMediaResource); Assert.Equal(root, File.ReadAllBytes(Path.Combine(saved, "project.json")));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("bytes")]
    [InlineData("path")]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("state")]
    [InlineData("missingState")]
    [InlineData("link")]
    [InlineData("limit")]
    public void 分析坏资源状态或源身份不自动重建或使用空序列(string kind)
    {
        using TemporaryDirectory directory = new(); var source = Source(directory); string saved = directory.FilePath("song"); ProjectStore.Save(source, saved);
        string path = Path.Combine(saved, source.AudioAnalysis!.Data!.RelativePath);
        if (kind == "missing") File.Delete(path);
        else if (kind == "bytes") File.AppendAllText(path, " ");
        else if (kind == "link") { File.Delete(path); File.CreateSymbolicLink(path, source.AudioPath!); }
        else Change(saved, root =>
        {
            switch (kind)
            {
                case "path": root["audioAnalysis"]!["file"] = "../../outside.json"; break;
                case "revision": root["audioAnalysis"]!["revision"] = "sha256:" + new string('0', 64); break;
                case "state": root["audioAnalysis"]!["state"] = "noAudio"; root["audioAnalysis"]!.AsObject().Remove("file"); root["audioAnalysis"]!.AsObject().Remove("revision"); break;
                case "missingState": root.Remove("audioAnalysis"); break;
                case "source": var other = new AudioAnalysisRevision(new string('b', 64), source.AudioAnalysis.Data.PcmHash, source.AudioAnalysis.Data.SampleCount, source.AudioAnalysis.Data.Points);
                    File.WriteAllBytes(Path.Combine(saved, other.RelativePath), AudioAnalysisJson.Encode(other)); root["audioAnalysis"]!["file"] = other.RelativePath; root["audioAnalysis"]!["revision"] = other.Revision; break;
            }
        });
        var loaded = ProjectStore.Load(saved, kind == "limit" ? new() { MaximumAnalysisBytes = 10 } : null);
        Assert.Null(loaded.Project); Assert.Contains(loaded.Report.Issues, i => i.Code == DiagnosticCode.InvalidAudioAnalysis);
    }
    [Theory]
    [InlineData("notAnalyzed")]
    [InlineData("failed")]
    [InlineData("noStream")]
    public void 不可用分析保留真实状态原因且没有假样本文件(string kind)
    {
        using TemporaryDirectory directory = new(); var source = Source(directory); var analysis = kind == "notAnalyzed" ? null
            : kind == "failed" ? ProjectAudioAnalysis.Failed("RealDecoderFailure", "真实解码失败") : ProjectAudioAnalysis.NoStream("AudioStreamMissing", "真实无流");
        string saved = directory.FilePath("song"); ProjectStore.Save(source with { AudioAnalysis = analysis }, saved);
        var restored = ProjectStore.Load(saved).Project!; Assert.Equal(analysis, restored.AudioAnalysis); Assert.False(Directory.Exists(Path.Combine(saved, "data/analysis")));
    }
    [Fact]
    public void 固定资源被改动后不接受新字节且IO限额可明确调整()
    {
        using TemporaryDirectory directory = new(); var source = Source(directory); string saved = directory.FilePath("song"); ProjectStore.Save(source, saved);
        Assert.Null(ProjectStore.Load(saved, new() { MaximumMediaBytes = 3 }).Project);
        var fixedProject = ProjectStore.Load(saved, new() { MaximumMediaBytes = 4 }).Project!; Assert.NotNull(fixedProject);
        File.WriteAllBytes(fixedProject.Background.Path!, [8, 8, 8, 8]);
        Assert.Throws<IOException>(() => ProjectStore.Save(fixedProject, directory.FilePath("copy")));
        Assert.Null(ProjectStore.Load(saved).Project);
    }
    [Fact]
    public void 分析规范值网格及算法身份严格且不改变精度()
    {
        string hash = new string('a', 64); Assert.Throws<ArgumentException>(() => new AudioAnalysisPoint(0, double.NaN, 0, null, 0));
        Assert.Throws<ArgumentException>(() => new AudioAnalysisPoint(0, 0, 0, null, .1)); Assert.Throws<ArgumentException>(() => new AudioAnalysisPoint(0, 0, 0, 1, .1));
        Assert.Throws<ArgumentException>(() => new AudioAnalysisRevision(hash, hash, 1921, [new(0, 0, 0, null, 0)]));
        var data = new AudioAnalysisRevision(hash, hash, 961, [new(0, .12345678901234567, 0, null, 0), new(20000, 0, 0, null, 0)]);
        Assert.Equal(data, AudioAnalysisJson.Read(AudioAnalysisJson.Encode(data))); Assert.Equal(data.ContentHash, Hash(AudioAnalysisJson.Encode(data)));
        var root = JsonNode.Parse(AudioAnalysisJson.Encode(data))!; root["sampleRate"] = 44100;
        Assert.Throws<MakoKaraGenerator.Core.Effects.EffectPackageException>(() => AudioAnalysisJson.Read(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())));
    }
}
