using System.Threading.Channels;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Rendering.Skia.Tests;

public sealed class SkiaBackendPreviewPngEncoderTests
{
    [Fact]
    public void RealBgraPremultipliedPixelsDecodeAsStraightRgbaIncludingTransparency()
    {
        SkiaBackendPreviewPngEncoder encoder = new();
        byte[] pixels = [0, 0, 255, 255, 32, 64, 96, 128, 0, 0, 0, 0];
        byte[] encoded = encoder.EncodePixels(pixels, 3, 1, 12, PixelFormat.Bgra8888Premultiplied);
        using SKCodec codec = SKCodec.Create(new MemoryStream(encoded));
        Assert.Equal(3, codec.Info.Width);
        Assert.Equal(1, codec.Info.Height);
        byte[] rgba = new byte[12];
        Assert.Equal(SKCodecResult.Success,
            codec.GetPixels(new SKImageInfo(3, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul), rgba));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[..4]);
        Assert.InRange(rgba[4], 190, 192);
        Assert.InRange(rgba[5], 127, 129);
        Assert.InRange(rgba[6], 63, 65);
        Assert.Equal(128, rgba[7]);
        Assert.Equal(new byte[4], rgba[8..]);
        Assert.Equal(new byte[] { 32, 64, 96, 128 }, pixels[4..8]);
        // A second frame must encode its actual pixels rather than a cached placeholder.
        byte[] blue = encoder.EncodePixels([255, 0, 0, 255], 1, 1, 4, PixelFormat.Bgra8888Premultiplied);
        using SKBitmap decoded = SKBitmap.Decode(blue);
        Assert.Equal(SKColors.Blue, decoded.GetPixel(0, 0));
    }

    [Theory]
    [InlineData(0, 1, 0, 0)]
    [InlineData(2, 1, 4, 8)]
    [InlineData(1, 2, 4, 4)]
    [InlineData(1, 1, 4, 5)]
    [InlineData(1921, 1, 7684, 0)]
    [InlineData(1, 1081, 4, 0)]
    public void RejectsInvalidDimensionsStrideOrExactByteCount(int width, int height, long stride, int bytes)
    {
        SkiaBackendPreviewPngEncoder encoder = new();
        Assert.Throws<ArgumentException>(() => encoder.EncodePixels(new byte[bytes], width, height, stride,
            PixelFormat.Bgra8888Premultiplied));
    }

    [Fact]
    public void RejectsUnsupportedFormatAndInputOrOutputBudgetBeforePublishing()
    {
        Assert.Throws<ArgumentNullException>(() => new SkiaBackendPreviewPngEncoder().Encode(null!));
        Assert.Throws<ArgumentException>(() => new SkiaBackendPreviewPngEncoder().EncodePixels(new byte[4],
            1, 1, 4, (PixelFormat)123));
        Assert.Throws<ArgumentException>(() => new SkiaBackendPreviewPngEncoder(maxPixelBytes: 3)
            .EncodePixels(new byte[4], 1, 1, 4, PixelFormat.Bgra8888Premultiplied));
        Assert.Throws<InvalidOperationException>(() => new SkiaBackendPreviewPngEncoder(maxEncodedBytes: 8)
            .EncodePixels(new byte[4], 1, 1, 4, PixelFormat.Bgra8888Premultiplied));
    }

    [Fact]
    public async Task PublicEncoderRejectsRenderedTargetDifferentFromPinnedRequest()
    {
        ProjectSnapshot project = ProjectSnapshot.FromProject(Project.Create("encoder",
            Composition.Create(1, 1, FrameRate.Fps30), LyricsDocument.Create([])));
        Channel<BackendPreviewFrame> frames = Channel.CreateUnbounded<BackendPreviewFrame>();
        await using BackendPreviewCoordinator coordinator = new();
        coordinator.Updated += update => { if (update.Frame is { } frame) frames.Writer.TryWrite(frame); };
        coordinator.Submit(new BackendPreviewRequest { PreviewGeneration = 1, SourceRevision = "1",
            SessionFactory = new MismatchedFactory(), RenderRequest = RenderRequest.At(project, 0) });
        BackendPreviewFrame actual = await frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<ArgumentException>(() => new SkiaBackendPreviewPngEncoder().Encode(actual));
    }

    private sealed class MismatchedFactory : IRenderFrameSessionFactory
    {
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRenderFrameSession>(new MismatchedSession());
    }

    private sealed class MismatchedSession : IRenderFrameSession
    {
        public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new RenderFrame(new byte[8], new RenderTarget { PixelWidth = 2, PixelHeight = 1 },
                PixelFormat.Bgra8888Premultiplied, request.TimeUs));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
