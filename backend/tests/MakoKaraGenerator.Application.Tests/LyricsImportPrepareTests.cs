using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricsImportPrepareTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 文件及追加纯准备固定ID不触历史并能一次提交撤销重做(bool append)
    {
        var workspace = Workspace(); var source = workspace.Project;
        var snapshot = LyricsImportEditUseCase.Capture(workspace);
        var baseline = new LyricsImportEditBaseline(workspace, workspace.Generation, "primary", source.Lyrics.Branches[0]);
        var prepared = append ? LyricsImportEditUseCase.PrepareAppendCandidate(snapshot, baseline, " 新 e\u0301 👩‍🚀 \n\n下一行")
            : LyricsImportEditUseCase.PrepareImportCandidate(snapshot, baseline, Parse("新 e\u0301 👩‍🚀\n下一行"));
        Assert.True(prepared.Result.Accepted); Assert.Same(source, workspace.Project); Assert.Equal(0, workspace.History.UndoCount);
        var candidate = prepared.Result.Project;
        Assert.Equal(2, prepared.Result.LineCount);
        Assert.Same(candidate, LyricsImportEditUseCase.CommitCandidate(workspace, prepared).Project);
        Assert.Equal(1, workspace.History.UndoCount); Assert.Same(source, workspace.Undo()); Assert.Same(candidate, workspace.Redo());
        Assert.Equal(LyricsImportEditErrorCode.TargetStale, LyricsImportEditUseCase.CommitCandidate(workspace, prepared).Error!.Code);
    }

    [Fact]
    public void 快照后台准备不读实时工作区且提交拒绝后来无关变化()
    {
        var workspace = Workspace(); var source = workspace.Project;
        var snapshot = LyricsImportEditUseCase.Capture(workspace);
        var baseline = new LyricsImportEditBaseline(workspace, workspace.Generation, null, ExpectedLyrics: source.Lyrics);
        workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "后来" }));
        var prepared = LyricsImportEditUseCase.PrepareImportCandidate(snapshot, baseline, Parse("新"), " 独立 ");
        Assert.True(prepared.Result.Accepted); Assert.Equal(source.Name, prepared.Result.Project.Name);
        Assert.Equal(LyricBranchTimingMode.Independent, prepared.Result.Project.Lyrics.Branches[^1].TimingMode);
        Assert.Equal("独立", prepared.Result.Project.Lyrics.Branches[^1].Name);
        Assert.Equal(LyricsImportEditErrorCode.TargetStale, LyricsImportEditUseCase.CommitCandidate(workspace, prepared).Error!.Code);
        Assert.Equal("后来", workspace.Project.Name);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 空输入和预算拒绝保工程与重做(bool append)
    {
        var workspace = Workspace(); workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "后来" })); workspace.Undo();
        var source = workspace.Project; var snapshot = LyricsImportEditUseCase.Capture(workspace);
        var baseline = new LyricsImportEditBaseline(workspace, workspace.Generation, "primary", source.Lyrics.Branches[0]);
        var guard = new LyricsImportPreparationGuard(4096);
        Assert.Throws<LyricsImportPreparationLimitException>(() =>
        {
            if (append) LyricsImportEditUseCase.PrepareAppendCandidate(snapshot, baseline, new string('汉', 1000), preparationGuard: guard);
            else LyricsImportEditUseCase.Prepare(new("song.txt", Encoding.UTF8.GetBytes(new string('汉', 1000))), new(), 0, preparationGuard: guard);
        });
        Assert.Same(source, workspace.Project); Assert.Equal(1, workspace.History.RedoCount); Assert.Equal(0, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData("[00:01.00]你好\n", "lrc.plain")]
    [InlineData("[00:01.00]<00:01.00>你<00:01.50>好\n", "lrc.enhanced")]
    public void LRC显式强制编码现按真实选择解码并如实报告而自动检测保持(string text, string importer)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = Encoding.GetEncoding("GB18030").GetBytes(text);
        var input = new LyricsImportSource("song.lrc", bytes);
        var auto = LyricsImportEditUseCase.Prepare(input, new(), 3_000_000);
        Assert.Equal("你好", auto.Document.Lines[0].Text); Assert.Equal("gb18030", auto.Report.EncodingName);
        var forced = LyricsImportEditUseCase.Prepare(input, new() { EncodingName = "utf-8" }, 3_000_000);
        Assert.Equal(importer, forced.Report.ImporterId); Assert.Equal("utf-8", forced.Report.EncodingName);
        Assert.Equal(importer == "lrc.plain" ? "���" : "����", forced.Document.Lines[0].Text); Assert.False(forced.Report.HadByteOrderMark);
        Assert.Equal(LyricsNewLineKind.LineFeed, forced.Report.NewLineKind);
        Assert.DoesNotContain(forced.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.EncodingAssumed);
    }

    [Fact]
    public void 有界准备保持Unicode内容和完整报告且不把原始元素数当最终项数()
    {
        string text = new('a', 21000);
        var result = LyricsImportEditUseCase.Prepare(new("song.txt", Encoding.UTF8.GetBytes(text)),
            new() { SplitRule = TokenSplitRule.LatinWords }, 0, preparationGuard: new(64L * 1024 * 1024));
        Assert.Single(result.Document.Lines[0].Tokens); Assert.Equal(text, result.Document.Lines[0].Tokens[0].Text);
        var noGuard = LyricsImportEditUseCase.Prepare(new("song.txt", Encoding.UTF8.GetBytes(text)), new() { SplitRule = TokenSplitRule.LatinWords });
        Assert.Equal(Serialize(noGuard), Serialize(result));
    }

    [Fact]
    public void 旧工程标识集合也在准备配额内且原构造器签名保留()
    {
        var lyrics=LyricsDocument.Create(Enumerable.Range(0,1000).Select(i=>LyricLine.Create("old-"+i,"旧")));
        var workspace=ProjectWorkspace.Adopt(Project.Create("旧大工程",lyrics:lyrics));
        var snapshot=LyricsImportEditUseCase.Capture(workspace);
        var baseline=new LyricsImportEditBaseline(workspace,workspace.Generation,"primary",lyrics.Branches[0]);
        Assert.Throws<LyricsImportPreparationLimitException>(()=>LyricsImportEditUseCase.PrepareAppendCandidate(snapshot,baseline,"新增",preparationGuard:new(4096)));
        Assert.Throws<LyricsImportPreparationLimitException>(()=>LyricsImportEditUseCase.PrepareImportCandidate(snapshot,baseline,Parse("新增"),preparationGuard:new(4096)));
        Assert.Same(snapshot.Project,workspace.Project); Assert.Equal(0,workspace.History.UndoCount);
        Assert.NotNull(typeof(LyricsEditIdAllocator).GetConstructor([typeof(LyricsDocument)]));
        Assert.NotNull(typeof(LyricsImportEditUseCase).GetMethod("Prepare",[typeof(LyricsImportSource),typeof(LyricsImportOptions),typeof(long)]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 最终行观察器在保留前检查固定ID和真实位置且首行拒绝立刻停止(bool append)
    {
        var workspace=Workspace(); var source=workspace.Project;
        var snapshot=LyricsImportEditUseCase.Capture(workspace); var baseline=new LyricsImportEditBaseline(workspace,workspace.Generation,"primary",source.Lyrics.Branches[0]);
        int observed=0;
        void RejectFirst(LyricBranch branch,LyricLine line,int index,int total)
        {
            observed++; Assert.Equal("primary",branch.Id); Assert.Equal(append ? 1 : 0,index); Assert.Equal(append ? 4 : 3,total);
            Assert.Equal("首行",line.Text); Assert.NotEqual("line-0001",line.Id);
            throw new LyricsImportPreparationLimitException();
        }
        Assert.Throws<LyricsImportPreparationLimitException>(()=>
        {
            if(append) LyricsImportEditUseCase.PrepareAppendCandidate(snapshot,baseline,"首行\n后行\n末行",onPreparedLine:RejectFirst);
            else LyricsImportEditUseCase.PrepareImportCandidate(snapshot,baseline,Parse("首行\n后行\n末行"),onPreparedLine:RejectFirst);
        });
        Assert.Equal(1,observed); Assert.Same(source,workspace.Project); Assert.Equal(0,workspace.History.UndoCount);
    }

    private static LyricsImportResult Parse(string text) => LyricsImportEditUseCase.Prepare(new("song.txt", Encoding.UTF8.GetBytes(text)), new());
    private static ProjectWorkspace Workspace() => ProjectWorkspace.Adopt(Project.Create("导入", lyrics: LyricsDocument.Create([LyricLine.Create("old", "旧")])));
    private static readonly JsonSerializerOptions ComparisonJson = CreateComparisonJson();
    private static string Serialize(object value) => JsonSerializer.Serialize(value, ComparisonJson);
    private static JsonSerializerOptions CreateComparisonJson()
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(LyricToken) || info.Type == typeof(LyricLine))
                for (int i = info.Properties.Count - 1; i >= 0; i--)
                    if (info.Properties[i].Name == "DurationUs") info.Properties.RemoveAt(i);
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }

}
