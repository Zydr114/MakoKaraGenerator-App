using System.Reflection;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendImportCommandTests
{
    [Fact]
    public async Task 固定文件新独立来源与粘贴FollowPrimary返回完整报告选择一次历史及重试()
    {
        await using Fixture f = await Fixture.Create(); var source = f.Workspace.Project;
        var asset = await f.Resources.UploadAsync("client", "new.srt", "application/octet-stream",
            new MemoryStream(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\n你好\n")));
        var request = f.Request("0", new ImportAssetCommandDto(asset.AssetId, new NewBranchLyricsDestinationDto(" 文件 ", ProjectLyricBranchRole.Annotation)));
        var first = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        var feedback = first.ImportResult!; Assert.Equal(1, feedback.LineCount); Assert.Equal("srt", feedback.Report!.ImporterId);
        Assert.Equal("0", feedback.Report.OffsetUs); Assert.Equal(feedback.Report.IssueCount, feedback.Report.Issues.Length);
        Assert.Equal(feedback.SelectedLineId, f.Workspace.Project.Lyrics.Branches[^1].Lines[0].Id);
        Assert.Equal("文件", f.Workspace.Project.Lyrics.Branches[^1].Name); Assert.Equal(LyricBranchTimingMode.Independent, f.Workspace.Project.Lyrics.Branches[^1].TimingMode);
        File.Delete(asset.StoragePath); // A retained receipt must not reacquire or reparse an expired/unavailable asset.
        var retry = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(retry)); Assert.Equal(1, f.Workspace.History.UndoCount);
        var pasted = Accepted(await f.Command("1", new AppendTextCommandDto(" 保留 👩‍🚀 e\u0301 \r\n\r\n第二行", new NewTranslationLyricsDestinationDto(" 粘贴 "))));
        Assert.Null(pasted.ImportResult!.Report); Assert.Equal(2, pasted.ImportResult.LineCount);
        var branch = f.Workspace.Project.Lyrics.Branches[^1]; Assert.Equal(LyricBranchTimingMode.FollowPrimary, branch.TimingMode);
        Assert.Equal(" 保留 👩‍🚀 e\u0301 ", branch.Lines[0].Text); Assert.All(branch.Lines[0].Tokens, token => Assert.Null(token.Time));
        var window = await f.Registry.ReadLyricsWindowAsync("client", f.Id, branch.Id, "2"); Assert.True(window.Accepted);
        Accepted(await f.Command("2", new("history.undo"))); Accepted(await f.Command("3", new("history.undo"))); Assert.Same(source, f.Workspace.Project);
    }

    [Fact]
    public async Task 替换保来源元数据追加保未触及大旧行并逐行预算拒绝无变化()
    {
        await using Fixture f = await Fixture.Create(); var branch = f.Translation;
        var asset = await f.Resources.UploadAsync("client", "new.txt", "text/plain", new MemoryStream("新\n"u8.ToArray()));
        Accepted(await f.Command("0", new ImportAssetCommandDto(asset.AssetId, new ExistingLyricsDestinationDto(branch.Id))));
        Assert.Equal(branch.StyleOverride, f.Translation.StyleOverride); Assert.Equal(branch.Language, f.Translation.Language); Assert.Equal(branch.TimingMode, f.Translation.TimingMode);
        Accepted(await f.Command("1", new("history.undo")));
        f.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics.ReplaceBranch(branch with { Lines = [branch.Lines[0] with { Text = new string('汉', 200000) }] }) });
        Accepted(await f.Command("2", new AppendTextCommandDto("新增", new ExistingLyricsDestinationDto(branch.Id))));
        var before = f.Workspace.Project; var history = await f.Registry.ReadHistoryAsync("client", f.Id); var summary = await f.Registry.ReadSummaryAsync("client", f.Id);
        var rejected = await f.Command("3", new AppendTextCommandDto(new string('x', 20000), new ExistingLyricsDestinationDto(branch.Id)));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, rejected.Error!.Code); Assert.Equal("command", rejected.Error.Field);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(history, await f.Registry.ReadHistoryAsync("client", f.Id)); Assert.Equal(summary, await f.Registry.ReadSummaryAsync("client", f.Id));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 完整报告或逻辑工作超限拒绝并可重试且不留准备名额(bool logical)
    {
        await using Fixture f = await Fixture.Create(); var before = f.Workspace.Project;
        string text = logical ? new string('a', 150000) : string.Join("\n", Enumerable.Range(0,1000).Select(i => "[unknown" + i + "]")) + "\n[00:01.00]有效";
        var asset = await f.Resources.UploadAsync("client", logical ? "large.txt" : "report.lrc", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var request = f.Request("0", new ImportAssetCommandDto(asset.AssetId, new ExistingLyricsDestinationDto("primary")));
        var result = await f.Registry.ExecuteCommandAsync("client", f.Id, request); Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
        var retry = await f.Registry.ExecuteCommandAsync("client", f.Id, request); Assert.Equal(result.Error, retry.Error);
        Assert.True((await f.Command("0", new AppendTextCommandDto("恢复", new ExistingLyricsDestinationDto("primary")))).Accepted);
    }

    [Fact]
    public async Task 读取授权固定字节核SHA并拒绝损坏及跨client资源且错误路径真实()
    {
        await using Fixture f = await Fixture.Create(); var original = f.Workspace.Project;
        var other = await f.Resources.UploadAsync("other", "new.txt", "text/plain", new MemoryStream("新"u8.ToArray()));
        var rejected = await f.Command("0", new ImportAssetCommandDto(other.AssetId, new ExistingLyricsDestinationDto("primary")));
        Assert.Equal("command.assetId", rejected.Error!.Field); Assert.Same(original, f.Workspace.Project);
        var asset = await f.Resources.UploadAsync("client", "new.txt", "text/plain", new MemoryStream("新"u8.ToArray()));
        await File.WriteAllBytesAsync(asset.StoragePath, "坏"u8.ToArray());
        rejected = await f.Command("0", new ImportAssetCommandDto(asset.AssetId, new ExistingLyricsDestinationDto("primary")));
        Assert.Equal(BackendErrorCode.LyricsImportFailed, rejected.Error!.Code); Assert.Equal("command.assetId", rejected.Error.Field); Assert.Same(original, f.Workspace.Project);
    }

    [Fact]
    public async Task 准备名额不排队使用429且长来源目的地不扩旧时间ID约束()
    {
        await using Fixture f = await Fixture.Create();
        var set = (HashSet<string>)typeof(ProjectSessionRegistry).GetField("_lyricsPreparations", BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(f.Registry)!;
        set.Add("client");
        var busy = await f.Command("0", new AppendTextCommandDto("新", new ExistingLyricsDestinationDto("primary")));
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded, busy.Error!.Code); Assert.Equal("command", busy.Error.Field); set.Clear();
        set.Add("other-a"); set.Add("other-b");
        var full=await f.Command("0",new AppendTextCommandDto("新",new ExistingLyricsDestinationDto("primary")));
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded,full.Error!.Code); set.Clear();
        string id = new('x',129); var branch = f.Translation;
        f.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics.ReplaceBranch(branch with { Id = id }) });
        var invalid = await f.Command("0", new AppendTextCommandDto("新", new ExistingLyricsDestinationDto(id)));
        Assert.Equal(BackendErrorCode.InvalidRequest, invalid.Error!.Code); Assert.Equal("command.destination.branchId", invalid.Error.Field);
    }

    [Fact]
    public async Task 并发准备同修订只提交一个且回执精确重放()
    {
        await using Fixture f = await Fixture.Create();
        var results = await Task.WhenAll(f.Command("0", new AppendTextCommandDto("甲", new ExistingLyricsDestinationDto("primary"))),
            f.Command("0", new AppendTextCommandDto("乙", new ExistingLyricsDestinationDto("primary"))));
        Assert.Single(results, value => value.Accepted);
        Assert.Single(results, value => value.Error?.Code is BackendErrorCode.RevisionConflict or BackendErrorCode.RequestQuotaExceeded);
        Assert.Equal(1, f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 已接受owner离开Writer后允许其他编辑并最终精确409且候选不覆盖()
    {
        await using Fixture f=await Fixture.Create(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Checkpoint(async()=> { entered.SetResult(); await release.Task; });
        var import=f.Command("0",new AppendTextCommandDto("不会提交",new ExistingLyricsDestinationDto("primary")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var edited=Accepted(await f.Command("0",new SetCanvasSizeCommandDto(1280,720))); Assert.Equal("1",edited.ProjectRevision);
        var current=f.Workspace.Project; release.SetResult(); var conflict=await import;
        Assert.Equal(BackendErrorCode.RevisionConflict,conflict.Error!.Code); Assert.Equal("expectedRevision",conflict.Error.Field);
        Assert.Same(current,f.Workspace.Project); Assert.Equal(1,f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 准备中的关闭先成功且旧owner拒绝复活会话()
    {
        await using Fixture f=await Fixture.Create(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Checkpoint(async()=> { entered.SetResult(); await release.Task; });
        var pending=f.Command("0",new AppendTextCommandDto("不会提交",new ExistingLyricsDestinationDto("primary")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close=await f.Registry.StartCloseAsync("client",f.Id,new("0","discard",ActiveJobs:"wait",Metadata:new(Guid.NewGuid().ToString("N"),"host",DateTimeOffset.UtcNow.AddMinutes(1))));
        Assert.True(close.Accepted,close.Error?.Message);
        for(int i=0;i<100 && f.Registry.TryGet("client",f.Id,out _);i++) await Task.Delay(10);
        Assert.False(f.Registry.TryGet("client",f.Id,out _));
        release.SetResult(); var result=await pending; Assert.Equal(BackendErrorCode.SessionClosing,result.Error!.Code);
        Assert.Equal(0,f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 取消等待不撤销owner重试共享固定ID且shutdown等owner终态()
    {
        await using Fixture f=await Fixture.Create(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Checkpoint(async()=> { entered.SetResult(); await release.Task; });
        using var cancellation=new CancellationTokenSource();
        var request=f.Request("0",new AppendTextCommandDto("接受后继续",new ExistingLyricsDestinationDto("primary")));
        var pending=f.Registry.ExecuteCommandAsync("client",f.Id,request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelledWait=f.Registry.ExecuteCommandAsync("client",f.Id,request,cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelledWait);
        var replay=f.Registry.ExecuteCommandAsync("client",f.Id,request);
        var shutdown=f.Registry.DisposeAsync().AsTask(); Assert.False(shutdown.IsCompleted);
        release.SetResult(); var owner=Accepted(await pending);
        var result=Accepted(await replay); Assert.Equal(JsonSerializer.Serialize(owner),JsonSerializer.Serialize(result)); await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1,f.Workspace.History.UndoCount); Assert.Equal("接受后继续",f.Workspace.Project.Lyrics.Lines[^1].Text);
        Assert.Equal(f.Workspace.Project.Lyrics.Lines[^1].Id,result.ImportResult!.SelectedLineId);
    }

    [Fact]
    public async Task 回执指纹保留非法基类槽不允许同ID外来字段重放接受()
    {
        await using Fixture f=await Fixture.Create();
        var request=f.Request("0",new AppendTextCommandDto("合法",new ExistingLyricsDestinationDto("primary")));
        Accepted(await f.Registry.ExecuteCommandAsync("client",f.Id,request));
        foreach(var command in new ProjectCommandDto[] { request.Command! with {StartUs="123"}, request.Command! with {EndUs="456"},
            request.Command! with {Target=new("primary","main")},request.Command! with {AnalysisJobId="foreign"}})
        {
            var result=await f.Registry.ExecuteCommandAsync("client",f.Id,request with {Command=command});
            Assert.Equal(BackendErrorCode.RequestIdReuse,result.Error!.Code);
        }
        Assert.Equal(1,f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 累计真实投影十六MiB拒绝而每行本身小于一MiB且逻辑工作未耗尽()
    {
        await using Fixture f=await Fixture.Create(); var before=f.Workspace.Project;
        string row="a"+new string('\u0301',20000);
        var asset=await f.Resources.UploadAsync("client","aggregate.txt","text/plain",new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n",Enumerable.Repeat(row,80)))));
        var result=await f.Command("0",new ImportAssetCommandDto(asset.AssetId,new ExistingLyricsDestinationDto("primary")));
        Assert.Equal(BackendErrorCode.ResourceTooLarge,result.Error!.Code); Assert.Same(before,f.Workspace.Project); Assert.Equal(0,f.Workspace.History.UndoCount);
        // The same row is independently readable when admitted as one complete new row.
        var small=await f.Resources.UploadAsync("client","one.txt","text/plain",new MemoryStream(Encoding.UTF8.GetBytes(row)));
        Accepted(await f.Command("0",new ImportAssetCommandDto(small.AssetId,new ExistingLyricsDestinationDto("primary"))));
        var window=await f.Registry.ReadLyricsWindowAsync("client",f.Id,"primary","1"); Assert.True(window.Accepted); Assert.Equal(row,Assert.Single(window.Response!.Lines).Text);
    }

    [Fact]
    public async Task 完整成功回执按配置预算在历史之前拒绝不靠归档后降级()
    {
        await using Fixture f=await Fixture.Create(receiptBytes:1024); var before=f.Workspace.Project;
        var asset=await f.Resources.UploadAsync("client","report.srt","text/plain",new MemoryStream("1\n00:00:01,000 --> 00:00:03,000\n有效\n\n坏块\n"u8.ToArray()));
        var rejected=await f.Command("0",new ImportAssetCommandDto(asset.AssetId,new ExistingLyricsDestinationDto("primary")));
        Assert.Equal(BackendErrorCode.ResourceTooLarge,rejected.Error!.Code); Assert.Same(before,f.Workspace.Project); Assert.Equal(0,f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task TTML部分成功保完整Error报告且原业务可提交保留下的行()
    {
        await using Fixture f=await Fixture.Create();
        string text="<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"1s\" end=\"3s\">保留</p><broken";
        var asset=await f.Resources.UploadAsync("client","partial.ttml","application/octet-stream",new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var result=Accepted(await f.Command("0",new ImportAssetCommandDto(asset.AssetId,new ExistingLyricsDestinationDto("primary"))));
        Assert.Equal("保留",Assert.Single(f.Workspace.Project.Lyrics.Lines).Text);
        Assert.Equal(LyricsImportIssueSeverity.Error,result.ImportResult!.Report!.MaxSeverity);
        Assert.Contains(result.ImportResult.Report.Issues,issue=>issue.Severity==LyricsImportIssueSeverity.Error);
        Assert.Equal(1,f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 租约释放失败先于历史提交且保留主读取错误并释放引用()
    {
        await using Fixture f=await Fixture.Create(); byte[] content="新"u8.ToArray();
        var asset=await f.Resources.UploadAsync("client","clean.txt","text/plain",new MemoryStream(content));
        var before=f.Workspace.Project; var history=await f.Registry.ReadHistoryAsync("client",f.Id); var summary=await f.Registry.ReadSummaryAsync("client",f.Id);
        f.Registry.ImportReadLeaseDecorator=read=>new(read.Asset,new CleanupFailureStream(content),read);
        var rejected=await f.Command("0",new ImportAssetCommandDto(asset.AssetId,new ExistingLyricsDestinationDto("primary")));
        Assert.Equal(BackendErrorCode.LyricsImportFailed,rejected.Error!.Code); Assert.Equal("command.assetId",rejected.Error.Field);
        Assert.Same(before,f.Workspace.Project); Assert.Equal(history,await f.Registry.ReadHistoryAsync("client",f.Id)); Assert.Equal(summary,await f.Registry.ReadSummaryAsync("client",f.Id));
        f.Registry.ImportReadLeaseDecorator=read=>new(read.Asset,new CleanupFailureStream("坏"u8.ToArray()),read);
        rejected=await f.Command("0",new ImportAssetCommandDto(asset.AssetId,new ExistingLyricsDestinationDto("primary")));
        Assert.Equal(BackendErrorCode.LyricsImportFailed,rejected.Error!.Code); Assert.Same(before,f.Workspace.Project);
        f.Registry.ImportReadLeaseDecorator=null;
        Accepted(await f.Command("0",new ImportAssetCommandDto(asset.AssetId,new ExistingLyricsDestinationDto("primary"))));
    }

    [Fact]
    public async Task TTML下载媒体类型支持创建全文档与再次导入显式选择并完整重映射片段身份()
    {
        await using Fixture f = await Fixture.Create();
        byte[] content = ProjectTtml();
        var asset = await f.Resources.UploadAsync("client", "project.ttml", "application/ttml+xml", new MemoryStream(content));
        var audio = await f.Resources.UploadAsync("client", "another.wav", "audio/wav", new MemoryStream([1]));
        var created = await f.Registry.CreateAsync("client", new("TTML全文档", audio.AssetId, asset.AssetId));
        Assert.True(created.Accepted, created.Error?.Message);
        var newWorkspace = created.Session!.Workspace;
        Assert.Equal(2, newWorkspace.Project.Lyrics.Branches.Length);
        Assert.Equal("source-main", newWorkspace.Project.Lyrics.Lines[0].Id);
        Assert.Equal("source-main", newWorkspace.Project.Lyrics.Branches[1].Lines[0].PrimaryLineId);
        var before = f.Workspace.Project;
        var missing = await f.Command("0", new ImportAssetCommandDto(asset.AssetId, new NewBranchLyricsDestinationDto("源", ProjectLyricBranchRole.Annotation)));
        Assert.Equal("SourceBranchRequired", missing.Error!.FailureCode);
        Assert.Equal("command.options.sourceBranchId", missing.Error.Field);
        var unknown = await f.Command("0", new ImportAssetCommandDto(asset.AssetId, new NewBranchLyricsDestinationDto("源", ProjectLyricBranchRole.Annotation), new(SourceBranchId: "gone")));
        Assert.Equal("SourceBranchNotFound", unknown.Error!.FailureCode);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
        string? previousSegment = null;
        for (int i = 0; i < 2; i++)
        {
            var result = Accepted(await f.Command(i.ToString(System.Globalization.CultureInfo.InvariantCulture), new ImportAssetCommandDto(asset.AssetId,
                new NewBranchLyricsDestinationDto("源" + i, ProjectLyricBranchRole.Annotation), new(SourceBranchId: "primary"))));
            LyricBranch branch = f.Workspace.Project.Lyrics.Branches[^1];
            RubyAnnotation ruby = Assert.Single(branch.Lines[0].RubyAnnotations);
            Assert.Equal(branch.Lines[0].Tokens[0].Id, ruby.BaseTokenId);
            Assert.NotEqual(previousSegment, ruby.Segments[0].Id); previousSegment = ruby.Segments[0].Id;
            Assert.InRange(ruby.Segments[0].Id.Length, 1, 128);
            Assert.Contains(result.ImportResult!.Report!.DiscardedFeatures, feature => feature.Contains("省略其它来源", StringComparison.Ordinal));
        }
        var translation = Accepted(await f.Command("2", new ImportAssetCommandDto(asset.AssetId,
            new ExistingLyricsDestinationDto("translation"), new(SourceBranchId: "source-translation"))));
        Assert.Equal("source-main", f.Translation.Lines[0].PrimaryLineId);
        Assert.Contains(translation.ImportResult!.Report!.Issues, issue => issue.Severity == LyricsImportIssueSeverity.Warning
            && issue.Message.Contains("SourceBranchLinkUnavailable", StringComparison.Ordinal));
        Assert.Equal(3, f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 全部来源导入一次撤销且保留已有主歌词和旧的单来源请求()
    {
        await using Fixture f = await Fixture.Create();
        string primary = f.Workspace.Project.Lyrics.PrimaryBranchId;
        int before = f.Workspace.Project.Lyrics.Branches.Length;
        var asset = await f.Resources.UploadAsync("client", "project.ttml", "application/ttml+xml", new MemoryStream(ProjectTtml()));
        var mixed = await f.Command("0", new ImportAssetCommandDto(asset.AssetId, new AllSourcesLyricsDestinationDto(), new(SourceBranchId: "primary")));
        Assert.Equal(BackendErrorCode.InvalidRequest, mixed.Error!.Code);
        Assert.Equal("command.options.sourceBranchId", mixed.Error.Field);
        Assert.Equal(0, f.Workspace.History.UndoCount);
        var imported = Accepted(await f.Command("0", new ImportAssetCommandDto(asset.AssetId, new AllSourcesLyricsDestinationDto())));
        Assert.Equal("lyrics.importAll", imported.UndoName);
        Assert.Equal(2, imported.ImportResult!.LineCount);
        Assert.Equal(before + 2, f.Workspace.Project.Lyrics.Branches.Length);
        Assert.Equal(primary, f.Workspace.Project.Lyrics.PrimaryBranchId);
        Assert.Equal(LyricBranchRole.Primary, f.Workspace.Project.Lyrics.PrimaryBranch!.Role);
        LyricBranch addedMain = f.Workspace.Project.Lyrics.Branches[before];
        LyricBranch addedTranslation = f.Workspace.Project.Lyrics.Branches[before + 1];
        Assert.NotEqual(LyricBranchRole.Primary, addedMain.Role);
        Assert.Equal(addedMain.Lines[0].Id, addedTranslation.Lines[0].PrimaryLineId);
        Assert.Equal(addedMain.Lines[0].Tokens[0].Id, Assert.Single(addedMain.Lines[0].RubyAnnotations).BaseTokenId);
        Assert.Contains(imported.ImportResult.Report!.Issues, issue => issue.Message.Contains("未替换工程已有来源", StringComparison.Ordinal));
        Accepted(await f.Command("1", new ProjectCommandDto("history.undo")));
        Assert.Equal(primary, f.Workspace.Project.Lyrics.PrimaryBranchId);
        Assert.Equal(before, f.Workspace.Project.Lyrics.Branches.Length);
        var single = Accepted(await f.Command("2", new ImportAssetCommandDto(asset.AssetId,
            new NewBranchLyricsDestinationDto("只要翻译", ProjectLyricBranchRole.Translation), new(SourceBranchId: "source-translation"))));
        Assert.Equal(1, single.ImportResult!.LineCount);
        Assert.Equal(before + 1, f.Workspace.Project.Lyrics.Branches.Length);
    }

    [Fact]
    public void AllSources只接受kind且不能用于粘贴()
    {
        ProjectCommandDto imported = JsonSerializer.Deserialize<ProjectCommandDto>("""{"type":"lyrics.importAsset","assetId":"asset","destination":{"kind":"allSources"}}""")!;
        Assert.IsType<AllSourcesLyricsDestinationDto>(Assert.IsType<ImportAssetCommandDto>(imported).Destination);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>("""{"type":"lyrics.importAsset","assetId":"asset","destination":{"kind":"allSources","name":"x"}}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>("""{"type":"lyrics.appendText","text":"甲","destination":{"kind":"allSources"}}"""));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    [InlineData("12")]
    public void SourceBranchId严格字符串且拒绝显式空值(string value)
    {
        string json = "{\"type\":\"lyrics.importAsset\",\"assetId\":\"asset\",\"destination\":{\"kind\":\"existing\",\"branchId\":\"primary\"},\"options\":{\"sourceBranchId\":" + value + "}}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(json));
    }

    [Fact]
    public async Task 导入信息保TTML完整来源与注音且选择预览不修改工程或消耗票据()
    {
        await using Fixture f = await Fixture.Create();
        var before = f.Workspace.Project;
        var asset = await f.Resources.UploadAsync("client", "project.ttml", "application/ttml+xml", new MemoryStream(ProjectTtml()));
        var plan = await f.Registry.ReadLyricsImportPlanAsync("client", new(asset.AssetId));
        Assert.True(plan.Accepted, plan.Error?.Message);
        Assert.Null(plan.Response!.PreparationError);
        Assert.Equal(2, plan.Response.SourceBranches.Length);
        Assert.Equal("源翻译", plan.Response.SourceBranches[1].Name);
        Assert.Equal(ProjectLyricBranchRole.Translation, plan.Response.SourceBranches[1].Role);
        Assert.Equal("primary", plan.Response.SelectedSourceBranchId);
        Assert.Equal("じ", Assert.Single(Assert.Single(plan.Response.PreviewLines).RubyAnnotations).Segments[0].Text);
        var translation = await f.Registry.ReadLyricsImportPlanAsync("client", new(asset.AssetId, new(SourceBranchId: "source-translation")));
        Assert.Equal("source-main", Assert.Single(translation.Response!.PreviewLines).PrimaryLineId);
        Assert.Same(before, f.Workspace.Project);
        Assert.Equal(0, f.Workspace.History.UndoCount);
        // Plans do not authorize imports and imports do not consume a plan id.
        var inaccessible = await f.Registry.ReadLyricsImportPlanAsync("other", new(asset.AssetId));
        Assert.False(inaccessible.Accepted);
        Accepted(await f.Command("0", new ImportAssetCommandDto(asset.AssetId,
            new ExistingLyricsDestinationDto("primary"), new(SourceBranchId: "primary"))));
        Assert.Equal(1, f.Workspace.History.UndoCount);
    }

    private static byte[] ProjectTtml()
    {
        LyricToken token = LyricToken.Create(new string('t', 128), "字");
        RubyAnnotation ruby = new() { Id = new string('r', 128), BaseTokenId = token.Id, Text = "じ",
            Segments = [RubyTextSegment.Create(new string('s', 128), "じ")] };
        LyricLine line = LyricLine.Create("source-main", "权威正文", new(0, 1_000_000), TimingStatus.Imported, [token]) with { RubyAnnotations = [ruby] };
        LyricsDocument source = LyricsDocument.Create([line]) with { Branches =
            [LyricBranch.Create("primary", "源主歌词", LyricBranchRole.Primary, [line]),
                LyricBranch.Create("source-translation", "源翻译", LyricBranchRole.Translation,
                    [LyricLine.Create("source-translation-line", "翻译") with { PrimaryLineId = line.Id }], "zh-CN", LyricBranchTimingMode.FollowPrimary)] };
        using MemoryStream output = new();
        new TtmlProjectExporter().Export(source, output, LyricsExportOptions.Default);
        return output.ToArray();
    }

    private sealed class CleanupFailureStream(byte[] bytes):MemoryStream(bytes,false)
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if(disposing) throw new IOException("controlled cleanup"); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 最终首行或后续大行实际投影拒绝且所有原子状态保留(bool later)
    {
        await using Fixture f=await Fixture.Create(); var source=f.Workspace.Project;
        var summary=await f.Registry.ReadSummaryAsync("client",f.Id);var history=await f.Registry.ReadHistoryAsync("client",f.Id);
        string text=(later ? "小行\n" : "")+new string('a',20000)+"\n不会写入";
        var asset=await f.Resources.UploadAsync("client","rows.txt","text/plain",new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var result=await f.Command("0",new ImportAssetCommandDto(asset.AssetId,new ExistingLyricsDestinationDto("primary")));
        Assert.Equal(BackendErrorCode.ResourceTooLarge,result.Error!.Code);Assert.Same(source,f.Workspace.Project);
        Assert.Equal(summary,await f.Registry.ReadSummaryAsync("client",f.Id));Assert.Equal(history,await f.Registry.ReadHistoryAsync("client",f.Id));
    }

    [Fact]
    public async Task owner准备期间即使关闭工程且回执期限过后仍由客户端pin阻止身份claim()
    {
        using TemporaryDirectory directory=new(); var clock=new ImportClock();
        const string secret="0123456789012345678901234567890123456789";
        var clients=new ClientRegistry("host",secret,new(){MaxClients=1,TokenLifetimeSeconds=1,LeaseLifetimeSeconds=1},clock);
        var client=clients.Register(secret,new()).Response!;
        var resources=new ResourceStore(new(){ResourceRoot=Path.Combine(directory.Path,"resources")},clock);
        var audio=await resources.UploadAsync(client.ClientId,"song.wav","audio/wav",new MemoryStream([1]));
        var lyrics=await resources.UploadAsync(client.ClientId,"song.lrc","text/plain",new MemoryStream("[00:01.00]旧\n"u8.ToArray()));
        WaveformPeakBuilder peaks=new();peaks.Append(new float[24000]);
        LoadedWaveform waveform=new(new AudioStreamInfo(3000000,8000,1,"pcm","wav"),peaks.Build(8000));
        await using var projects=new ProjectSessionRegistry("host",resources,new((_,_)=>Task.FromResult<LoadedWaveform?>(waveform)),clock,clients:clients);
        projects.ConfigurePersistence(new(){StateRoot=Path.Combine(directory.Path,"state")});
        var session=(await projects.CreateAsync(client.ClientId,new("pin",audio.AssetId,lyrics.AssetId))).Session!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        projects.ImportPreparationCheckpoint=async()=>{entered.SetResult();await release.Task;};
        var pending=projects.ExecuteCommandAsync(client.ClientId,session.ProjectSessionId,new("0",new AppendTextCommandDto("新",new ExistingLyricsDestinationDto("primary")),"import","host",clock.GetUtcNow().AddSeconds(1)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var close=await projects.StartCloseAsync(client.ClientId,session.ProjectSessionId,new("0","discard",ActiveJobs:"wait",Metadata:new("close","host",clock.GetUtcNow().AddSeconds(1))));
            Assert.True(close.Accepted,close.Error?.Message);
            using var closeWait=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while(projects.TryGet(client.ClientId,session.ProjectSessionId,out _) && !closeWait.IsCancellationRequested) await Task.Delay(20);
            Assert.False(projects.TryGet(client.ClientId,session.ProjectSessionId,out _));clock.Advance(TimeSpan.FromSeconds(3));
            int claims=0;
            var registration=await clients.RegisterAsync(secret,new(),id=>{claims++;return projects.TryReclaimClientAsync(id);});
            Assert.Equal(BackendErrorCode.ClientQuotaExceeded,registration.Error!.Code);Assert.Equal(0,claims);
        }
        finally{release.TrySetResult();projects.ImportPreparationCheckpoint=null;}
        Assert.Equal(BackendErrorCode.SessionClosing,(await pending).Error!.Code);
    }

    private sealed class ImportClock:TimeProvider
    {
        private DateTimeOffset _now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>_now;
        internal void Advance(TimeSpan time)=>_now+=time;
    }

    [Fact]
    public async Task 固定读取取消与清理同时失败保留原取消异常而不被Dispose遮蔽()
    {
        await using Fixture f=await Fixture.Create();var asset=await f.Resources.UploadAsync("client","cancel.txt","text/plain",new MemoryStream("新"u8.ToArray()));
        var primary=new OperationCanceledException("controlled read cancellation");
        f.Registry.ImportReadLeaseDecorator=read=>new(read.Asset,new ReadAndCleanupFailureStream(primary),read);
        var method=typeof(ProjectSessionRegistry).GetMethod("ReadFixedLyricsSourceAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var task=(Task<MakoKaraGenerator.Application.Editing.LyricsImportSource>)method.Invoke(f.Registry,["client",asset.AssetId])!;
        var observed=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);Assert.Same(primary,observed);
        Assert.IsType<IOException>(observed.Data["LyricsReadCleanupFailure"]);Assert.Equal(0,f.Workspace.History.UndoCount);
        f.Registry.ImportReadLeaseDecorator=null;
    }

    private sealed class ReadAndCleanupFailureStream(OperationCanceledException primary):MemoryStream("新"u8.ToArray(),false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)=>ValueTask.FromException<int>(primary);
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing)throw new IOException("controlled cleanup");}
    }

    private static ProjectCommandResponse Accepted(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, result.Error?.Message); return result.Response!; }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ResourceStore Resources = null!;
        internal ProjectSessionRegistry Registry = null!; internal ProjectSession Session = null!;
        internal string Id => Session.ProjectSessionId;
        internal ProjectWorkspace Workspace => Session.Workspace;
        internal LyricBranch Translation => Workspace.Project.Lyrics.Branches.Single(branch => branch.Id == "translation");
        internal void Checkpoint(Func<Task> checkpoint) => Registry.ImportPreparationCheckpoint=checkpoint;
        internal void Replace(Project project) => typeof(ProjectWorkspace).GetProperty("Project")!.SetValue(Workspace, project);
        internal ProjectCommandRequest Request(string revision, ProjectCommandDto command) => new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Registry.ExecuteCommandAsync("client", Id, Request(revision, command));
        internal static async Task<Fixture> Create(int receiptBytes=65536)
        {
            Fixture f = new(); ResourceStore resources = f.Resources = new(new() { ResourceRoot = f._directory.Path });
            var audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1]));
            var lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]主\n"u8.ToArray()));
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new("host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)),commandRetention:new() {MaxReceiptBytes=receiptBytes});
            f.Registry.ConfigurePersistence(new() {StateRoot=Path.Combine(f._directory.Path,"state")});
            f.Session = (await f.Registry.CreateAsync("client", new("branches", audio.AssetId, lyrics.AssetId))).Session!;
            var source = f.Workspace.Project;
            f.Replace(source with { Lyrics = source.Lyrics with { Branches = [source.Lyrics.Branches[0] with
            {
                Lines = [LyricLine.Create("main", "主")],
            }, LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
                [LyricLine.Create("translated", "译", tokens: [LyricToken.Create("translated-token", "译")]) with
                { RubyAnnotations = [RubyAnnotation.Create("ruby", "translated-token", "yi")] }],
                "zh-CN", LyricBranchTimingMode.FollowPrimary, new LyricStyleOverride { FontSizePx = 25 })] } });
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
