using System.Text.Json;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

using var stream = SKTypeface.Default.OpenStream();
Console.WriteLine(JsonSerializer.Serialize(new { SKFontManager.Default.FontFamilyCount,
    SKTypeface.Default.FamilyName, StreamLength = stream?.Length }));
if (args.Length != 1 || args[0] != "no-fonts" || stream is not null)
    throw new InvalidOperationException("Expected a fresh process with no readable font data.");
try
{
    using var shaper = new SKShaper(SKTypeface.Default);
    throw new InvalidOperationException("Expected the reported native asset failure.");
}
catch (ArgumentNullException error) when (error.ParamName == "asset")
{
    Console.WriteLine("PASS real SKShaper asset failure reproduced with an unreadable default typeface");
}
using var engine = new SkiaTextLayoutEngine();
try
{
    engine.Shape("朝の光", new TextLayoutOptions { FontSizePx = 72 });
    throw new InvalidOperationException("Expected a controlled missing-font failure.");
}
catch (InvalidOperationException error) when (error.Message.Contains("无法读取字体数据", StringComparison.Ordinal))
{
    Console.WriteLine("PASS product refuses unreadable fonts before SKShaper: " + error.Message);
}
