using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

// Explicit validation readback only. None of this import/CPU comparison is in
// the production library, worker, transport or consumer release protocol.
internal static class ProductionGpuProbe
{
    private const ulong Budget = 192ul * 1024 * 1024;
    internal static async Task RunAsync()
    {
        List<object> checks = [];
        using (var device = VulkanSkiaRenderDevice.Create(true))
        {
            try
            {
            var target = new RenderTarget { PixelWidth = 96, PixelHeight = 64 };
            using var surface = device.CreateSurface(new(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
            Expect("GpuPreviewTargetNotInitialized", () => device.CopyGpuFrame(surface, target));
            Expect("GpuPreviewSizeUnsupported", () => device.InitializeGpuOutput(target with { PixelWidth = 63 }, Budget));
            Expect("GpuPreviewPoolBudgetExceeded", () => device.InitializeGpuOutput(target, Budget + 1));
            var pool = device.InitializeGpuOutput(target, Budget);
            Require(pool.SlotCount == 3 && pool.AllocationBytes == pool.RetainedAllocationBytes && pool.AllocationBytes <= Budget, "pool budget");
            foreach (bool srgb in new[] { false, true })
            {
                using var colorSpace = srgb ? SKColorSpace.CreateSrgb() : null;
                using var colored = device.CreateSurface(new(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace));
                for (int frame = 0; frame < 12; frame++)
                {
                    Draw(colored.Canvas, frame);
                    using var lease = device.CopyGpuFrame(colored, target);
                    using var baseline = new SKBitmap(new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace));
                    Require(colored.ReadPixels(baseline.Info, baseline.GetPixels(), baseline.RowBytes, 0, 0), "baseline validation readback");
                    byte[] expected = new byte[baseline.ByteCount];
                    Marshal.Copy(baseline.GetPixels(), expected, 0, expected.Length);
                    using (var exported = lease.ExportForDelivery())
                    {
                        Require(exported.Descriptor.CpuPixelReadbacks == 0 && exported.Descriptor.GpuBlits > 0, "transport counters");
                        byte[] actual = ReadValidationPixels(device, exported);
                        Require(expected.AsSpan().SequenceEqual(actual), $"pixel parity srgb={srgb} frame={frame}");
                        Expect("GpuPreviewStaleLease", () => lease.ReleaseAfterConsumerCompletion(lease.Token with { FrameSequence = lease.Token.FrameSequence + 1 }));
                        Expect("GpuPreviewLeaseAlreadyExported", () => lease.ExportForDelivery());
                    }
                    // Test importer has waited for its actual GPU queue and destroyed
                    // all imported image/memory references. The exported FD is closed.
                    lease.ReleaseAfterConsumerCompletion(lease.Token);
                    lease.Dispose(); lease.Dispose();
                }
                checks.Add(new { check = "production-copy-byte-parity", srgb, frames = 12, validationOnlyReadbacks = 24 });
            }
            using var first = device.CopyGpuFrame(surface, target);
            using var second = device.CopyGpuFrame(surface, target);
            using var third = device.CopyGpuFrame(surface, target);
            Expect("GpuPreviewNoFreeSlot", () => device.CopyGpuFrame(surface, target));
            var cancelledToken = second.Token;
            second.Dispose();
            using var replacement = device.CopyGpuFrame(surface, target);
            Require(replacement.Token.SlotId == cancelledToken.SlotId && replacement.Token.SlotEpoch != cancelledToken.SlotEpoch, "cancel reuse changes epoch");
            var resized = device.InitializeGpuOutput(target with { PixelWidth = 128 }, Budget);
            Require(resized.PoolGeneration != pool.PoolGeneration && resized.RetainedAllocationBytes == resized.AllocationBytes + pool.AllocationBytes, "resize retained budget");
            using var resizedSurface = device.CreateSurface(new(128, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var retainedNew = device.CopyGpuFrame(resizedSurface, target with { PixelWidth = 128 });
            Expect("GpuPreviewPoolBudgetExceeded", () => device.InitializeGpuOutput(target with { PixelWidth = 160 }, Budget));
            checks.Add(new { check = "three-slots-cancel-exact-token-two-generations", passed = true });
            }
            catch (Exception error) { Console.Error.WriteLine("Primary probe error: " + error); throw; }
        }
        var snapshot = new ProjectSnapshot
        {
            Name = "production GPU session parity",
            Composition = Composition.Create(96, 64, FrameRate.Fps30, backgroundColor: new(12, 34, 56), outputDurationUs: 1_000_000),
            Lyrics = LyricsDocument.Create([LyricLine.Create("row", "GPU", new(0, 1_000_000), TimingStatus.Edited)]),
            LyricStyle = LyricStyle.Default with { FontSizePx = 22, Stroke = null, Shadow = null },
        };
        var factory = new ProjectRenderSessionFactory("unused-refs", "unused-cache", new FfmpegVideoFrameProvider(), ReplayReason.Initial,
            renderBackend: "vulkan", enableGpuPreview: true);
        await using (var session = await factory.CreateAsync(snapshot))
        {
            var gpu = (IGpuRenderFrameSession)session;
            var request = RenderRequest.At(snapshot, 400_000);
            await gpu.InitializeGpuOutputAsync(request.ResolveTarget(), Budget);
            using var cpu = await session.RenderAsync(request);
            using var output = await gpu.RenderGpuAsync(request);
            var sessionDevice = session.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(field => field.GetValue(session)).OfType<VulkanSkiaRenderDevice>().Single();
            using (var exported = output.Lease.ExportForDelivery())
                Require(cpu.PixelMemory.Span.SequenceEqual(ReadValidationPixels(sessionDevice, exported)), "real ProjectRenderSession parity");
            output.Lease.ReleaseAfterConsumerCompletion(output.Lease.Token);
            Require(cpu.ImageEffectDiagnostics.SequenceEqual(output.ImageEffectDiagnostics) && cpu.MaterialDiagnostics.SequenceEqual(output.MaterialDiagnostics), "session diagnostics");
            try { await gpu.RenderSyntheticGpuAsync(request.ResolveTarget(), 0, 1); throw new Exception("synthetic allowed without benchmark flag"); }
            catch (GpuPreviewException error) when (error.Code == "GpuPreviewSyntheticDisabled") { }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await gpu.RenderGpuAsync(request, cancelled.Token); throw new Exception("cancelled GPU request rendered"); }
            catch (OperationCanceledException) { }
            try { await gpu.RenderGpuAsync(request with { Project = snapshot with { Name = "different fixed input" } }); throw new Exception("foreign snapshot rendered"); }
            catch (PreparedFrameException error) when (error.Code == "RenderSessionInputsMismatch") { }
            await session.DisposeAsync();
            await session.DisposeAsync();
            try { await gpu.RenderGpuAsync(request); throw new Exception("disposed GPU session rendered"); }
            catch (ObjectDisposedException) { }
            checks.Add(new { check = "real-project-session-pixel-diagnostics-parity-synthetic-gate", passed = true });
        }
        using (var device = VulkanSkiaRenderDevice.Create(true))
        {
            var renderer = new SkiaRenderPipeline(null, null, null, null, false, device, false);
            var source = new RecordingSource(snapshot, renderer);
            await using var session = new ProjectRenderSession(snapshot, renderer, source, null, [], [], null, null, device, true);
            await session.InitializeGpuOutputAsync(new() { PixelWidth = 96, PixelHeight = 64 }, Budget);
            foreach (bool transparent in new[] { false, true })
            {
                var request = RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent };
                using var cpu = await session.RenderAsync(request);
                using var gpu = await session.RenderGpuAsync(request);
                using (var exported = gpu.Lease.ExportForDelivery())
                    Require(cpu.PixelMemory.Span.SequenceEqual(ReadValidationPixels(device, exported)), "prepared output pixel parity");
                gpu.Lease.ReleaseAfterConsumerCompletion(gpu.Lease.Token);
                Require(source.LastMode == (transparent ? OutputMode.Transparent : OutputMode.Opaque) && source.LastTarget == request.ResolveTarget() && source.LastTime == request.TimeUs,
                    "PrepareAsync exact time/output/target");
                Require(cpu.DiagnosticSnapshot.ToImmutableArray().SequenceEqual(gpu.DiagnosticSnapshot.ToImmutableArray()) && gpu.DiagnosticSnapshot.ToImmutableArray().Length == 1,
                    "prepared warning diagnostic parity");
            }
            int preparations = source.Calls;
            using (var synthetic = await session.RenderSyntheticGpuAsync(new() { PixelWidth = 96, PixelHeight = 64 }, 0, ulong.MaxValue))
            {
                using (var exported = synthetic.Lease.ExportForDelivery())
                {
                    byte[] pixels = ReadValidationPixels(device, exported);
                    for (int bit = 0; bit < 16; bit++)
                        Require(pixels[(bit * 96 / 16) * 4] == (((synthetic.Lease.Token.FrameSequence >> bit) & 1) == 0 ? 0 : 255), "synthetic identity equals lease");
                }
                synthetic.Lease.ReleaseAfterConsumerCompletion(synthetic.Lease.Token);
            }
            Require(source.Calls == preparations, "synthetic must skip PrepareAsync");
            checks.Add(new { check = "prepared-mode-target-diagnostics-parity-and-synthetic-skips-prepare", passed = true });
        }
        var quarantine = VulkanSkiaRenderDevice.Create(true);
        var qtarget = new RenderTarget { PixelWidth = 96, PixelHeight = 64 };
        quarantine.InitializeGpuOutput(qtarget, Budget);
        using (var surface = quarantine.CreateSurface(new(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul)))
        using (var lease = quarantine.CopyGpuFrame(surface, qtarget))
        {
            using var exported = lease.ExportForDelivery();
            lease.Dispose();
            Require(!quarantine.SupportsGpuFrames, "unknown export must fail stop");
        }
        Expect("GpuPreviewNotDrained", quarantine.Dispose);
        checks.Add(new { check = "unknown-export-quarantine-not-drained", passed = true });
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks, validationOnlyReadbacks = 55,
            consumerCompletionEvidence = "test-only Vulkan import, GPU staging-copy queue idle, imported image/memory destroy and FD close before exact release; Electron proof remains separate" }));
    }

    private static void Draw(SKCanvas canvas, int frame)
    {
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        (SKRect rect, SKColor color)[] patches =
        [
            (new(0, 0, 48, 24), SKColors.Red), (new(48, 0, 96, 24), SKColors.Lime),
            (new(0, 40, 48, 64), SKColors.Blue), (new(48, 40, 96, 64), SKColors.White),
            (new(0, 24, 24, 40), new(255, 0, 255, 128)), (new(24, 24, 48, 40), SKColors.Transparent),
            (new(48, 24, 72, 40), new(37, 37, 37)), (new(72, 24, 96, 40), new((byte)(frame * 19), 0, 0)),
        ];
        foreach (var patch in patches) { paint.Color = patch.color; canvas.DrawRect(patch.rect, paint); }
    }

    private static byte[] ReadValidationPixels(VulkanSkiaRenderDevice device, GpuFrameExport exported)
    {
        var desc = exported.Descriptor;
        lock (device.SynchronizationRoot)
        {
            byte[] result = new byte[checked(desc.Width * desc.Height * 4)];
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var physical = (SharpVk.PhysicalDevice)device.GetType().GetField("_physicalDevice", flags)!.GetValue(device)!;
            var nativeDevice = (SharpVk.Device)device.GetType().GetField("_device", flags)!.GetValue(device)!;
            var queue = (SharpVk.Queue)device.GetType().GetField("_queue", flags)!.GetValue(device)!;
            uint family = (uint)device.GetType().GetField("_queueFamily", flags)!.GetValue(device)!;
            var error = new StringBuilder(256);
            Require(ImportRead((IntPtr)physical.RawHandle.ToUInt64(), (IntPtr)nativeDevice.RawHandle.ToUInt64(),
                (IntPtr)queue.RawHandle.ToUInt64(), family, exported.FileDescriptor.DangerousGetHandle().ToInt32(),
                (uint)desc.Width, (uint)desc.Height, desc.PlaneStride, desc.PlaneOffset, desc.AllocationSize,
                desc.QueriedSubresourceSize, desc.Modifier, result, error, 256) == 0, "validation Vulkan import: " + error);
            return result;
        }
    }
    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
    private sealed class RecordingSource(ProjectSnapshot snapshot, SkiaRenderPipeline renderer) : IEvaluatedFrameSource
    {
        internal int Calls;
        internal long LastTime;
        internal OutputMode LastMode;
        internal RenderTarget? LastTarget;
        public ValueTask<PreparedRenderFrame> PrepareAsync(long timeUs, OutputMode outputMode, RenderTarget target, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++; LastTime = timeUs; LastMode = outputMode; LastTarget = target;
            return ValueTask.FromResult(new PreparedRenderFrame(snapshot, timeUs, outputMode, target, renderer.GetSceneFrame(snapshot, timeUs), [], [],
                [new(new("test-source"), "test-artifact", "Prepare", timeUs, new(DiagnosticSeverity.Warning, "GpuProbeWarning", "prepared warning preserved"))]));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (GpuPreviewException error) when (error.Code == code) { return; }
        throw new Exception("Expected " + code);
    }
    [DllImport("mako-gpu-validation", EntryPoint = "mako_validation_import_read", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ImportRead(IntPtr physical, IntPtr device, IntPtr queue, uint family, int fd, uint width, uint height,
        uint stride, ulong offset, ulong allocationBytes, ulong subresourceBytes, ulong modifier, [Out] byte[] destination, StringBuilder error, nuint errorSize);
}
