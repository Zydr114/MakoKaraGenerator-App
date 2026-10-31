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
        Assert.Equal(expected, actual);
    }
}
