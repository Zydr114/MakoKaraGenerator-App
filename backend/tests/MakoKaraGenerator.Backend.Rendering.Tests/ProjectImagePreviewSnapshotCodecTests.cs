using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class ProjectImagePreviewSnapshotCodecTests
{
    [Fact]
    public void 静态图片往返保留背景身份适配和底色且封包不含原始路径()
    {
        byte[] image = Png();
        string hash = Convert.ToHexStringLower(SHA256.HashData(image));
        ProjectSnapshot source = StaticSnapshotFixture.Create() with
        {
            Background = new BackgroundSource
            {
                Kind = BackgroundKind.Image, Path = "/private/project/background.png",
                Media = new(hash, image.Length, "background.png"), Fit = BackgroundFit.Cover,
                Playback = BackgroundPlaybackMode.FreezeLastFrame,
            },
        };
        byte[] encoded = ProjectImagePreviewSnapshotCodec.Encode(source, image);
        string wire = System.Text.Encoding.UTF8.GetString(encoded);
        Assert.DoesNotContain("/private/project/background.png", wire, StringComparison.Ordinal);
        var decoded = ProjectImagePreviewSnapshotCodec.Decode(encoded);
        Assert.Equal(source.Background with { Path = $"data/media/{hash}.media" }, decoded.Snapshot.Background);
        Assert.Equal($"data/media/{hash}.media", decoded.LogicalPath);
        Assert.Equal(image, decoded.ImageBytes);
        Assert.Equal(hash, decoded.ImageSha256);
        Assert.Equal(encoded, ProjectImagePreviewSnapshotCodec.Encode(decoded.Snapshot, decoded.ImageBytes));
    }

    [Fact]
    public void 静态分析图片往返保留分析基础能力()
    {
        byte[] image = Png();
        string hash = Convert.ToHexStringLower(SHA256.HashData(image));
        ProjectSnapshot source = AnalysisStaticProjectSnapshotCodecTestsSource() with
        {
            Background = BackgroundSource.ForImage("/private/analysis.png") with
            { Media = new(hash, image.Length, "analysis.png") },
        };
        byte[] encoded = ProjectImagePreviewSnapshotCodec.Encode(source, image);
        var decoded = ProjectImagePreviewSnapshotCodec.Decode(encoded);
        Assert.Equal(Backend.Contracts.BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
            ProjectPreviewSnapshotProfile.Select(decoded.Snapshot with { Background = BackgroundSource.Color }));
        Assert.NotNull(decoded.Snapshot.AudioAnalysis);
    }

    private static ProjectSnapshot AnalysisStaticProjectSnapshotCodecTestsSource()
    {
        ProjectSnapshot basis = StaticSnapshotFixture.Create();
        string hash = new('a', 64);
        return basis with
        {
            AudioPath = "/private/audio.wav", AudioMedia = new(hash, 12, "audio.wav"),
            AudioAnalysis = ProjectAudioAnalysis.NoStream("NoStream", "没有音轨"),
        };
    }

    [Fact]
    public void 包图片往返保留包来源且拒绝预设()
    {
        byte[] image = Png();
        ProjectSnapshot source = PackageSnapshotContractFixture.Create() with
        {
            Background = BackgroundSource.ForImage("/private/package/image.png", BackgroundFit.Contain) with
            { Media = new(Convert.ToHexStringLower(SHA256.HashData(image)), image.Length, "image.png") },
        };
        var decoded = ProjectImagePreviewSnapshotCodec.Decode(ProjectImagePreviewSnapshotCodec.Encode(source, image));
        Assert.Equal(Backend.Contracts.BackendSnapshotProfiles.PackageLyricsScene, ProjectPreviewSnapshotProfile.Select(decoded.Snapshot with { Background = BackgroundSource.Color }));
        Assert.NotEmpty(decoded.Snapshot.Effects.Packages);
        Assert.Equal(BackgroundKind.Image, decoded.Snapshot.Background.Kind);
        ProjectSnapshot preset = PresetSnapshotFixture.Create() with
        {
            Background = BackgroundSource.ForImage("/private/preset.png") with
            { Media = new(Convert.ToHexStringLower(SHA256.HashData(image)), image.Length, "preset.png") },
        };
        AssertCode("SnapshotEffectsUnsupported", () => ProjectImagePreviewSnapshotCodec.Encode(preset, image));
    }

    [Fact]
    public void 图片大小摘要媒体身份和重复字段均严格拒绝()
    {
        byte[] image = Png();
        ProjectSnapshot source = StaticSnapshotFixture.Create() with
        {
            Background = BackgroundSource.ForImage("/private/background.png") with
            { Media = new(new string('a', 64), image.Length + 1, "background.png") },
        };
        AssertCode("ProjectImageSourceChanged", () => ProjectImagePreviewSnapshotCodec.Encode(source, image));
        ProjectSnapshot valid = source with { Background = source.Background with
            { Media = new(Convert.ToHexStringLower(SHA256.HashData(image)), image.Length, "background.png") } };
        byte[] encoded = ProjectImagePreviewSnapshotCodec.Encode(valid, image);
        JsonObject wire = JsonNode.Parse(encoded)!.AsObject();
        wire["imageSha256"] = "bad";
        AssertCode("SnapshotInvalid", () => ProjectImagePreviewSnapshotCodec.Decode(System.Text.Encoding.UTF8.GetBytes(wire.ToJsonString())));
        string duplicate = System.Text.Encoding.UTF8.GetString(encoded);
        duplicate = duplicate[..^1] + ",\"profile\":\"project-image-preview-v1\"}";
        AssertCode("SnapshotDuplicateField", () => ProjectImagePreviewSnapshotCodec.Decode(System.Text.Encoding.UTF8.GetBytes(duplicate)));
    }

    [Theory]
    [InlineData("unknown", "SnapshotInvalid")]
    [InlineData("nullBackground", "SnapshotInvalid")]
    [InlineData("nullMedia", "SnapshotInvalid")]
    [InlineData("nullImage", "SnapshotInvalid")]
    [InlineData("nullBase", "SnapshotInvalid")]
    [InlineData("missingField", "SnapshotInvalid")]
    [InlineData("wrongKind", "SnapshotInvalid")]
    [InlineData("wrongFit", "SnapshotInvalid")]
    [InlineData("wrongPlayback", "SnapshotInvalid")]
    [InlineData("wrongPath", "SnapshotInvalid")]
    [InlineData("wrongName", "SnapshotInvalid")]
    [InlineData("wrongSize", "ProjectImageSourceChanged")]
    [InlineData("wrongHash", "ProjectImageSourceChanged")]
    [InlineData("wrongBytes", "ProjectImageSourceChanged")]
    [InlineData("wrongImageHash", "ProjectImageSourceChanged")]
    [InlineData("wrongEngine", "SnapshotVersionMismatch")]
    [InlineData("wrongProfile", "SnapshotVersionMismatch")]
    [InlineData("wrongBaseProfile", "SnapshotVersionMismatch")]
    [InlineData("wrongFingerprint", "SnapshotFingerprintMismatch")]
    public void 图片封包严格拒绝未知字段空值枚举能力和内容身份(string mutation, string code)
    {
        byte[] image = Png();
        ProjectSnapshot source = ImageSource(image);
        JsonObject wire = JsonNode.Parse(ProjectImagePreviewSnapshotCodec.Encode(source, image))!.AsObject();
        switch (mutation)
        {
            case "unknown": wire["background"]!["extra"] = 1; break;
            case "nullBackground": wire["background"] = null; break;
            case "nullMedia": wire["background"]!["media"] = null; break;
            case "nullImage": wire["imageBytes"] = null; break;
            case "nullBase": wire["snapshotBytes"] = null; break;
            case "missingField": wire.Remove("imageSha256"); break;
            case "wrongKind": wire["background"]!["kind"] = 2; break;
            case "wrongFit": wire["background"]!["fit"] = 99; break;
            case "wrongPlayback": wire["background"]!["playback"] = 99; break;
            case "wrongPath": wire["background"]!["logicalPath"] = "/private/image.png"; break;
            case "wrongName": wire["background"]!["media"]!["originalName"] = "../image.png"; break;
            case "wrongSize": wire["background"]!["media"]!["byteLength"] = image.Length + 1; break;
            case "wrongHash":
                string changedHash = new('b', 64);
                wire["background"]!["media"]!["hash"] = changedHash;
                wire["background"]!["logicalPath"] = "data/media/" + changedHash + ".media";
                break;
            case "wrongBytes": image[0] ^= 1; wire["imageBytes"] = Convert.ToBase64String(image); break;
            case "wrongImageHash": wire["imageSha256"] = new string('c', 64); break;
            case "wrongEngine": wire["engineIdentity"] = "old"; break;
            case "wrongProfile": wire["profile"] = "unknown"; break;
            case "wrongBaseProfile": wire["baseProfile"] = StaticProjectSnapshotCodec.PresetsProfile; break;
            case "wrongFingerprint": wire["sourceEffectInputsFingerprint"] = "sha256:" + new string('d', 64); break;
        }
        AssertCode(code, () => ProjectImagePreviewSnapshotCodec.Decode(System.Text.Encoding.UTF8.GetBytes(wire.ToJsonString())));
    }

    [Fact]
    public void 原图上限与包含双层base64的完整封包上限都明确拒绝()
    {
        byte[] oversized = new byte[ProjectImagePreviewSnapshotCodec.MaximumImageBytes + 1];
        AssertCode("SnapshotSizeExceeded", () => ProjectImagePreviewSnapshotCodec.Encode(ImageSource(oversized), oversized));
        byte[] max = new byte[ProjectImagePreviewSnapshotCodec.MaximumImageBytes];
        ProjectSnapshot source = ImageSource(max);
        source = source with { Name = new string('x', 1_100_000) };
        AssertCode("SnapshotSizeExceeded", () => ProjectImagePreviewSnapshotCodec.Encode(source, max));
        AssertCode("SnapshotSizeExceeded", () => ProjectImagePreviewSnapshotCodec.Decode(new byte[ProjectImagePreviewSnapshotCodec.MaximumBytes + 1]));
        AssertCode("SnapshotSizeExceeded", () => ProjectImagePreviewSnapshotCodec.Decode(ReadOnlyMemory<byte>.Empty));
    }

    private static ProjectSnapshot ImageSource(byte[] bytes) => StaticSnapshotFixture.Create() with
    {
        Background = BackgroundSource.ForImage("/private/image.png") with
        { Media = new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "image.png") },
    };

    private static byte[] Png()
    {
        using var bitmap = new SkiaSharp.SKBitmap(2, 2); bitmap.Erase(new(50, 100, 150, 220));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void AssertCode(string code, Action operation)
    {
        var error = Assert.Throws<SnapshotContractException>(operation);
        Assert.Equal(code, error.Code);
    }
}
