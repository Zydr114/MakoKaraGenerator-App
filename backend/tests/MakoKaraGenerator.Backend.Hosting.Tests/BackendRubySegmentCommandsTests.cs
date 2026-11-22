using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendRubySegmentCommandsTests
{
    private static readonly RubyGroupTargetDto Group = new("primary","one","first","ruby");
    private static readonly RubySegmentTargetDto Target = new("primary","one","first","ruby","a");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task 七类命令真实读模型历史与回执重放保持独立时间及选择()
    {
        using var f = await Fixture.Create(); Project source = f.Workspace.Project;
        var request = f.Request("0",new SetRubySegmentTimeCommandDto(Target,"4000001","5000007"));
        var first = Accept(await f.Execute(request));
        Assert.Equal(1,first.RubyResult!.ChangedSegmentCount); Assert.Contains("lyricsRubyTiming",first.ChangeSet.Areas);
        Assert.Equal(["first"],first.ChangeSet.TokenIds); Assert.DoesNotContain("a", first.ChangeSet.TokenIds);
        var replay = Accept(await f.Execute(request with { ExpectedRevision="00",Command=new SetRubySegmentTimeCommandDto(Target,"+04000001","05000007") }));
        Assert.Equal(JsonSerializer.Serialize(first),JsonSerializer.Serialize(replay)); Assert.Equal(1,(await f.History()).UndoCount);
        var line = (await f.Registry.ReadLyricsWindowAsync("client",f.Session.ProjectSessionId,"primary","1")).Response!.Lines[0];
        Assert.Equal(new LyricTimeDto("4000001","5000007"),line.RubyAnnotations[0].Segments[0].Time);
        Assert.Equal(new LyricTimeDto("100000","200000"),line.Tokens[0].Time);
        var added = Accept(await f.Command("1",new AddRubySegmentCommandDto(Group,"っ","a"))); string id=added.RubyResult!.CreatedSegmentId!;
        Assert.True(added.RubyResult.SelectionChanged); Assert.Equal(id,added.RubyResult.SelectedSegmentId);
        Accept(await f.Command("2",new SetRubySegmentTextCommandDto(Target,"きゃ")));
        Accept(await f.Command("3",new ReorderRubySegmentsCommandDto(Group,["b",id,"a"])));
        Accept(await f.Command("4",new ClearRubySegmentTimeCommandDto(Target)));
        Accept(await f.Command("5",new SetRubySegmentTimesCommandDto([new(Target,"7000001","8000007")])));
        Accept(await f.Command("6",new RemoveRubySegmentCommandDto(Target with { SegmentId=id })));
        Assert.Equal("うきゃ",f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations[0].Text);
        for(int revision=7;revision<14;revision++) { var undone=Accept(await f.Command(revision.ToString(CultureInfo.InvariantCulture),new("history.undo"))); Assert.Contains("timeline",undone.ChangeSet.Areas); }
        Assert.Same(source,f.Workspace.Project); Assert.Equal(7,(await f.History()).RedoCount);
        Accept(await f.Command("14",new("history.redo"))); Assert.Equal(new TimeRange(4000001,5000007),f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations[0].Segments[0].Time);
        string path=Path.Combine(f.DirectoryPath,"saved.mkgproj"); f.Workspace.SaveAs(path);
        var reopened=ProjectWorkspace.CreateNew("other"); Assert.True(reopened.Open(path).Loaded);
        Assert.Equal(f.Workspace.Project.Lyrics,reopened.Project.Lyrics);
    }
    [Fact]
    public async Task 同值及重复清除保redo且旧setRubyText拒绝覆盖分段计时()
    {
        using var f=await Fixture.Create();
        Accept(await f.Command("0",new ClearRubySegmentTimeCommandDto(Target)));
        Accept(await f.Command("1",new SetRubySegmentTextCommandDto(Target,"新")));
        Accept(await f.Command("2",new("history.undo"))); var history=await f.History();
        Assert.False(Accept(await f.Command("3",new ClearRubySegmentTimeCommandDto(Target))).Changed);
        Assert.Equal(history,await f.History());
        var error=(await f.Command("3",new SetRubyTextCommandDto(new("primary","one","first"),"新"))).Error!;
        Assert.Equal("RubySegmentsRequireExplicitEdit",error.FailureCode); Assert.Equal(history,await f.History());
        Assert.False(Accept(await f.Command("3",new SetRubyTextCommandDto(new("primary","one","first"),"こう"))).Changed);
    }
    [Fact]
    public async Task 输入形状先于目标查找且父链端点批次重复和晚项错误原子拒绝()
    {
        using var f=await Fixture.Create(); var source=f.Workspace.Project;
        ProjectCommandDto[] malformed=[new SetRubySegmentTimeCommandDto(Target with { RubyId="missing" },"bad","2"),
            new SetRubySegmentTimesCommandDto([]), new SetRubySegmentTimesCommandDto([null!]),
            new SetRubySegmentTextCommandDto(Target,null), new ClearRubySegmentTimeCommandDto(Target) with { Target=new("primary","one","first") }];
        foreach(var command in malformed) Assert.Equal(BackendErrorCode.InvalidRequest,(await f.Command("0",command)).Error!.Code);
        var cases=new (ProjectCommandDto Command,string Code,string Field)[] {
            (new SetRubySegmentTimeCommandDto(Target with { TokenId="second" },"1","2"),"TargetStale","command.target"),
            (new SetRubySegmentTimeCommandDto(Target,"-1","2"),"InvalidStart","command.startUs"),
            (new SetRubySegmentTimeCommandDto(Target,"2","2"),"InvalidRange","command.endUs"),
            (new SetRubySegmentTextCommandDto(Target,""),"EmptyText","command.text"),
            (new AddRubySegmentCommandDto(Group,"新","missing"),"TargetStale","command.afterSegmentId"),
            (new ReorderRubySegmentsCommandDto(Group,["a","a"]),"InvalidOrder","command.orderedSegmentIds"),
            (new SetRubySegmentTimesCommandDto([new(Target,"1","2"),new(Target,"3","4")]),"DuplicateTarget","command.changes"),
            (new SetRubySegmentTimesCommandDto([new(Target,"1","2"),new(Target with { SegmentId="b" },"3","3")]),"InvalidRange","command.changes") };
        foreach(var item in cases) { var error=(await f.Command("0",item.Command)).Error!; Assert.Equal(BackendErrorCode.CommandRejected,error.Code); Assert.Equal(item.Code,error.FailureCode); Assert.Equal(item.Field,error.Field); Assert.Same(source,f.Workspace.Project); }
        Assert.Equal(0,(await f.History()).UndoCount);
    }
    [Fact]
    public async Task 输入完整最终行和真实回执预算均在提交前拒绝且保redo()
    {
        using var f=await Fixture.Create();
        Accept(await f.Command("0",new SetRubySegmentTextCommandDto(Target,"新"))); Accept(await f.Command("1",new("history.undo")));
        var source=f.Workspace.Project;var history=await f.History();
        Assert.Equal(BackendErrorCode.ResourceTooLarge,(await f.Command("2",new SetRubySegmentTextCommandDto(Target,new string('字',100000)))).Error!.Code);
        Assert.Same(source,f.Workspace.Project);Assert.Equal(history,await f.History());
        var line=source.Lyrics.Lines[0]; var ruby=line.RubyAnnotations[0];
        var segments=Enumerable.Range(0,20000).Select(i=>RubyTextSegment.Create("seg-"+i.ToString(CultureInfo.InvariantCulture),"あ")).ToArray();
        var oversized=ruby with { Text=new string('あ',20000),Segments=[..segments] };
        f.Workspace.Replace(source with { Lyrics=source.Lyrics with { Lines=source.Lyrics.Lines.SetItem(0,line with { RubyAnnotations=[oversized] }) } });
        var before=f.Workspace.Project;
        Assert.Equal(BackendErrorCode.ResourceTooLarge,(await f.Command("2",new SetRubySegmentTextCommandDto(Target with { SegmentId="seg-0" },"新"))).Error!.Code);
        Assert.Same(before,f.Workspace.Project);
        using var tiny=await Fixture.Create(receiptBytes:1024);
        var longTarget=Target with { BranchId=new string('字',128),LineId=new string('行',128),TokenId=new string('字',128),RubyId=new string('音',128) };
        var original=tiny.Workspace.Project;var oldLine=original.Lyrics.Lines[0];var oldRuby=oldLine.RubyAnnotations[0];
        var renamed=oldLine with { Id=longTarget.LineId,Tokens=[oldLine.Tokens[0] with { Id=longTarget.TokenId }],Text="甲",RubyAnnotations=[oldRuby with { Id=longTarget.RubyId,BaseTokenId=longTarget.TokenId }] };
        tiny.Workspace.Replace(original with { Lyrics=LyricsDocument.Create([renamed]) });
        // Keep the real primary identity; long line/token/ruby identities alone exceed this receipt gate.
        longTarget=longTarget with { BranchId="primary" }; var prior=tiny.Workspace.Project;
        Assert.Equal(BackendErrorCode.ResourceTooLarge,(await tiny.Command("0",new SetRubySegmentTextCommandDto(longTarget,"新"))).Error!.Code);
        Assert.Same(prior,tiny.Workspace.Project);Assert.Equal(0,(await tiny.History()).UndoCount);
    }
    [Fact]
    public async Task 删除最后片段明确清空选择且回放不生成第二个新身份()
    {
        using var f=await Fixture.Create();
        Accept(await f.Command("0",new RemoveRubySegmentCommandDto(Target)));
        var deleted=Accept(await f.Command("1",new RemoveRubySegmentCommandDto(Target with { SegmentId="b" })));
        Assert.True(deleted.RubyResult!.SelectionChanged);Assert.Null(deleted.RubyResult.SelectedSegmentId);Assert.Empty(f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations);
        Accept(await f.Command("2",new("history.undo")));Assert.Single(f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations);
        var request=f.Request("3",new AddRubySegmentCommandDto(Group,"新",null));var added=Accept(await f.Execute(request));
        var retry=Accept(await f.Execute(request));Assert.Equal(added.RubyResult!.CreatedSegmentId,retry.RubyResult!.CreatedSegmentId);Assert.Equal(2,f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations[0].Segments.Length);
    }
    [Theory]
    [InlineData("{\"type\":\"lyrics.setRubySegmentTime\",\"target\":{\"branchId\":\"primary\",\"lineId\":\"one\",\"tokenId\":\"first\",\"rubyId\":\"ruby\",\"segmentId\":\"a\",\"segmentId\":\"b\"},\"startUs\":\"1\",\"endUs\":\"2\"}")]
    [InlineData("{\"type\":\"lyrics.clearTokenTime\",\"target\":{\"branchId\":\"primary\",\"lineId\":\"one\",\"tokenId\":\"first\",\"segmentId\":\"a\"}}")]
    [InlineData("{\"type\":\"lyrics.addRubySegment\",\"target\":{},\"text\":\"x\"}")]
    public void 严格converter拒绝重复父链旧token混入segment及缺必填字段(string json) => Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<ProjectCommandDto>(json,Json));

    [Fact]
    public async Task 超过JS整数上限端点的单项批次和读窗口逐字串精确往返()
    {
        using var f = await Fixture.Create(); const string start = "9007199254740993", end = "9007199254741099";
        Accept(await f.Command("0", new SetRubySegmentTimeCommandDto(Target, start, end)));
        Accept(await f.Command("1", new SetRubySegmentTimesCommandDto([new(Target with { SegmentId = "b" }, start, end)])));
        var window = (await f.Registry.ReadLyricsWindowAsync("client", f.Session.ProjectSessionId, "primary", "2")).Response!;
        Assert.All(window.Lines[0].RubyAnnotations[0].Segments, segment => Assert.Equal(new LyricTimeDto(start, end), segment.Time));
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(window, Json));
        Assert.All(serialized.RootElement.GetProperty("lines")[0].GetProperty("rubyAnnotations")[0].GetProperty("segments").EnumerateArray(),
            segment => { Assert.Equal(JsonValueKind.String, segment.GetProperty("time").GetProperty("startUs").ValueKind); Assert.Equal(start, segment.GetProperty("time").GetProperty("startUs").GetString()); });
    }
    [Fact]
    public async Task ruby结构撤销重做返回恢复删除后的真实选择且重排同文保redo()
    {
        using var f = await Fixture.Create();
        var added = Accept(await f.Command("0", new AddRubySegmentCommandDto(Group, "っ", "a"))); string id = added.RubyResult!.CreatedSegmentId!;
        var undo = Accept(await f.Command("1", new("history.undo")));
        Assert.Equal("b", undo.RubyResult!.SelectedSegmentId); Assert.True(undo.RubyResult.SelectionChanged);
        Assert.False(Accept(await f.Command("2", new ReorderRubySegmentsCommandDto(Group, ["a", "b"]))).Changed);
        Assert.False(Accept(await f.Command("2", new SetRubySegmentTextCommandDto(Target, "こ"))).Changed);
        Assert.Equal(1, (await f.History()).RedoCount);
        var redo = Accept(await f.Command("2", new("history.redo")));
        Assert.Equal(id, redo.RubyResult!.SelectedSegmentId); Assert.Null(redo.RubyResult.CreatedSegmentId); Assert.True(redo.RubyResult.SelectionChanged);
        Accept(await f.Command("3", new RemoveRubySegmentCommandDto(Target)));
        Accept(await f.Command("4", new RemoveRubySegmentCommandDto(Target with { SegmentId = "b" })));
        Accept(await f.Command("5", new RemoveRubySegmentCommandDto(Target with { SegmentId = id })));
        var restored = Accept(await f.Command("6", new("history.undo"))); Assert.Equal(id, restored.RubyResult!.SelectedSegmentId);
        var removed = Accept(await f.Command("7", new("history.redo"))); Assert.Null(removed.RubyResult!.SelectedSegmentId); Assert.True(removed.RubyResult.SelectionChanged);
    }
    [Fact]
    public async Task 完整JSON行字节及累计行预算拒绝所有部分修改且大旧注音可通过删除修复()
    {
        using var f = await Fixture.Create(); var original = f.Workspace.Project; var line = original.Lyrics.Lines[0];
        string huge = new('a', 550000);
        var oversized = line with { Text = huge, Tokens = [line.Tokens[0] with { Text = huge }] };
        f.Workspace.Replace(original with { Lyrics = LyricsDocument.Create([oversized]) }); var source = f.Workspace.Project;
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", new SetRubySegmentTimeCommandDto(Target, "1", "2"))).Error!.Code); Assert.Same(source, f.Workspace.Project);
        string large = new('a', 40000);
        var lines = Enumerable.Range(0, 220).Select(i => line with { Id = "line-" + i, Text = large,
            Tokens = [line.Tokens[0] with { Id = "token-" + i, Text = large }], RubyAnnotations = [RubyAnnotation.Create("ruby-" + i, "token-" + i, "a") with { Segments = [RubyTextSegment.Create("segment-" + i, "a")] }] }).ToArray();
        f.Workspace.Replace(original with { Lyrics = LyricsDocument.Create(lines) }); source = f.Workspace.Project;
        var changes = Enumerable.Range(0, lines.Length).Select(i => new RubySegmentTimeChangeDto(new("primary", "line-" + i, "token-" + i, "ruby-" + i, "segment-" + i), "1", "2")).ToArray();
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", new SetRubySegmentTimesCommandDto(changes))).Error!.Code); Assert.Same(source, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
        var ruby = line.RubyAnnotations[0];
        var repairable = ruby with { Text = huge + "う", Segments = [RubyTextSegment.Create("a", huge), ruby.Segments[1]] };
        f.Workspace.Replace(original with { Lyrics = LyricsDocument.Create([line with { RubyAnnotations = [repairable] }]) });
        Accept(await f.Command("0", new RemoveRubySegmentCommandDto(Target)));
        Assert.Equal("う", f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations[0].Text);
    }
    [Theory]
    [InlineData("{\"type\":\"lyrics.setRubySegmentTime\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\",\"segmentId\":\"s\"},\"startUs\":1,\"endUs\":\"2\"}")]
    [InlineData("{\"type\":\"lyrics.setRubySegmentTime\",\"target\":null,\"startUs\":\"1\",\"endUs\":\"2\"}")]
    [InlineData("{\"type\":\"lyrics.clearRubySegmentTime\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\",\"segmentId\":\"s\",\"extra\":1}}")]
    [InlineData("{\"type\":\"lyrics.addRubySegment\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\",\"segmentId\":\"s\"},\"text\":\"a\",\"afterSegmentId\":null}")]
    [InlineData("{\"type\":\"lyrics.setRubySegmentTimes\",\"changes\":[{\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\",\"segmentId\":\"s\"},\"startUs\":\"1\",\"endUs\":\"2\",\"endUs\":\"3\"}]}")]
    [InlineData("{\"type\":\"lyrics.reorderRubySegments\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\"},\"orderedSegmentIds\":[null]}")]
    public void converter逐类拒绝数字时间null坏父链未知字段与重复批次字段(string json) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(json, Json));
    [Theory]
    [InlineData("{\"type\":\"lyrics.setRubySegmentTime\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\",\"segmentId\":\"s\"},\"startUs\":\"9007199254740993\",\"endUs\":\"9007199254741099\"}")]
    [InlineData("{\"type\":\"lyrics.clearRubySegmentTime\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\",\"segmentId\":\"s\"}}")]
    [InlineData("{\"type\":\"lyrics.setRubySegmentText\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\",\"segmentId\":\"s\"},\"text\":\"a\"}")]
    [InlineData("{\"type\":\"lyrics.addRubySegment\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\"},\"text\":\"a\",\"afterSegmentId\":null}")]
    [InlineData("{\"type\":\"lyrics.removeRubySegment\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\",\"segmentId\":\"s\"}}")]
    [InlineData("{\"type\":\"lyrics.reorderRubySegments\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\"},\"orderedSegmentIds\":[\"a\",\"b\"]}")]
    [InlineData("{\"type\":\"lyrics.setRubySegmentTimes\",\"changes\":[{\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\",\"rubyId\":\"r\",\"segmentId\":\"s\"},\"startUs\":\"1\",\"endUs\":\"2\"}]}")]
    public void 七命令严格JSON读写往返不混入旧时间目标(string json)
    {
        var command = JsonSerializer.Deserialize<ProjectCommandDto>(json, Json)!;
        string serialized = JsonSerializer.Serialize(command, Json);
        Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement), JsonSerializer.Serialize(JsonDocument.Parse(serialized).RootElement));
        Assert.Null(command.Target); Assert.Null(command.StartUs); Assert.Null(command.EndUs);
    }

    [Fact]
    public async Task 原子时间数组与重排数组排队后保持分离载荷及真实回执指纹()
    {
        using var f = await Fixture.Create();
        var writer = f.Session.Writer;
        RubySegmentTimeChangeDto[] changes = [new(Target, "150001", "250003")];
        var request = f.Request("0", new SetRubySegmentTimesCommandDto(changes));
        await writer.WaitAsync(); Task<ProjectCommandExecutionResult> pending;
        try { pending = f.Execute(request); Assert.False(pending.IsCompleted); changes[0] = new(Target, "160005", "260007"); }
        finally { writer.Release(); }
        var first = Accept(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new TimeRange(150001, 250003), f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations[0].Segments[0].Time);
        var frozen = request with { Command = new SetRubySegmentTimesCommandDto([new(Target, "+0150001", "+0250003")]) };
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(Accept(await f.Execute(frozen))));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(request)).Error!.Code);
        string[] order = ["b", "a"]; var reorder = f.Request("1", new ReorderRubySegmentsCommandDto(Group, order));
        await writer.WaitAsync();
        try { pending = f.Execute(reorder); Assert.False(pending.IsCompleted); order[0] = "a"; order[1] = "b"; }
        finally { writer.Release(); }
        Accept(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["b", "a"], f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations[0].Segments.Select(segment => segment.Id));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(reorder)).Error!.Code);
        Assert.Equal(2, (await f.History()).UndoCount);
    }
    private static ProjectCommandResponse Accept(ProjectCommandExecutionResult result) { Assert.True(result.Accepted,$"{result.Error?.Code} {result.Error?.FailureCode}: {result.Error?.Message}");return result.Response!; }
    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal string DirectoryPath => _directory.Path;
        internal ProjectSessionRegistry Registry = null!; internal ProjectSession Session = null!; internal ProjectWorkspace Workspace = null!;
        private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        internal static async Task<Fixture> Create(int receiptBytes = 65536)
        {
            Fixture f = new(); ResourceStore store = new(new() { ResourceRoot = Path.Combine(f._directory.Path, "resources") });
            StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]甲乙\n"u8.ToArray()));
            StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            WaveformPeakBuilder builder = new(); builder.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), builder.Build(8000));
            f.Registry = new("host", store, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)), new FixedClock(), new ProjectCommandRetentionOptions { MaxReceiptBytes = receiptBytes });
            f.Session = (await f.Registry.CreateAsync("client", new("content", audio.AssetId, lyrics.AssetId))).Session!;
            f.Workspace = f.Session.Workspace;
            LyricLine line = LyricLine.Create("one", "甲乙", TimeRange.Create(0, 2000000), TimingStatus.Imported,
                [LyricToken.Create("first", "甲", TimeRange.Create(100000, 200000), TimingStatus.Imported),
                 LyricToken.Create("second", "乙", TimeRange.Create(500000, 700000), TimingStatus.Imported)]) with
                { RubyAnnotations = [RubyAnnotation.Create("ruby", "first", "こう", "ja", .7, 3, RubyAlignment.Start) with { Segments = [RubyTextSegment.Create("a", "こ", new(100000, 200000), TimingStatus.Imported), RubyTextSegment.Create("b", "う", new(200000, 300000), TimingStatus.Imported)] }] };
            f.Workspace.Replace(f.Workspace.Project with { Lyrics = LyricsDocument.Create([line,
                LyricLine.Create("two", "丙", TimeRange.Create(2000000, 2500000), TimingStatus.Imported)]) });
            return f;
        }
        internal async Task<ProjectHistoryResponse> History() => (await Registry.ReadHistoryAsync("client", Session.ProjectSessionId))!;
        internal ProjectCommandRequest Request(string revision, ProjectCommandDto command) => new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, Now.AddMinutes(5));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Execute(Request(revision, command));
        internal Task<ProjectCommandExecutionResult> Execute(ProjectCommandRequest request) => Registry.ExecuteCommandAsync("client", Session.ProjectSessionId, request);
        public void Dispose() { Registry.Dispose(); _directory.Dispose(); }
        private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    }
}
