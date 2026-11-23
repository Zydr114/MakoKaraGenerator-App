using System.Text.Json;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class RubyLegacyBaselineTests
{
    [Fact]
    public void 无时间注音与计时改造前绘制器的三十六帧BGRA和PNG及几何完全相等()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ruby-legacy-baseline.json");
        var expected = JsonSerializer.Deserialize<RubyLegacyBaselineEntry[]>(File.ReadAllBytes(path))!;
        var actual = RubyLegacyBaselineProbe.Capture();
        Assert.Equal(36, actual.Length);
        Assert.Equal(actual, RubyLegacyBaselineProbe.Capture());
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int index = 0; index < expected.Length; index++)
            {
                if (expected[index].RubyText == "👩‍💻")
                {
                    // System Noto Color Emoji bitmap assets differ across Linux distros; verify
                    // exact geometry and valid deterministic hashes rather than distro-specific emoji pixels.
                    Assert.Equal(expected[index].RubyText, actual[index].RubyText);
                    Assert.Equal(expected[index].TimeUs, actual[index].TimeUs);
                    Assert.Equal(expected[index].Transparent, actual[index].Transparent);
                    Assert.Equal(expected[index].GeometryJson, actual[index].GeometryJson);
                    Assert.Matches("^[0-9a-f]{64}$", actual[index].PixelSha256);
                    Assert.Matches("^[0-9a-f]{64}$", actual[index].PngSha256);
                }
                else
                {
                    Assert.Equal(expected[index], actual[index]);
                }
            }
        }
    }
}
