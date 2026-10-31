using MakoKaraGenerator.Media;
using SkiaSharp;

namespace MakoKaraGenerator.Runtime.Hosting.Authoring;

/// <summary>工坊固定试样的自有音频和测试背景；不接受当前工程媒体。</summary>
public static class TemplateSampleMedia
{
    /// <summary>生成12秒PCM和背景并接受真实音频分析；目录由调用者拥有。</summary>
    public static async Task<TemplateSampleResources> CreateAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        string audio = Path.Combine(directory, "sample.wav");
        string background = Path.Combine(directory, "background.png");
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 标准48k单声道PCM：每秒轻柔节拍，4～6秒留真实空档。
            const int rate = 48_000;
            int count = (int)(TemplateSampleScene.DurationUs * rate / 1_000_000);
            using (var writer = new BinaryWriter(File.Create(audio)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
                writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(count * 2);
                for (int index = 0; index < count; index++)
                {
                    if (index % rate == 0) cancellationToken.ThrowIfCancellationRequested();
                    double time = (double)index / rate;
                    double pulse = time is >= 4 and < 6 ? 0 : Math.Exp(-12 * (time % 1));
                    writer.Write((short)(Math.Sin(2 * Math.PI * 220 * time) * pulse * 2_800));
                }
            }
            using var bitmap = new SKBitmap(640, 360);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(24, 31, 48));
            using var paint = new SKPaint { Color = new(45, 75, 108) };
            for (int x = 0; x < 640; x += 80)
                for (int y = 0; y < 360; y += 80)
                    if ((x / 80 + y / 80) % 2 == 0) canvas.DrawRect(x, y, 80, 80, paint);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(background);
            encoded.SaveTo(stream);
        }, cancellationToken).ConfigureAwait(false);
        AudioFeatureAnalysis analysis = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(audio, cancellationToken).ConfigureAwait(false);
        return new(audio, background, analysis.ToSourceData());
    }
}
