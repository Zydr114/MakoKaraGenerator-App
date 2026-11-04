using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private SvgAtlasPage AtlasPage(Fx.AtlasSprite sprite) => _svgEffects?.GetSprite(sprite.Sprite, _device)
        ?? throw new PreparedFrameException("SvgResourcesRequired", "SVG atlas 需要当前场景的固定包资源。");
    private void ValidateNativeAtlasSprite(Fx.AtlasSprite sprite)
    {
        ValidateNativeUnitAffine(sprite.UnitToWorld);
        var page = AtlasPage(sprite); var content = page.ContentRect;
        if (!FiniteSvgBounds(content) || content.Left < 1 || content.Top < 1 || content.Right > page.Image.Width - 1
            || content.Bottom > page.Image.Height - 1 || page.PaddingPx < 1 || page.Image.Handle == nint.Zero)
            throw new PreparedFrameException("SvgAtlasInvalid", "固定 SVG atlas 必须具备完整页、真实内容区域及透明 padding。");
        if (_device is not CpuSkiaRenderDevice && (!page.MipmapsRequested || !page.IsTextureBacked || !ReferenceEquals(page.Device, _device)))
            throw new PreparedFrameException("SvgAtlasMipUnavailable", "当前 GPU 设备缺少真实上传的 mip atlas，不能降成普通图片。");
        foreach (var point in SvgCorners(AtlasPageInUnit(page))) NativePoint(sprite.UnitToWorld.Map(point), 0, positive: false);
    }
    private static SKRect AtlasPageInUnit(SvgAtlasPage page)
        => new(-page.ContentRect.Left / page.ContentRect.Width, -page.ContentRect.Top / page.ContentRect.Height,
            (page.Image.Width - page.ContentRect.Left) / page.ContentRect.Width, (page.Image.Height - page.ContentRect.Top) / page.ContentRect.Height);
    private void DrawAtlasSprites(SKCanvas canvas, Fx.AtlasSpriteBatch batch, RenderRequest request, SvgRenderWorkBudget work)
    {
        using SKPaint paint = new() { IsAntialias = request.Quality != RenderQuality.Draft, Color = SKColors.White };
        foreach (var sprite in batch.Sprites)
        {
            request.CancellationToken.ThrowIfCancellationRequested(); work.Charge(1, 0);
            var page = AtlasPage(sprite);
            _ = SvgPhysicalMatrix(canvas.TotalMatrix, sprite.UnitToWorld);
            // Draw the complete independent page, then clip the author's unit content. Keeping
            // padding in the sampled image preserves transparent edge texels and full affine LOD
            // derivatives; no per-pixel clamp or similarity-only DrawAtlas transform is used.
            SKRect pageInUnit = AtlasPageInUnit(page);
            using var filter = SvgTintFilter(sprite.Tint); paint.ColorFilter = filter;
            canvas.Save();
            try
            {
                canvas.Concat(UnitOverlayMatrix(sprite.UnitToWorld));
                canvas.ClipRect(new(0, 0, 1, 1));
                canvas.DrawImage(page.Image, pageInUnit, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            }
            finally { canvas.Restore(); paint.ColorFilter = null; }
            request.CancellationToken.ThrowIfCancellationRequested();
        }
    }
}
