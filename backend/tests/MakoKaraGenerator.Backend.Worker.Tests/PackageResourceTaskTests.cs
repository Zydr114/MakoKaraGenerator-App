using System.Diagnostics;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Worker;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class PackageResourceTaskTests
{
    [Fact]
    public void CapabilitiesDeclareImageVideoAndFontProfiles()
    {
        PackageResourceWorkerCapabilities capabilities = PackageResourceTask.Capabilities();
        PackageResourceWorkerContract.ValidateCapabilities(capabilities);
        Assert.Contains(PackageResourceWorkerContract.ImageDecodeProfile, capabilities.ImageProfiles);
        Assert.Contains(PackageResourceWorkerContract.VideoMetadataProfile, capabilities.VideoProfiles);
        Assert.Contains(PackageResourceWorkerContract.VideoFrameProfile, capabilities.VideoProfiles);
        Assert.Contains(PackageResourceWorkerContract.FontShapeProfile, capabilities.FontProfiles);
        Assert.Equal(PackageResourceWorkerContract.FontProviderIdentity, capabilities.FontProviderIdentity);
    }

    [Fact]
    public void ImageGrantIsDecodedAndHashedThroughWorkerPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "mako-resource-image-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using SKBitmap bitmap = new(4, 3, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKCanvas canvas = new(bitmap); canvas.Clear(SKColors.Transparent);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            byte[] bytes = File.ReadAllBytes(path);
            PackageResourceWorkerGrant grant = new("g1", "p", "r", "assets/a.png", "image", path,
                bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            PackageResourceInspection result = PackageResourceTask.Validate(grant, CancellationToken.None);
            Assert.Equal("image", result.Role);
            Assert.Equal(4, result.Image!.Width);
            Assert.Equal(3, result.Image.Height);
            Assert.True(result.Image.HasAlpha);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ChangedGrantFileIsRejectedBeforeDecode()
    {
        string path = Path.Combine(Path.GetTempPath(), "mako-resource-image-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            PackageResourceWorkerGrant grant = new("g1", "p", "r", "assets/a.png", "image", path, 3, new string('0', 64));
            WorkerProtocolException error = Assert.Throws<WorkerProtocolException>(() => PackageResourceTask.Validate(grant, CancellationToken.None));
            Assert.Equal("WorkerResourceGrantChanged", error.Code);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task VideoGrantReportsMetadataAndStableFirstFrameDigest()
    {
        string path = Path.Combine(Path.GetTempPath(), "mako-resource-video-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            ProcessStartInfo start = new("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
            foreach (string arg in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=32x24:rate=5:duration=1",
                "-an", "-c:v", "mpeg4", "-y", path }) start.ArgumentList.Add(arg);
            using (Process process = Process.Start(start)!)
            {
                string errors = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, errors);
            }
            byte[] bytes = await File.ReadAllBytesAsync(path);
            PackageResourceWorkerGrant grant = new("g-video", "p", "r", "assets/background.mp4", "video", path,
                bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            PackageResourceInspection result = PackageResourceTask.Validate(grant, CancellationToken.None);
            Assert.Equal("video", result.Role);
            Assert.True(result.Video!.DurationUs > 0);
            Assert.Equal(32, result.Video.Width);
            Assert.Equal(24, result.Video.Height);
            Assert.Equal("mpeg4", result.Video.Codec);
            Assert.False(result.Video.HasAudio);
            Assert.Equal(32 * 24 * 4, result.Video.DecodedFrameBytes);
            Assert.Matches("^[0-9a-f]{64}$", result.Video.FrameSha256);
            Assert.Equal(0, result.Video.FrameTimeUs);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void FontGrantBuildsProviderAndShapesFixedText()
    {
        string fontPath = FindFontPath();
        byte[] bytes = File.ReadAllBytes(fontPath);
        PackageResourceWorkerGrant grant = new("g-font", "p", "r", "fonts/DejaVuSans.ttf", "font", fontPath,
            bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        PackageResourceInspection result = PackageResourceTask.Validate(grant, CancellationToken.None);
        Assert.Equal("font", result.Role);
        Assert.NotEmpty(result.Font!.FamilyNames);
        Assert.True(result.Font.GlyphCount > 0);
        Assert.Equal(PackageResourceWorkerContract.FontShapeProfile, result.Font.ShapingProfile);
        Assert.True(result.Font.ShapedAdvancePx > 0);
    }

    [Fact]
    public void UnknownRoleRemainsExplicitlyUnsupported()
    {
        string path = Path.Combine(Path.GetTempPath(), "mako-resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] bytes = [1, 2, 3];
            File.WriteAllBytes(path, bytes);
            PackageResourceWorkerGrant grant = new("g1", "p", "r", "assets/resource", "audio", path,
                bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            WorkerProtocolException error = Assert.Throws<WorkerProtocolException>(() => PackageResourceTask.Validate(grant, CancellationToken.None));
            Assert.Equal("WorkerResourceGrantInvalid", error.Code);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task ExternalConcatInputCannotReadAnUngrantVideo()
    {
        string root = Path.Combine(Path.GetTempPath(), "mako-resource-concat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string video = Path.Combine(root, "ungranted.mp4");
            ProcessStartInfo start = new("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
            foreach (string arg in new[] { "-v", "error", "-f", "lavfi", "-i", "color=red:size=16x12:rate=2:duration=1",
                "-an", "-c:v", "mpeg4", "-y", video }) start.ArgumentList.Add(arg);
            using (Process process = Process.Start(start)!)
            {
                string errors = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, errors);
            }
            string path = Path.Combine(root, "resource.bin");
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("ffconcat version 1.0\nfile 'ungranted.mp4'\n");
            await File.WriteAllBytesAsync(path, bytes);
            PackageResourceWorkerGrant grant = new("g-concat", "p", "r", "assets/video.mp4", "video", path,
                bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            WorkerProtocolException error = Assert.Throws<WorkerProtocolException>(() => PackageResourceTask.Validate(grant, CancellationToken.None));
            Assert.Equal("WorkerResourceVideoInvalid", error.Code);
            Assert.DoesNotContain(root, error.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string FindFontPath()
    {
        string[] roots = ["/usr/share/fonts", "/usr/local/share/fonts", Environment.GetFolderPath(Environment.SpecialFolder.Fonts)];
        string? path = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .FirstOrDefault(file => file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
        return path ?? throw new FileNotFoundException("测试需要可读取的系统字体文件。");
    }
}
