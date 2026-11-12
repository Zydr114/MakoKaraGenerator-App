using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// P3 第 6 步：描边材质槽按两态拆开（P3 设计 §5.3、§10.2）。
/// 两态填充同色、描边同宽，只有描边不同：合并后的描边在过渡带内应恰好是两帧按 m 的交叉淡化，
/// 带外（字左侧整段已唱、右侧整段未唱）也逐像素等于对应一态。
/// </summary>
public sealed partial class WipeFeatherRenderingTests
{
    private static readonly RgbaColor StrokeFill = new(230, 230, 60, 255), SourceStroke = new(40, 200, 40, 200);

    /// <summary>单字「█」，两态同一填充；源描边为同宽实色，跟随未唱。</summary>
    private static Project StrokeProject(double feather)
    {
        var project = Project(feather, [("█", SideTime)], StrokeFill, StrokeFill, SideBackground);
        return project with
        {
            LyricStyle = project.LyricStyle with
            {
                Stroke = new StrokeSpec { Color = SourceStroke, WidthPx = (float)SideRig.StrokeWidth, Join = StrokeJoin.Miter },
                Shadow = null, Glow = null,
            },
        };
    }

    /// <summary>字左右各外扩 30 px 的整列逐像素：实际 ≈ (1 − m)·整字未唱 + m·整字已唱；带内两态不同的像素足够多。</summary>
    private static void AssertStrokeCrossfade(byte[] before, byte[] after, byte[] actual, float left, float right, long t, int tolerance, string where)
    {
        int distinct = 0;
        for (int y = 0; y < Height; y++)
        for (int x = (int)left - 30; x < (int)right + 30; x++)
        {
            double m = Mask(left, right, SideTime, t, 50, x + .5);
            int offset = (y * Width + x) * 4;
            if (m is > 0 and < 1 && Enumerable.Range(0, 4).Any(c => Math.Abs(before[offset + c] - after[offset + c]) > 20)) distinct++;
            for (int c = 0; c < 4; c++)
            {
                double expected = before[offset + c] * (1 - m) + after[offset + c] * m;
                Assert.True(Math.Abs(actual[offset + c] - expected) <= tolerance,
                    $"{where}：({x},{y}) 分量 {c} m={m:F3} 期望 {expected:F1}（未唱 {before[offset + c]}，已唱 {after[offset + c]}），实际 {actual[offset + c]}。");
            }
        }
        // 上下两条描边各约 50 × 8 px 落在带内。
        Assert.True(distinct >= 400, $"{where}：带内两态不同的像素 {distinct}。");
    }

    [Theory]
    [InlineData(MaterialSides.Both, false)]
    [InlineData(MaterialSides.Both, true)]
    [InlineData(MaterialSides.Unsung, false)]
    [InlineData(MaterialSides.Unsung, true)]
    [InlineData(MaterialSides.Sung, false)]
    public void 两态描边材质在过渡带内等于两态完整结果按m交叉淡化(MaterialSides sides, bool prefix)
    {
        var project = StrokeProject(.5);
        using var rig = new SideRig(replace: false, gpu: false);
        using var measure = new SkiaRenderPipeline();
        var (left, right) = Assert.Single(Spans(measure, project));
        const long t = 1_400_000;
        byte[] before = rig.Render(project, sides, 500_000, prefix, out _, stroke: true), after = rig.Render(project, sides, 3_000_000, prefix, out _, stroke: true);
        var actual = rig.Render(project, sides, t, prefix, out _, stroke: true);
        AssertStrokeCrossfade(before, after, actual, left, right, t, 2, $"描边 {sides} prefix={prefix}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 两态描边材质各自落在自己的一态(bool prefix)
    {
        // 交叉淡化只证明两侧合并正确；这里锚定每侧用的是自己那一态的绑定：整字未唱时只有未唱一侧可见，整字已唱时只有已唱一侧。
        var project = StrokeProject(.5);
        using var rig = new SideRig(replace: false, gpu: false);
        byte[] Render(MaterialSides sides, long time) => rig.Render(project, sides, time, prefix, out _, stroke: true);
        Assert.Equal(Render(MaterialSides.Unsung, 500_000), Render(MaterialSides.Both, 500_000));
        Assert.Equal(Render(MaterialSides.Sung, 3_000_000), Render(MaterialSides.Both, 3_000_000));
        Assert.NotEqual(Render(MaterialSides.Unsung, 500_000), Render(MaterialSides.Unsung, 3_000_000));
    }

    [Fact]
    public void GPU上两态描边材质在过渡带内按m交叉淡化()
    {
        if (Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var project = StrokeProject(.5);
        using var rig = new SideRig(replace: false, gpu: true);
        using var measure = new SkiaRenderPipeline();
        var (left, right) = Assert.Single(Spans(measure, project));
        const long t = 1_400_000;
        byte[] before = rig.Render(project, MaterialSides.Both, 500_000, true, out _, stroke: true), after = rig.Render(project, MaterialSides.Both, 3_000_000, true, out _, stroke: true);
        AssertStrokeCrossfade(before, after, rig.Render(project, MaterialSides.Both, t, true, out _, stroke: true), left, right, t, 3, "GPU 描边");
    }

    [Fact]
    public void 两态同一描边材质仍按单个部件绘制且与羽化无关()
    {
        using var rig = new SideRig(replace: false, gpu: false);
        const long t = 1_400_000;
        foreach (bool prefix in new[] { false, true })
        {
            // 单个部件不参与两态拆分：羽化与否、处在带内与否都逐字节相同；逐对象调度时表面峰值也比拆侧低（前缀调度的峰值由组表面决定）。
            var hard = rig.Render(StrokeProject(0), MaterialSides.Both, t, prefix, out _, stroke: true, same: true);
            var feathered = rig.Render(StrokeProject(.5), MaterialSides.Both, t, prefix, out var sameUsage, stroke: true, same: true);
            Assert.Equal(hard, feathered);
            Assert.Equal(feathered, rig.Render(StrokeProject(.5), MaterialSides.Both, 500_000, prefix, out _, stroke: true, same: true));
            if (prefix) continue;
            rig.Render(StrokeProject(.5), MaterialSides.Both, t, prefix, out var splitUsage, stroke: true);
            Assert.True(sameUsage.PeakBytes < splitUsage.PeakBytes, $"prefix={prefix}：同材质峰值 {sameUsage.PeakBytes}，拆侧 {splitUsage.PeakBytes}。");
        }
    }
}
