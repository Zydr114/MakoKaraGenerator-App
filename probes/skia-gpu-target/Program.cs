using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

using var device = VulkanSkiaRenderDevice.Create();
var context = (GRContext)typeof(VulkanSkiaRenderDevice).GetField("_context", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(device)!;
var output = new List<object>();
foreach (bool srgb in new[] { false, true })
{
using var colorSpace = srgb ? SKColorSpace.CreateSrgb() : null;
using var surface = device.CreateSurface(new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace));
ulong image = 0;
for (int iteration = 0; iteration < 12; iteration++)
{
    surface.Canvas.Clear(iteration % 2 == 0 ? SKColors.Red : SKColors.Blue);
    using var paint = new SKPaint { Color = SKColors.Lime, BlendMode = SKBlendMode.Src };
    surface.Canvas.DrawRect(0, 0, 24, 24, paint);
    // TRANSFER_SRC_OPTIMAL and IGNORED leave the graphics queue ownership intact.
    if (!Bridge.Flush(context.Handle, surface.Handle, 6, uint.MaxValue)) throw new Exception("flush/submit failed");
    if (!Bridge.GetInfo(surface.Handle, out GRVkImageInfo info)) throw new Exception("Vulkan image query failed");
    if (info.ImageLayout != 6 || info.SampleCount != 1 || info.Image == 0) throw new Exception("unexpected Vulkan image state");
    if (iteration > 0 && info.Image != image) throw new Exception("surface unexpectedly changed image");
    image = info.Image;
    if (!Bridge.Submit(context.Handle, true)) throw new Exception("info flush submit failed");
    // Validation only: this readback is deliberately outside the shared transport probe.
    using var bitmap = new SKBitmap(new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
    if (!surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) throw new Exception("validation readback failed");
    if (bitmap.GetPixel(4, 4) != SKColors.Lime || bitmap.GetPixel(80, 40) != (iteration % 2 == 0 ? SKColors.Red : SKColors.Blue))
        throw new Exception("rendered pixels mismatch");
    output.Add(new { srgb, origin = "BottomLeft (production default)", iteration, layout = info.ImageLayout, queue = info.CurrentQueueFamily, singleSample = info.SampleCount == 1 });
}
}
Console.WriteLine(JsonSerializer.Serialize(new { nativeVersion = SkiaSharpVersion.Native.ToString(), managedVersion = typeof(SKSurface).Assembly.GetName().Version!.ToString(), device = device.Info, iterations = output, validationOnlyReadbacks = 24, passed = true }));

static class Bridge
{
    [DllImport("libSkiaSharp", EntryPoint = "gr_mako_direct_context_flush_surface_vk_state", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool Flush(IntPtr context, IntPtr surface, uint layout, uint family);
    [DllImport("libSkiaSharp", EntryPoint = "gr_direct_context_submit", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool Submit(IntPtr context, [MarshalAs(UnmanagedType.I1)] bool syncCpu);
    [DllImport("libSkiaSharp", EntryPoint = "sk_mako_surface_get_vk_image_info", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool GetInfo(IntPtr surface, out GRVkImageInfo info);
}

namespace MakoKaraGenerator.Rendering.Skia
{
    // The linked production device is unchanged. This standalone probe disables only telemetry.
    internal enum PerformancePhase { Readback }
    internal static class PerformanceTelemetry
    {
        internal static bool HasFrame => false;
        internal static void RecordReadback(bool image, long bytes) { }
        internal static IDisposable Measure(PerformancePhase phase) => new EmptyScope();
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    }
}
