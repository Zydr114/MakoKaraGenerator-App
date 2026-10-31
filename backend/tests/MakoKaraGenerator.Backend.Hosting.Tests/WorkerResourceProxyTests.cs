using System.Diagnostics;
using System.Reflection;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Contracts;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerResourceProxyTests
{
    [Fact]
    public async Task Package_capability_process_markers_are_profile_specific_and_stop_requires_resource_rehandshake()
    {
        string worker = FindWorkerApphost();
        await using WorkerProcessProxy proxy = new(new WorkerProcessOptions(worker, cancellationGracePeriod: TimeSpan.FromSeconds(2)));

        await proxy.GetPackageCapabilitiesAsync(TimeSpan.FromSeconds(30));
        Process renderProcess = Assert.IsType<Process>(PrivateProcess(proxy, "_packageRenderVerifiedProcess"));
        Assert.Null(PrivateProcess(proxy, "_packageValidationVerifiedProcess"));
        Assert.Null(PrivateProcess(proxy, "_packageResourceVerifiedProcess"));
        Assert.Null(VerifiedProcess(proxy, PackageResourceWorkerContract.ValidateKind));

        await proxy.GetPackageValidationCapabilitiesAsync(TimeSpan.FromSeconds(30));
        Assert.Same(renderProcess, PrivateProcess(proxy, "_packageRenderVerifiedProcess"));
        Assert.Same(renderProcess, PrivateProcess(proxy, "_packageValidationVerifiedProcess"));
        Assert.Null(PrivateProcess(proxy, "_packageResourceVerifiedProcess"));
        Assert.Null(VerifiedProcess(proxy, PackageResourceWorkerContract.ValidateKind));

        await proxy.GetPackageResourceCapabilitiesAsync(TimeSpan.FromSeconds(30));
        Process resourceProcess = Assert.IsType<Process>(PrivateProcess(proxy, "_packageResourceVerifiedProcess"));
        Assert.Same(renderProcess, resourceProcess);
        Assert.Same(renderProcess, PrivateProcess(proxy, "_packageValidationVerifiedProcess"));
        Assert.Same(resourceProcess, VerifiedProcess(proxy, PackageResourceWorkerContract.ValidateKind));

        int oldPid = resourceProcess.Id;
        await proxy.StopAsync();
        Assert.Null(PrivateProcess(proxy, "_packageRenderVerifiedProcess"));
        Assert.Null(PrivateProcess(proxy, "_packageValidationVerifiedProcess"));
        Assert.Null(PrivateProcess(proxy, "_packageResourceVerifiedProcess"));

        await proxy.GetPackageResourceCapabilitiesAsync(TimeSpan.FromSeconds(30));
        Process restartedResourceProcess = Assert.IsType<Process>(PrivateProcess(proxy, "_packageResourceVerifiedProcess"));
        Assert.NotEqual(oldPid, restartedResourceProcess.Id);
        Assert.Null(PrivateProcess(proxy, "_packageRenderVerifiedProcess"));
        Assert.Null(PrivateProcess(proxy, "_packageValidationVerifiedProcess"));
        await proxy.StopAsync();
    }

    [Fact]
    public async Task RealWorkerValidatesStagedImageWithoutExposingHostPathInResult()
    {
        string root = FindRepositoryRoot();
        string worker = Path.Combine(root, "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker.dll");
        Assert.True(File.Exists(worker), worker);
        byte[] png;
        using (SKBitmap bitmap = new(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul))
        using (SKImage image = SKImage.FromBitmap(bitmap))
        using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100)) png = data.ToArray();
        string stage = Path.Combine(Path.GetTempPath(), "mako-resource-proxy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using WorkerResourceLeaseStore store = new(stage);
            using WorkerResourceLease lease = await store.StageAsync("pkg", "rev", "assets/image.png", "image", png);
            await using WorkerProcessProxy proxy = new(new WorkerProcessOptions("dotnet", [worker], TimeSpan.FromSeconds(2)));
            PackageResourceWorkerCapabilities capabilities = await proxy.GetPackageResourceCapabilitiesAsync(TimeSpan.FromSeconds(20));
            Assert.Contains(PackageResourceWorkerContract.ImageDecodeProfile, capabilities.ImageProfiles);
            Assert.Contains(PackageResourceWorkerContract.VideoMetadataProfile, capabilities.VideoProfiles);
            Assert.Contains(PackageResourceWorkerContract.VideoFrameProfile, capabilities.VideoProfiles);
            Assert.Contains(PackageResourceWorkerContract.FontShapeProfile, capabilities.FontProfiles);
            Assert.Equal(PackageResourceWorkerContract.FontProviderIdentity, capabilities.FontProviderIdentity);
            PackageResourceInspection inspection = await proxy.ValidatePackageResourceAsync(lease.Grant, TimeSpan.FromSeconds(20));
            Assert.Equal("image", inspection.Role);
            Assert.Equal(2, inspection.Image!.Width);
            Assert.Equal(2, inspection.Image.Height);
            Assert.DoesNotContain(lease.Grant.FilePath, inspection.ToString(), StringComparison.Ordinal);
            await proxy.StopAsync();
        }
        finally { try { Directory.Delete(stage, true); } catch { } }
    }

    [Fact]
    public async Task RealWorkerValidatesStagedVideoAndFontWithoutHostPathLeak()
    {
        string root = FindRepositoryRoot();
        string worker = Path.Combine(root, "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker.dll");
        Assert.True(File.Exists(worker), worker);
        string stage = Path.Combine(Path.GetTempPath(), "mako-resource-proxy-" + Guid.NewGuid().ToString("N"));
        string video = Path.Combine(stage, "source.mp4");
        string font = FindFontPath();
        try
        {
            Directory.CreateDirectory(stage);
            ProcessStartInfo start = new("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
            foreach (string arg in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=16x12:rate=2:duration=1",
                "-an", "-c:v", "mpeg4", "-y", video }) start.ArgumentList.Add(arg);
            using (Process process = Process.Start(start)!)
            {
                string errors = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, errors);
            }
            byte[] videoBytes = await File.ReadAllBytesAsync(video);
            byte[] fontBytes = await File.ReadAllBytesAsync(font);
            using WorkerResourceLeaseStore store = new(Path.Combine(stage, "leases"));
            using WorkerResourceLease videoLease = await store.StageAsync("pkg", "rev", "assets/background.mp4", "video", videoBytes);
            using WorkerResourceLease fontLease = await store.StageAsync("pkg", "rev", "fonts/DejaVuSans.ttf", "font", fontBytes);
            await using WorkerProcessProxy proxy = new(new WorkerProcessOptions("dotnet", [worker], TimeSpan.FromSeconds(2)));
            PackageResourceInspection videoResult = await proxy.ValidatePackageResourceAsync(videoLease.Grant, TimeSpan.FromSeconds(30));
            Assert.Equal("video", videoResult.Role);
            Assert.Equal(16, videoResult.Video!.Width);
            Assert.Equal(12, videoResult.Video.Height);
            Assert.Equal(16 * 12 * 4, videoResult.Video.DecodedFrameBytes);
            Assert.DoesNotContain(videoLease.Grant.FilePath, videoResult.ToString(), StringComparison.Ordinal);
            PackageResourceInspection fontResult = await proxy.ValidatePackageResourceAsync(fontLease.Grant, TimeSpan.FromSeconds(30));
            Assert.Equal("font", fontResult.Role);
            Assert.NotEmpty(fontResult.Font!.FamilyNames);
            Assert.True(fontResult.Font.GlyphCount > 0);
            Assert.True(fontResult.Font.ShapedAdvancePx > 0);
            Assert.DoesNotContain(fontLease.Grant.FilePath, fontResult.ToString(), StringComparison.Ordinal);
            await proxy.StopAsync();
        }
        finally { try { Directory.Delete(stage, true); } catch { } }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MakoKaraGenerator.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }

    private static string FindWorkerApphost()
    {
        string path = Path.Combine(FindRepositoryRoot(), "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker");
        Assert.True(File.Exists(path), path);
        return path;
    }

    private static Process? PrivateProcess(WorkerProcessProxy proxy, string fieldName)
        => (Process?)typeof(WorkerProcessProxy).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(proxy);

    private static Process? VerifiedProcess(WorkerProcessProxy proxy, string kind)
        => (Process?)typeof(WorkerProcessProxy).GetMethod("VerifiedPackageProcess", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(proxy, [kind]);

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
