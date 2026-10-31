using System.Globalization;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Application.Tests.LyricContentEditUseCaseTests;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricsEditIdAllocatorTests
{
    [Fact]
    public void 分配扫描全部来源行元素注音和来源自身且同候选不复用标识()
    {
        var lyrics = LyricsDocument.Create([LyricLine.Create("line-1009", "甲", tokens: [LyricToken.Create("token-1100", "甲")])]);
        lyrics = lyrics with { Branches = lyrics.Branches.Add(LyricBranch.Create("token-1500", "另外", lines: [
            LyricLine.Create("token-1600", "乙", tokens: [LyricToken.Create("line-1700", "乙")]) with
                { RubyAnnotations = [RubyAnnotation.Create("line-2000", "line-1700", "え"), RubyAnnotation.Create("token-2100", "line-1700", "お")] }])) };
        var allocator = new LyricsEditIdAllocator(lyrics);
        Assert.Equal("line-2001", allocator.NextLineId());
        Assert.Equal("token-2101", allocator.NextTokenId());
        Assert.Equal("token-2102", allocator.NextTokenId());
        Assert.Equal("line-2002", allocator.NextLineId());
    }

    [Fact]
    public void 原下界前导零与Invariant编号规则保持()
    {
        var lyrics = LyricsDocument.Create([LyricLine.Create("line-0999", "甲", tokens: [LyricToken.Create("token-001234", "甲")])]);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var allocator = new LyricsEditIdAllocator(lyrics);
            Assert.Equal("line-1000", allocator.NextLineId());
            Assert.Equal("token-1235", allocator.NextTokenId());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("line-")] [InlineData("token-")]
    public void 数字最大值后明确耗尽不回绕或无限搜索(string prefix)
    {
        var lyrics = LyricsDocument.Create([LyricLine.Create(prefix + int.MaxValue, "甲")]);
        var allocator = new LyricsEditIdAllocator(lyrics);
        Assert.Throws<LyricsIdentifierExhaustedException>(() => prefix == "line-" ? allocator.NextLineId() : allocator.NextTokenId());
    }

    [Theory]
    [InlineData("line-")] [InlineData("token-")]
    public void 最后一个数字仍可分配随后明确耗尽(string prefix)
    {
        var lyrics = LyricsDocument.Create([LyricLine.Create(prefix + (int.MaxValue - 1), "甲")]);
        var allocator = new LyricsEditIdAllocator(lyrics);
        Assert.Equal(prefix + int.MaxValue, Next());
        Assert.Throws<LyricsIdentifierExhaustedException>(() => Next());
        string Next() => prefix == "line-" ? allocator.NextLineId() : allocator.NextTokenId();
    }

    [Fact]
    public void 新增行编号耗尽返回中立失败而无需元素编号不受其耗尽影响()
    {
        Project source = Sample();
        source = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.Add(LyricLine.Create("line-2147483647", "上界")) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        AssertRejected(workspace, () => LyricContentEditUseCase.AddLineAfter(workspace,
            new(workspace, workspace.Generation, "primary", null)), LyricContentEditErrorCode.IdentifierExhausted);
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "甲X👨‍👩‍👧‍👦é乙")).Accepted);
    }

    [Fact]
    public void 正文和复制中途元素编号耗尽不会提交部分候选或损失重做()
    {
        Project source = Sample();
        source = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.Add(LyricLine.Create("token-2147483646", "上界")) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        AssertRejected(workspace, () => LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "两个新元素")),
            LyricContentEditErrorCode.IdentifierExhausted);
        AssertRejected(workspace, () => LyricContentEditUseCase.DuplicateLine(workspace, Baseline(workspace)),
            LyricContentEditErrorCode.IdentifierExhausted);
        var added = LyricContentEditUseCase.AddLineAfter(workspace, new(workspace, workspace.Generation, "primary", null));
        Assert.True(added.Accepted);
        workspace.Undo();
        AssertRejected(workspace, () => LyricContentEditUseCase.DuplicateLine(workspace, Baseline(workspace)),
            LyricContentEditErrorCode.IdentifierExhausted);
        Assert.Same(added.Project, workspace.Redo());
    }
}
