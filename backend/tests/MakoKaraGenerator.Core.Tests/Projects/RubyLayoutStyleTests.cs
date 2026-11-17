using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class RubyLayoutStyleTests
{
    [Fact]
    public void 逐层设置显式回退和恢复继承不会覆盖原注音数据()
    {
        var annotation = RubyAnnotation.Create("r", "t", "かな", relativeScale: .8, gapPx: 7);
        var initial = LyricStyle.Default;
        Assert.Equal(.8, initial.Ruby.ResolveScale(annotation));
        Assert.Equal(7, initial.Ruby.ResolveGap(annotation));
        var branch = new LyricStyleOverride { Ruby = new() { RelativeScale = OptionalValue.Set<double?>(.6), GapPx = OptionalValue.Set<double?>(4) } };
        var line = new LyricStyleOverride { Ruby = new() { GapPx = OptionalValue.Set<double?>(2) } };
        var token = new LyricStyleOverride { Ruby = new() { RelativeScale = OptionalValue.Set<double?>(null) } };
        var resolved = LyricStyleResolver.Resolve(initial, branchOverride: branch, lineOverride: line, tokenOverride: token);
        Assert.Equal(.8, resolved.Style.Ruby.ResolveScale(annotation));
        Assert.Equal(2, resolved.Style.Ruby.ResolveGap(annotation));
        Assert.Equal(LyricStyleSource.Token, resolved.SourceOf(LyricStyleProperty.RubyRelativeScale));
        Assert.Equal(LyricStyleSource.Line, resolved.SourceOf(LyricStyleProperty.RubyGapPx));
        var restored = StyleBatchEditor.CopyProperties(token, initial, [LyricStyleProperty.RubyRelativeScale], restoreInheritance: true);
        Assert.True(restored.IsEmpty);
        Assert.Equal(.6, LyricStyleResolver.Resolve(initial, branchOverride: branch, tokenOverride: restored).Style.Ruby.RelativeScale);
        Assert.Equal(.8, annotation.RelativeScale);
        Assert.Equal(7, annotation.GapPx);
    }

    [Fact]
    public void 模板复制空值表示显式回退而非删除覆盖()
    {
        var copy = StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, LyricStyle.Default,
            [LyricStyleProperty.RubyRelativeScale, LyricStyleProperty.RubyGapPx]);
        Assert.True(copy.Ruby!.RelativeScale.IsSet);
        Assert.Null(copy.Ruby.RelativeScale.Value);
        Assert.True(copy.Ruby.GapPx.IsSet);
        var result = LyricStyleResolver.Resolve(LyricStyle.Default with { Ruby = new() { RelativeScale = .7, GapPx = 3 } }, tokenOverride: copy);
        Assert.Null(result.Style.Ruby.RelativeScale);
        Assert.Null(result.Style.Ruby.GapPx);
    }

    [Fact]
    public void 比例转换为实际字体字号不能溢出或下溢()
    {
        foreach (double scale in new[] { double.Epsilon, (double)float.MaxValue })
        {
            var style = LyricStyle.Default with { Ruby = new() { RelativeScale = scale } };
            Assert.False(style.IsValid);
            Assert.Throws<ArgumentException>(() => LyricStyleResolver.Resolve(style));
            Assert.Throws<ArgumentOutOfRangeException>(() => StyleBatchEditor.ValidateFinite(style));
        }
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(-.1, 2)] [InlineData(.5, -1)]
    public void 拒绝无效排版(double scale, double gap)
    {
        var value = new RubyLayoutSpec { RelativeScale = scale, GapPx = gap };
        Assert.False(value.IsValid);
        Assert.Throws<ArgumentException>(() => LyricStyleResolver.Resolve(LyricStyle.Default with { Ruby = value }));
    }
}
