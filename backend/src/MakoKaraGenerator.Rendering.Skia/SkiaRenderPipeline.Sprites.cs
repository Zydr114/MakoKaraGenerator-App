using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private SkiaGraphSurface SpriteImage(Fx.Sprite sprite) => _imageEffects?.GetSpriteImage(sprite.Image)
        ?? throw new PreparedFrameException("SpriteResourcesRequired", "精灵需要帧外固定图片集合，不能在帧内读文件或忽略输出。");
    private static SKMatrix SpriteMatrix(Fx.Sprite sprite) => new((float)sprite.UnitToWorld.A, (float)sprite.UnitToWorld.C, (float)sprite.UnitToWorld.Tx,
        (float)sprite.UnitToWorld.B, (float)sprite.UnitToWorld.D, (float)sprite.UnitToWorld.Ty, 0, 0, 1);
    private static void ValidateNativeSprite(Fx.Sprite sprite)
    {
        var matrix = sprite.UnitToWorld;
        var values = new[] { matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty };
        if (values.Any(value => !float.IsFinite((float)value) || value != 0 && (float)value == 0)
            || !SpriteMatrix(sprite).TryInvert(out var inverse)
            || new[] { inverse.ScaleX, inverse.ScaleY, inverse.SkewX, inverse.SkewY, inverse.TransX, inverse.TransY }.Any(value => !float.IsFinite(value)))
            throw new PreparedFrameException("UnsupportedNativeOverlay", "精灵矩阵超出后端精度或不可逆。");
        foreach (var point in new Fx.Point2[] { new(0, 0), new(1, 0), new(0, 1), new(1, 1) }) NativePoint(matrix.Map(point), 0, positive: false);
        var source = sprite.Source;
        if ((float)source.Right <= (float)source.Left || (float)source.Bottom <= (float)source.Top)
            throw new PreparedFrameException("UnsupportedNativeOverlay", "精灵裁片低于后端精度，不能静默绘制空图。");
    }
    private static T SpriteOperation<T>(PreparedRenderFrame frame, Fx.SpriteBatch batch, Func<T> action)
    {
        try { return action(); }
        catch (OperationCanceledException) { throw; }
        catch (EffectFailureException) { throw; }
        catch (Exception error)
        {
            var owner = frame.OverlayOwners.SingleOrDefault(owner => owner.InstanceId == batch.InstanceId);
            if (owner is null) throw;
            string code = error switch { PreparedFrameException prepared => prepared.Code, ImageSurfaceBudgetException budget => budget.Code,
                ShaderProgramException shader => shader.Code, _ => "SpriteRenderFailed" };
            throw owner.Failure(frame.TimeUs, code, error);
        }
    }
    private SpriteImageLeases BorrowSpriteImages(RenderRequest request, ImageSurfaceBudgetFrame resources)
    {
        List<IDisposable> leases = [];
        try
        {
            if (request.PreparedFrame is { } frame)
                foreach (var batch in frame.OverlayBatches.OfType<Fx.SpriteBatch>())
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    leases.Add(SpriteOperation(frame, batch, () => resources.BorrowInputs(batch.Sprites.Select(SpriteImage))));
                }
            return new SpriteImageLeases(leases);
        }
        catch { foreach (var lease in leases) lease.Dispose(); throw; }
    }
    private void DrawSprites(SKCanvas canvas, Fx.SpriteBatch batch, RenderRequest request)
    {
        using SKPaint paint = new() { IsAntialias = request.Quality != RenderQuality.Draft, Color = SKColors.White };
        foreach (var sprite in batch.Sprites)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var image = SpriteImage(sprite).ImageForDevice(_device, out var raster); var tint = sprite.Tint;
            try
            {
                using var shader = _spriteProgram!.CreateShader(image, sprite.Source, request.CancellationToken);
                using var filter = SKColorFilter.CreateColorMatrix([
                    (float)tint.Red, 0, 0, 0, 0, 0, (float)tint.Green, 0, 0, 0,
                    0, 0, (float)tint.Blue, 0, 0, 0, 0, 0, (float)tint.Alpha, 0]);
                paint.ColorFilter = filter;
                paint.Shader = shader;
                canvas.Save();
                try
                {
                    canvas.Concat(SpriteMatrix(sprite));
                    canvas.DrawRect(new SKRect(0, 0, 1, 1), paint);
                }
                finally { canvas.Restore(); paint.ColorFilter = null; paint.Shader = null; }
            }
            finally { raster?.Dispose(); }
        }
    }
    private sealed class SpriteImageLeases(List<IDisposable> leases) : IDisposable
    {
        public void Dispose() { foreach (var lease in leases) lease.Dispose(); }
    }
}
