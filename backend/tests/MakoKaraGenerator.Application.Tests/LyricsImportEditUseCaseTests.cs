using System.Collections.Immutable;
using System.Text;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricsImportEditUseCaseTests
{
    [Theory]
    [InlineData("song.lrc", "[00:01.00]你好\n", "lrc.plain")]
    [InlineData("song.lrc", "[00:01.00]<00:01.00>你<00:01.50>好\n", "lrc.enhanced")]
    [InlineData("song.txt", "你好\n", "text.plain")]
    [InlineData("song.srt", "1\n00:00:01,000 --> 00:00:03,000\n你好\n", "srt")]
    [InlineData("song.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n你好\n", "vtt")]
    [InlineData("song.ass", "[Script Info]\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\k50}你{\\k50}好\n", "ass")]
    [InlineData("song.txt", "#TITLE:x\n#ARTIST:y\n#BPM:30\n#GAP:1000\n: 0 4 60 你\n: 4 2 62 好\n", "ustar")]
    [InlineData("song.ttml", "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"00:00:01.000\" end=\"00:00:03.000\"><span begin=\"00:00:01.000\" end=\"00:00:02.000\">你</span><span begin=\"00:00:02.000\" end=\"00:00:03.000\">好</span></p></div></body></tt>", "ttml")]
    public void 八种格式沿原注册表检测和准备保留真实文档与完整报告(string fileName, string content, string importerId)
    {
        var source = Source(content, fileName);
        var detection = LyricsImportEditUseCase.Detect(source);
        var result = LyricsImportEditUseCase.Prepare(source, new(), 6_000_000);
        Assert.Equal(importerId, detection!.Importer.Id);
        Assert.Equal(importerId, result.Report.ImporterId);
        Assert.Equal("你好", Assert.Single(result.Document.Lines).Text);
        Assert.Equal(1, result.Report.LineCount);
        Assert.Equal(result.Document.TokenCount, result.Report.TokenCount);
        Assert.Equal(detection.Reason, result.Report.DetectionReason);
        Assert.Equal(detection.IsAmbiguous, result.Report.IsFormatAmbiguous);
    }

    [Fact]
    public void 固定来源防御复制原数组且检测与解析不受后来内容改变()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("[00:01.00]<00:01.00>你<00:01.50>好\n");
        var source = new LyricsImportSource("song.lrc", bytes);
        int length = bytes.Length;
        var detection = LyricsImportEditUseCase.Detect(source);
        Array.Fill(bytes, (byte)'X');
        var result = LyricsImportEditUseCase.Prepare(source, new(), 3_000_000);
        Assert.Equal(length, source.Length);
        Assert.Equal("song.lrc", source.FileName);
        Assert.Equal("lrc.enhanced", detection!.Importer.Id);
        Assert.Equal("你好", result.Document.Lines[0].Text);
        Assert.Equal(2, result.Report.TimedTokenCount);
        Assert.Equal(result.Document, LyricsImportEditUseCase.Prepare(source, new(), 3_000_000).Document);
    }

    [Fact]
    public void 准备报告的实际集合不可由调用者修改()
    {
        var result = LyricsImportEditUseCase.Prepare(Source("[unknowntag]\n[00:01.00]你好\n", "song.lrc"), new());
        var issueList = Assert.IsAssignableFrom<IList<LyricsImportIssue>>(result.Report.Issues);
        var candidates = Assert.IsAssignableFrom<IList<string>>(result.Report.CandidateNames);
        var discarded = Assert.IsAssignableFrom<IList<string>>(result.Report.DiscardedFeatures);
        Assert.Throws<NotSupportedException>(() => issueList.Clear());
        Assert.Throws<NotSupportedException>(() => candidates.Add("changed"));
        Assert.Throws<NotSupportedException>(() => discarded.Add("changed"));
        Assert.IsType<ImmutableArray<LyricsImportIssue>>(result.Report.Issues);
        Assert.Contains(result.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.UnknownTag);
    }

    [Fact]
    public void 显式格式选择覆盖歧义而未知选项沿原自动容错()
    {
        var source = Source("[00:01.00]<00:01.00>你<00:01.50>好\n", "song.lrc");
        var detection = LyricsImportEditUseCase.Detect(source)!;
        Assert.True(detection.IsAmbiguous);
        Assert.Contains(detection.Matches, match => match.Importer.Id == "lrc.plain");
        var plain = LyricsImportEditUseCase.Prepare(source, new() { ImporterId = "lrc.plain" }, 3_000_000);
        Assert.Equal("lrc.plain", plain.Report.ImporterId);
        Assert.Contains("<00:01.00>", plain.Document.Lines[0].Text, StringComparison.Ordinal);
        Assert.All(plain.Document.Lines[0].Tokens, token => Assert.Null(token.Time));
        var fallback = LyricsImportEditUseCase.Prepare(source, new() { ImporterId = "unknown", EncodingName = "unknown" }, 3_000_000);
        Assert.Equal("lrc.enhanced", fallback.Report.ImporterId);
        Assert.Equal("你好", fallback.Document.Lines[0].Text);
    }

    [Theory]
    [InlineData(null)] [InlineData("gb18030")]
    public void 原自动与显式GB18030编码均保留中文和换行报告(string? encoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var source = new LyricsImportSource("song.srt", Encoding.GetEncoding("GB18030").GetBytes("1\r\n00:00:01,000 --> 00:00:03,000\r\n你好\r\n"));
        var result = LyricsImportEditUseCase.Prepare(source, new() { EncodingName = encoding });
        Assert.Equal("你好", result.Document.Lines[0].Text);
        Assert.Equal("gb18030", result.Report.EncodingName);
        Assert.Equal(LyricsNewLineKind.CarriageReturnLineFeed, result.Report.NewLineKind);
    }

    [Fact]
    public void UTF8带BOM和未知媒体时长沿原末行兜底而正媒体使用精确末点()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("[00:01.00]你好\r\n")];
        var source = new LyricsImportSource("song.lrc", bytes);
        var fallback = LyricsImportEditUseCase.Prepare(source, new(), -1);
        Assert.True(fallback.Report.HadByteOrderMark);
        Assert.Equal(5_000_000, fallback.Document.Lines[0].Time!.Value.EndUs);
        Assert.Equal(1, fallback.Report.FallbackLineEndCount);
        var media = LyricsImportEditUseCase.Prepare(source, new() { EncodingName = "utf-8" }, 3_000_017);
        Assert.Equal(3_000_017, media.Document.Lines[0].Time!.Value.EndUs);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 准备保留切分规则与是否估算的原时间语义(bool estimate)
    {
        var result = LyricsImportEditUseCase.Prepare(Source("1\n00:00:01,000 --> 00:00:03,000\nHello world\n", "song.srt"),
            new() { SplitRule = TokenSplitRule.Mixed, EstimateSplitTimes = estimate });
        var line = result.Document.Lines[0];
        Assert.Equal("Hello world", line.Text);
        Assert.Equal(["Hello ", "world"], line.Tokens.Select(token => token.Text));
        Assert.Equal(1, result.Report.SplitLineCount);
        if (estimate)
        {
            Assert.Equal(1_000_000, line.Tokens[0].Time!.Value.StartUs);
            Assert.Equal(3_000_000, line.Tokens[^1].Time!.Value.EndUs);
            Assert.All(line.Tokens, token => Assert.Equal(TimingStatus.Estimated, token.TimingStatus));
            Assert.Equal(2, result.Report.SplitEstimatedTokenCount);
        }
        else Assert.All(line.Tokens, token => { Assert.Null(token.Time); Assert.Equal(TimingStatus.Missing, token.TimingStatus); });
    }

    [Fact]
    public void 坏字幕块和报告错误不笼统拒绝可保留的已解析行()
    {
        var prepared = LyricsImportEditUseCase.Prepare(Source("1\n00:00:01,000 --> 00:00:02,000\n第一句\n\n坏块\n\n2\n00:00:03,000 --> 00:00:04,000\n第二句\n", "song.srt"), new());
        Assert.Equal(2, prepared.Document.Lines.Length);
        Assert.Contains(prepared.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.MalformedTag);
        prepared = new(prepared.Document, prepared.Report with { Issues = prepared.Report.Issues.Append(
            new LyricsImportIssue(LyricsImportIssueKind.MalformedTag, IssueSeverity.Error, 1, "bad", "保留的诊断")).ToImmutableArray() });
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var result = LyricsImportEditUseCase.CommitPrepared(workspace, Existing(workspace), prepared);
        Assert.True(result.Accepted); Assert.Equal(2, result.LineCount);
        Assert.Equal(["第一句", "第二句"], result.Project.Lyrics.Lines.Select(line => line.Text));
        Assert.Equal(IssueSeverity.Error, prepared.Report.MaxSeverity);
    }

    [Fact]
    public void 文件替换明确来源保留主来源元数据并重映射注音基础ID()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricBranch branch = source.Lyrics.Branches[1];
        var token = LyricToken.Create("token", "新", new(1, 10), TimingStatus.Imported) with { StyleOverride = new() { FontSizePx = 17 } };
        var ruby = RubyAnnotation.Create("ruby", "token", "しん", "ja", .6, 3, RubyAlignment.End);
        var line = LyricLine.Create("line", "新", new(0, 11), TimingStatus.Imported, [token]) with
            { RubyAnnotations = [ruby], PrimaryLineId = "legacy-association", StyleOverride = new() { FontSizePx = 22 } };
        var prepared = Prepared(LyricsDocument.Create([line], "ja"));
        var result = LyricsImportEditUseCase.CommitPrepared(workspace, Existing(workspace, branch.Id), prepared);
        Assert.True(result.Accepted); Assert.True(result.Changed); Assert.True(result.ChangeSet.FullRefreshRequired);
        LyricBranch edited = result.Project.Lyrics.Branches[1];
        var editedLine = Assert.Single(edited.Lines);
        Assert.NotEqual(line.Id, editedLine.Id);
        Assert.Equal(branch with { Language = "ja", Lines = edited.Lines }, edited);
        Assert.Same(source.Lyrics.PrimaryBranch, result.Project.Lyrics.PrimaryBranch);
        Assert.Equal(source.Lyrics.PrimaryBranchId, result.Project.Lyrics.PrimaryBranchId);
        Assert.Equal("legacy-association", editedLine.PrimaryLineId);
        LyricToken mappedToken = Assert.Single(editedLine.Tokens);
        RubyAnnotation mappedRuby = Assert.Single(editedLine.RubyAnnotations);
        Assert.NotEqual(token.Id, mappedToken.Id); Assert.NotEqual(ruby.Id, mappedRuby.Id);
        Assert.Equal(token with { Id = mappedToken.Id }, mappedToken);
        Assert.Equal(mappedToken.Id, mappedRuby.BaseTokenId);
        Assert.NotEqual(ruby.Segments[0].Id, mappedRuby.Segments[0].Id);
        Assert.Equal(ruby.Segments[0] with { Id = mappedRuby.Segments[0].Id }, mappedRuby.Segments[0]);
        Assert.Equal(ruby with { Id = mappedRuby.Id, BaseTokenId = mappedToken.Id, Segments = mappedRuby.Segments }, mappedRuby);
        Assert.Equal(line with { Id = editedLine.Id, Tokens = editedLine.Tokens, RubyAnnotations = editedLine.RubyAnnotations }, editedLine);
        Assert.Equal(branch.Id, result.SelectedBranchId); Assert.Equal(editedLine.Id, result.SelectedLineId);
        Assert.Equal("lyrics.import", workspace.UndoName); Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 导入语言未知保留原语言且替换是全行替换而非追加()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricBranch original = workspace.Project.Lyrics.Branches[1];
        var result = LyricsImportEditUseCase.CommitPrepared(workspace, Existing(workspace, original.Id),
            LyricsImportEditUseCase.Prepare(Source("新\n"), new()));
        Assert.True(result.Accepted);
        Assert.Equal(original.Language, result.Project.Lyrics.Branches[1].Language);
        Assert.Equal("新", Assert.Single(result.Project.Lyrics.Branches[1].Lines).Text);
    }

    [Theory]
    [InlineData(LyricBranchRole.Translation)] [InlineData(LyricBranchRole.Annotation)] [InlineData(LyricBranchRole.Custom)]
    public void 文件新来源保持独立时间及原主来源并连同行一次历史(LyricBranchRole role)
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var baseline = NewBranch(workspace);
        var prepared = LyricsImportEditUseCase.Prepare(Source("[00:01.00]新\n", "song.lrc"), new(), 3_000_000);
        int notifications = 0; workspace.History.Changed += (_, _) => notifications++;
        var result = LyricsImportEditUseCase.CommitPrepared(workspace, baseline, prepared, "  文件来源  ", role);
        Assert.True(result.Accepted);
        LyricBranch branch = result.Project.Lyrics.Branches[^1];
        Assert.Equal("文件来源", branch.Name); Assert.Equal(role, branch.Role);
        Assert.Equal(LyricBranchTimingMode.Independent, branch.TimingMode);
        Assert.NotNull(branch.Lines[0].Time);
        Assert.Equal(source.Lyrics.PrimaryBranchId, result.Project.Lyrics.PrimaryBranchId);
        Assert.Same(source.Lyrics.PrimaryBranch, result.Project.Lyrics.PrimaryBranch);
        Assert.Equal("branch.import", workspace.UndoName); Assert.Equal(1, workspace.History.UndoCount); Assert.Equal(1, notifications);
        AssertRejected(workspace, () => LyricsImportEditUseCase.CommitPrepared(workspace, baseline, prepared, "文件来源", role), LyricsImportEditErrorCode.TargetStale);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 粘贴保留原行和非空原文完整Unicode元素但新行元素均未定时()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var result = LyricsImportEditUseCase.AppendText(workspace, Existing(workspace), " \r\n  👨‍👩‍👧‍👦é \r\n\r新\n\t");
        Assert.True(result.Accepted); Assert.Equal(2, result.LineCount);
        Assert.Same(source.Lyrics.Lines[0], result.Project.Lyrics.Lines[0]);
        LyricLine first = result.Project.Lyrics.Lines[1];
        Assert.Equal("  👨‍👩‍👧‍👦é ", first.Text);
        Assert.Equal([" ", " ", "👨‍👩‍👧‍👦", "é", " "], first.Tokens.Select(token => token.Text));
        Assert.Equal("新", result.Project.Lyrics.Lines[2].Text);
        Assert.All(result.Project.Lyrics.Lines.Skip(1), line =>
        {
            Assert.Null(line.Time); Assert.Equal(TimingStatus.Missing, line.TimingStatus); Assert.Empty(line.RubyAnnotations);
            Assert.All(line.Tokens, token => { Assert.Null(token.Time); Assert.Equal(TimingStatus.Missing, token.TimingStatus); });
        });
        Assert.Equal("primary", result.SelectedBranchId); Assert.Equal(first.Id, result.SelectedLineId);
        Assert.Equal("lyrics.paste", workspace.UndoName); Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 粘贴新翻译来源跟随主来源且整批一次提交重复请求失效()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var baseline = NewBranch(workspace);
        int notifications = 0; workspace.History.Changed += (_, _) => notifications++;
        var result = LyricsImportEditUseCase.AppendText(workspace, baseline, "one\ntwo", "  翻译  ");
        var branch = result.Project.Lyrics.Branches[^1];
        Assert.True(result.Accepted); Assert.Equal("翻译", branch.Name);
        Assert.Equal(LyricBranchRole.Translation, branch.Role); Assert.Equal(LyricBranchTimingMode.FollowPrimary, branch.TimingMode);
        Assert.Equal(2, branch.Lines.Length); Assert.All(branch.Lines, line => Assert.Null(line.PrimaryLineId));
        Assert.Equal(1, workspace.History.UndoCount); Assert.Equal(1, notifications);
        AssertRejected(workspace, () => LyricsImportEditUseCase.AppendText(workspace, baseline, "one\ntwo", "翻译"), LyricsImportEditErrorCode.TargetStale);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 追加分配器扫描来源行元素和注音并在同候选保留全部ID()
    {
        Project source = Sample();
        var secondary = source.Lyrics.Branches[1] with { Id = "line-2400", Lines = [source.Lyrics.Branches[1].Lines[0] with
            { RubyAnnotations = [RubyAnnotation.Create("token-2600", "secondary-token", "译")] }] };
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.SetItem(1, secondary) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var result = LyricsImportEditUseCase.AppendText(workspace, Existing(workspace), "甲乙\n丙丁");
        Assert.True(result.Accepted);
        Assert.Equal(["line-2401", "line-2402"], result.Project.Lyrics.Lines.Skip(1).Select(line => line.Id));
        Assert.Equal(["token-2601", "token-2602", "token-2603", "token-2604"], result.Project.Lyrics.Lines.Skip(1).SelectMany(line => line.Tokens).Select(token => token.Id));
        string[] ids = AllIds(result.Project.Lyrics).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 中途ID耗尽拒绝全部新来源行并保留旧工程重做()
    {
        Project source = Sample();
        var old = source.Lyrics.Lines[0];
        source = source with { Lyrics = source.Lyrics with { Lines = [old with
            { RubyAnnotations = [RubyAnnotation.Create("token-2147483646", old.Tokens[0].Id, "旧")] }] } };
        ProjectWorkspace workspace = WithRedo(source);
        AssertRejected(workspace, () => LyricsImportEditUseCase.AppendText(workspace, NewBranch(workspace), "甲乙", "翻译"),
            LyricsImportEditErrorCode.IdentifierExhausted);
        Assert.Equal(1, workspace.History.RedoCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 无关工程设置变化可继续明确来源或新来源但保留最新设置(bool create)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = create ? NewBranch(workspace) : Existing(workspace);
        workspace.Execute(ProjectCommand.Create("composition", project => CompositionEditor.SetOutputDuration(project, 5_000_017)));
        var result = LyricsImportEditUseCase.CommitPrepared(workspace, baseline, Prepared(), create ? "新来源" : null);
        Assert.True(result.Accepted);
        Assert.Equal(5_000_017, result.Project.Composition.OutputDurationUs);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 完整原来源变更会拒绝替换与追加而另一个来源变更不阻塞(bool append)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = Existing(workspace);
        workspace.Execute(ProjectCommand.Create("secondary", project => project with { Lyrics = project.Lyrics.ReplaceBranch(project.Lyrics.Branches[1] with { Name = "改名" }) }));
        var accepted = append ? LyricsImportEditUseCase.AppendText(workspace, baseline, "新")
            : LyricsImportEditUseCase.CommitPrepared(workspace, baseline, Prepared());
        Assert.True(accepted.Accepted);
        var current = Existing(workspace);
        workspace.Execute(ProjectCommand.Create("target", project => project with { Lyrics = project.Lyrics.ReplaceBranch(project.Lyrics.PrimaryBranch! with { Name = "改目标" }) }));
        AssertRejected(workspace, () => append ? LyricsImportEditUseCase.AppendText(workspace, current, "新")
            : LyricsImportEditUseCase.CommitPrepared(workspace, current, Prepared()), LyricsImportEditErrorCode.TargetStale);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 工作区或代次过期拒绝所有导入提交(bool append)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = Existing(workspace);
        ProjectWorkspace other = ProjectWorkspace.Adopt(workspace.Project);
        AssertRejected(other, () => append ? LyricsImportEditUseCase.AppendText(other, baseline, "新")
            : LyricsImportEditUseCase.CommitPrepared(other, baseline, Prepared()), LyricsImportEditErrorCode.WorkspaceStale);
        workspace.StartNew(workspace.Project);
        AssertRejected(workspace, () => append ? LyricsImportEditUseCase.AppendText(workspace, baseline, "新")
            : LyricsImportEditUseCase.CommitPrepared(workspace, baseline, Prepared()), LyricsImportEditErrorCode.WorkspaceStale);
    }

    [Fact]
    public void 不完整或互斥的目标原值及新主来源请求都拒绝()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricsImportEditBaseline existing = Existing(workspace), create = NewBranch(workspace);
        LyricsImportEditBaseline[] invalid =
        [
            existing with { ExpectedBranch = null },
            existing with { BranchId = "other" },
            existing with { ExpectedLyrics = workspace.Project.Lyrics },
            create with { ExpectedBranch = workspace.Project.Lyrics.PrimaryBranch },
            create with { ExpectedLyrics = null },
        ];
        foreach (var baseline in invalid)
            AssertRejected(workspace, () => LyricsImportEditUseCase.CommitPrepared(workspace, baseline, Prepared(), baseline.BranchId is null ? "新" : null),
                LyricsImportEditErrorCode.InvalidTarget);
        AssertRejected(workspace, () => LyricsImportEditUseCase.CommitPrepared(workspace, existing, Prepared(), "不应新建"), LyricsImportEditErrorCode.InvalidTarget);
        AssertRejected(workspace, () => LyricsImportEditUseCase.CommitPrepared(workspace, create, Prepared(), "  "), LyricsImportEditErrorCode.InvalidTarget);
        AssertRejected(workspace, () => LyricsImportEditUseCase.CommitPrepared(workspace, create, Prepared(), "新", LyricBranchRole.Primary), LyricsImportEditErrorCode.InvalidTarget);
        AssertRejected(workspace, () => LyricsImportEditUseCase.CommitPrepared(workspace, create, Prepared(), "新", (LyricBranchRole)99), LyricsImportEditErrorCode.InvalidTarget);
    }

    [Fact]
    public void 空文件与空粘贴统一拒绝且旧工程引用干净重做选择保持()
    {
        ProjectWorkspace workspace = WithRedo(Sample());
        AssertRejected(workspace, () => LyricsImportEditUseCase.CommitPrepared(workspace, Existing(workspace), Prepared(LyricsDocument.Create())),
            LyricsImportEditErrorCode.EmptyImport);
        AssertRejected(workspace, () => LyricsImportEditUseCase.CommitPrepared(workspace, NewBranch(workspace), Prepared(LyricsDocument.Create()), "新"),
            LyricsImportEditErrorCode.EmptyImport);
        AssertRejected(workspace, () => LyricsImportEditUseCase.AppendText(workspace, Existing(workspace), " \r\n\t"), LyricsImportEditErrorCode.EmptyText);
        AssertRejected(workspace, () => LyricsImportEditUseCase.AppendText(workspace, NewBranch(workspace), " \n", "新"), LyricsImportEditErrorCode.EmptyText);
        Assert.False(workspace.IsDirty); Assert.Equal(1, workspace.History.RedoCount);
    }

    [Fact]
    public void 重复导入ID在候选内被拒绝不会留下部分行()
    {
        var prepared = Prepared();
        var line = prepared.Document.Lines[0];
        prepared = new(prepared.Document with { Lines = [line, line] }, prepared.Report);
        ProjectWorkspace workspace = WithRedo(Sample());
        AssertRejected(workspace, () => LyricsImportEditUseCase.CommitPrepared(workspace, Existing(workspace), prepared),
            LyricsImportEditErrorCode.IdentifierExhausted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 展示候选不合法时拒绝导入或追加且不返回成功行数选择(bool append)
    {
        Project source = ExclusiveDisplayFixture.Create();
        source = source with { LyricPresentation = source.LyricPresentation with
            { Nodes = source.LyricPresentation.Nodes.Add(source.LyricPresentation.Nodes[0]) } };
        ProjectWorkspace workspace = WithRedo(source);
        AssertRejected(workspace, () => append ? LyricsImportEditUseCase.AppendText(workspace, Existing(workspace), "新")
            : LyricsImportEditUseCase.CommitPrepared(workspace, Existing(workspace), Prepared()), LyricsImportEditErrorCode.DomainValidationFailed);
    }

    private static LyricsImportSource Source(string text, string fileName = "song.txt") => new(fileName, Encoding.UTF8.GetBytes(text));
    private static LyricsImportResult Prepared(LyricsDocument? document = null)
    {
        var result = LyricsImportEditUseCase.Prepare(Source("新\n"), new());
        return document is null ? result : new(document, result.Report);
    }

    private static Project Sample()
    {
        var line = LyricLine.Create("old-line", "旧", new(0, 1_000_000), TimingStatus.Edited, [LyricToken.Create("old-token", "旧")]);
        var branch = LyricBranch.Create("secondary", "译文", LyricBranchRole.Translation,
            [LyricLine.Create("secondary-line", "译", tokens: [LyricToken.Create("secondary-token", "译")])], "en", LyricBranchTimingMode.FollowPrimary,
            new() { FontSizePx = 15 });
        var lyrics = LyricsDocument.Create([line], "zh");
        lyrics = lyrics with { Branches = lyrics.Branches.Add(branch) };
        return Project.Create("导入", lyrics: lyrics);
    }

    private static LyricsImportEditBaseline Existing(ProjectWorkspace workspace, string id = "primary") =>
        new(workspace, workspace.Generation, id, workspace.Project.Lyrics.Branches.Single(branch => branch.Id == id));
    private static LyricsImportEditBaseline NewBranch(ProjectWorkspace workspace) => new(workspace, workspace.Generation, null, ExpectedLyrics: workspace.Project.Lyrics);

    private static ProjectWorkspace WithRedo(Project source)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("probe", project => project with { Name = "changed" }));
        workspace.Undo();
        return workspace;
    }

    private static IEnumerable<string> AllIds(LyricsDocument lyrics) => lyrics.Branches.SelectMany(branch => new[] { branch.Id }
        .Concat(branch.Lines.SelectMany(line => new[] { line.Id }.Concat(line.Tokens.Select(token => token.Id)).Concat(line.RubyAnnotations.Select(ruby => ruby.Id)))));

    private static void AssertRejected(ProjectWorkspace workspace, Func<LyricsImportEditResult> operation, LyricsImportEditErrorCode code)
    {
        Project source = workspace.Project;
        int undo = workspace.History.UndoCount, redo = workspace.History.RedoCount, notifications = 0;
        bool dirty = workspace.IsDirty;
        workspace.History.Changed += OnChanged;
        try
        {
            var result = operation();
            Assert.False(result.Accepted); Assert.False(result.Changed); Assert.Equal(code, result.Error?.Code);
            Assert.Equal(0, result.LineCount); Assert.Null(result.SelectedBranchId); Assert.Null(result.SelectedLineId);
            Assert.Same(source, result.Project); Assert.Same(source, workspace.Project);
            Assert.Equal(undo, workspace.History.UndoCount); Assert.Equal(redo, workspace.History.RedoCount);
            Assert.Equal(dirty, workspace.IsDirty); Assert.Equal(0, notifications);
        }
        finally { workspace.History.Changed -= OnChanged; }
        void OnChanged(object? sender, EventArgs args) => notifications++;
    }
}
