using MakoKaraGenerator.Scripting.Compilation;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using SkiaSharp;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class RubySpacingWorkerIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task Wide_ruby_matches_real_static_worker_pixels_after_seek(bool transparent)
        => VerifyWideRubyWorkerPixelsAsync(package: false, transparent);

    [Theory]
    [Trait("Category", "RequiresVulkan")]
    [InlineData(false)] [InlineData(true)]
    public Task Wide_ruby_matches_real_package_worker_pixels_after_seek(bool transparent)
        => VerifyWideRubyWorkerPixelsAsync(package: true, transparent);

    private async Task VerifyWideRubyWorkerPixelsAsync(bool package, bool transparent)
    {
        PackageWorkerRuntimeFixture.AssertFixedFont();
        var source = RubySpacingFixture.Create(width: 320);
        if (package)
        {
            var files = EffectPackageFixture.Files();
            var contents = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)),
                EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
            var original = PackageWorkerRuntimeFixture.Create("scale").Effects.Instances[0];
            source = PackageWorkerRuntimeFixture.Fingerprint(source with { Effects = new([contents],
                [original with { PackageId = contents.PackageId, Revision = contents.Revision,
                    Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token-0", "token-1", "token-2"]) }]) });
        }
        byte[] bytes = package ? PackageProjectSnapshotCodec.Encode(source) : StaticProjectSnapshotCodec.Encode(source);
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var factory = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos, renderBackend: package ? "vulkan" : null);
        await using var direct = await factory.CreateAsync(source);
        await using WorkerProcessProxy worker = new(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = package ? "gpu" : null });
        var handle = package ? await worker.OpenPackageSessionAsync(bytes, TimeSpan.FromSeconds(30)) : await worker.OpenStaticSessionAsync(bytes, TimeSpan.FromSeconds(30));
        int pid = worker.ProcessId!.Value; long startIdentity = RuntimeTestPlatformHelpers.GetStartIdentityUtc(pid);
        OwnedProcessGroup? group = null;
        if (OperatingSystem.IsLinux()) { using var process = Process.GetProcessById(pid); group = OwnedProcessGroup.TryCapture(process); Assert.NotNull(group); }
        long sequence = 0; byte[]? first = null;
        foreach (long time in new long[] { 150_000, 250_000, 800_000, 150_000 })
        {
            var request = RenderRequest.At(source, time) with { Target = new() { PixelWidth = 320, PixelHeight = 240 }, TransparentBackground = transparent };
            using var expected = await direct.RenderAsync(request);
            var input = new StaticWorkerFrameRequest(handle.SessionId, handle.SourceSha256, 320, 240, time, transparent, ++sequence);
            var actual = package ? await worker.RenderPackageSessionAsync(input, TimeSpan.FromSeconds(20)) : await worker.RenderStaticSessionAsync(input, TimeSpan.FromSeconds(20));
            Assert.Equal(expected.PixelMemory.ToArray(), actual.Payload);
            using var transported = new RenderFrame(actual.Payload, expected.Target, expected.Format, time);
            Assert.Equal(Png(expected), Png(transported));
            if (time == 150_000) { if (first is null) first = actual.Payload; else Assert.Equal(first, actual.Payload); }
        }
        if (package) await worker.ClosePackageSessionAsync(handle, ++sequence, TimeSpan.FromSeconds(20));
        else await worker.CloseStaticSessionAsync(handle, ++sequence, TimeSpan.FromSeconds(20));
        await worker.StopAsync(); if (group is not null) Assert.Empty(group.ObserveMembers());
        Assert.False(RuntimeTestPlatformHelpers.IsProcessAlive(pid, startIdentity));
        output.WriteLine($"Wide ruby Worker {pid}; package={package}; transparent={transparent}; four BGRA/PNG frames byteexact; group gone.");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Wide_ruby_same_frame_measurement_has_direct_geometry_and_png(bool transparent)
    {
        var source = RubySpacingFixture.Create(width: 320); byte[] bytes = StaticProjectSnapshotCodec.Encode(source);
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var factory = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos);
        await using var direct = await factory.CreateAsync(source);
        var measurable = Assert.IsAssignableFrom<ISceneMeasurementSession>(direct);
        foreach (long time in new long[] { 150_000, 250_000 })
        {
            await using WorkerProcessProxy worker = new(new(PackageWorkerRuntimeFixture.WorkerApphost()));
            var request = new SceneMeasurementRequest("0", "time", time.ToString(CultureInfo.InvariantCulture), 320, 240, transparent ? "transparent" : "opaque");
            var input = new WorkerSceneMeasurementInput(request, WorkerProtocol.ComputeSha256(bytes), string.Empty);
            input = input with { SampleInputFingerprint = WorkerSceneMeasurementContract.Fingerprint(bytes, input) };
            var actual = (await worker.MeasureSceneAsync(input, bytes)).Measurement;
            int pid = worker.ProcessId!.Value; long startIdentity = RuntimeTestPlatformHelpers.GetStartIdentityUtc(pid);
            OwnedProcessGroup? group = null;
            if (OperatingSystem.IsLinux()) { using var process = Process.GetProcessById(pid); group = OwnedProcessGroup.TryCapture(process); Assert.NotNull(group); }
            using var expected = await measurable.MeasureAsync(RenderRequest.At(source, time) with { Target = new() { PixelWidth = 320, PixelHeight = 240 }, TransparentBackground = transparent }, false);
            Assert.Equal(Png(expected.Frame), actual.Png);
            Assert.Equal(WorkerProtocol.ComputeSha256(actual.Png), actual.PngSha256);
            Assert.Equal(input.SampleInputFingerprint, actual.SampleInputFingerprint);
            foreach (var ruby in actual.Elements.Where(e => e.RubyId is not null))
            {
                var comparison = Assert.Single(expected.Scene.Elements, e => e.SourceBinding.RubyId == ruby.RubyId);
                Assert.Equal(comparison.WorldCorners.Select(p => new ProjectGeometryPointDto(p.X, p.Y)), ruby.WorldCorners);
                Assert.True(ruby.Visible); Assert.Equal("measuredInk", ruby.GeometryStatus);
            }
            Assert.Equal(3, actual.Elements.Count(e => e.RubyId is not null));
            await worker.StopAsync(); if (group is not null) Assert.Empty(group.ObserveMembers());
            Assert.False(RuntimeTestPlatformHelpers.IsProcessAlive(pid, startIdentity));
            output.WriteLine($"Wide ruby geometry/PNG Worker {pid}; transparent={transparent}; time={time}; group gone.");
        }
    }

    private static byte[] Png(RenderFrame frame)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var pixels = frame.PixelMemory.ToArray(); Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
