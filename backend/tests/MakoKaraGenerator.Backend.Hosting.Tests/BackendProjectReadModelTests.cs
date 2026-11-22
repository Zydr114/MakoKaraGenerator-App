using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendProjectReadModelTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    [Fact]
    public async Task 多来源分页保留稳定ID角色语言策略和主来源()
    {
        await using Fixture f = await Fixture.Create();
        Project before = f.Workspace.Project;
        var first = Accepted(await f.Registry.ReadLyricBranchesAsync("client", f.Id, "0", limit: 1));
        Assert.Equal("lead", first.PrimaryBranchId); Assert.Equal(3, first.TotalCount); Assert.Equal(1, first.NextOffset);
        Assert.Equal(new ProjectLyricBranchDto("lead", "主歌词", ProjectLyricBranchRole.Primary, "ja-JP", ProjectLyricTimingMode.Independent, 6), Assert.Single(first.Branches));
        var second = Accepted(await f.Registry.ReadLyricBranchesAsync("client", f.Id, "0", 1, 1));
        Assert.Equal(ProjectLyricTimingMode.FollowPrimary, Assert.Single(second.Branches).TimingMode);
        Assert.Equal(ProjectLyricBranchRole.Translation, second.Branches[0].Role);
        var last = Accepted(await f.Registry.ReadLyricBranchesAsync("client", f.Id, "0", 2, 1));
        Assert.Null(last.NextOffset); Assert.Equal(ProjectLyricTimingMode.None, last.Branches[0].TimingMode);
        var empty = Accepted(await f.Registry.ReadLyricBranchesAsync("client", f.Id, "0", 3));
        Assert.Empty(empty.Branches); Assert.Null(empty.NextOffset);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 编排读模型返回稳定树和程序默认容器并校验修订()
    {
        await using Fixture f = await Fixture.Create();
        var response = Accepted(await f.Registry.ReadOrchestrationAsync("client", f.Id, "0"));
        Assert.NotEmpty(response.Nodes);
        Assert.NotEmpty(response.Programs);
        Assert.All(response.Nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.Id)));
        Assert.All(response.Programs, program => Assert.False(string.IsNullOrWhiteSpace(program.DefaultContainerId)));
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadOrchestrationAsync("client", f.Id, "1")).Error!.Code);
    }

    [Fact]
    public async Task 编排读模型返回片段成员和调度上下文的完整投影()
    {
        await using Fixture f = await Fixture.Create();
        Project project = f.Workspace.Project;
        LyricDisplayProgram program = project.LyricPresentation.Programs[0];
        LyricDisplayClip clip = new()
        {
            Id = "clip-read-model", Name = null, LineIds = ["line-1", "line-2"], ContainerId = program.DefaultContainerId,
            ScheduleContext = new() { LineIds = ["line-0", "line-1", "line-2"], IsDefaultSegment = true,
                PreviousLineId = "previous", NextLineId = "next" },
        };
        f.Replace(project with
        {
            LyricPresentation = project.LyricPresentation with
            {
                Programs = project.LyricPresentation.Programs.SetItem(
                    project.LyricPresentation.Programs.IndexOf(program), program with { Clips = [clip] }),
            },
        });

        ProjectOrchestrationProgramDto model = Assert.Single(
            Accepted(await f.Registry.ReadOrchestrationAsync("client", f.Id, "0")).Programs,
            item => item.BranchId == program.BranchId);
        ProjectOrchestrationClipDto projected = Assert.Single(model.Clips!);
        Assert.Equal(clip.Id, projected.Id);
        Assert.Null(projected.Name);
        Assert.Equal(clip.LineIds, projected.LineIds);
        Assert.Equal(clip.ContainerId, projected.ContainerId);
        Assert.NotNull(projected.ScheduleContext);
        Assert.Equal(clip.ScheduleContext!.LineIds, projected.ScheduleContext!.LineIds);
        Assert.Equal(clip.ScheduleContext.IsDefaultSegment, projected.ScheduleContext.IsDefaultSegment);
        Assert.Equal(clip.ScheduleContext.PreviousLineId, projected.ScheduleContext.PreviousLineId);
        Assert.Equal(clip.ScheduleContext.NextLineId, projected.ScheduleContext.NextLineId);
    }

    [Fact]
    public async Task 正文不从元素重组且所有时间状态和完整注音元数据原样返回()
    {
        await using Fixture f = await Fixture.Create();
        var response = Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0"));
        Assert.Equal(6, response.Lines.Length);
        Assert.Equal(Enum.GetValues<ProjectLyricTimingStatus>(), response.Lines.Select(line => line.TimingStatus));
        Assert.Equal(new LyricTimeDto(null, null), response.Lines[0].Time);
        Assert.Equal(new LyricTimeDto("0", "9007199254740993"), response.Lines[1].Time);
        Assert.Equal(new LyricTimeDto("9", "8"), response.Lines[5].Time);
        ProjectLyricLineDto first = response.Lines[0];
        Assert.Equal("行の原文 🎵", first.Text); Assert.Equal("基", first.Tokens[0].Text);
        ProjectRubyAnnotationDto ruby = Assert.Single(first.RubyAnnotations);
        Assert.Equal("ruby", ruby.Id); Assert.Equal("token-0", ruby.BaseTokenId);
        Assert.Equal("き\u3099", ruby.Text); Assert.Equal("ja-JP", ruby.Language);
        Assert.Equal(0.75, ruby.RelativeScale); Assert.Equal(4.25, ruby.GapPx); Assert.Equal(ProjectRubyAlignment.End, ruby.Alignment);
        ProjectRubyTextSegmentDto segment = Assert.Single(ruby.Segments);
        Assert.Equal("き\u3099", segment.Text); Assert.Equal(new LyricTimeDto(null, null), segment.Time);
        Assert.Equal(ProjectLyricTimingStatus.Missing, segment.TimingStatus);
        Assert.Equal(ProjectLyricTimingStatus.Missing, first.Tokens[0].TimingStatus);
        var translated = Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, "translation", "0"));
        Assert.Equal("line-1", translated.Lines[0].PrimaryLineId);
        Assert.Equal(new LyricTimeDto(null, null), translated.Lines[0].Time);
    }

    [Fact]
    public async Task 页间写入和撤销不允许拼接旧修订且读取不修改历史()
    {
        await using Fixture f = await Fixture.Create();
        f.Replace(f.Baseline with { Lyrics = f.Baseline.Lyrics.ReplaceBranch(f.Baseline.Lyrics.Branches[0] with
        {
            Lines = [LyricLine.Create("line-0", "first"), LyricLine.Create("line-1", "second")],
        }) });
        var first = Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, "primary", "0", limit: 1));
        Assert.Equal(1, first.NextOffset);
        var change = await f.Registry.ExecuteCommandAsync("client", f.Id, f.Command("0", new("lyrics.setTimeRange", new("primary", "line-0"), "0", "1000000")));
        Assert.True(change.Accepted, change.Error?.Message); Assert.Equal("1", change.Response!.ProjectRevision);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadLyricsWindowAsync("client", f.Id, "primary", "0", 1, 1)).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadLyricBranchesAsync("client", f.Id, "0")).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadSettingsAsync("client", f.Id, "0")).Error!.Code);
        var current = Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, "primary", "1"));
        Assert.Equal(new LyricTimeDto("0", "1000000"), current.Lines[0].Time);
        Assert.Equal(ProjectLyricTimingStatus.Edited, current.Lines[0].TimingStatus);
        Assert.Equal(1, f.Workspace.History.UndoCount);
        Assert.True((await f.Registry.ExecuteCommandAsync("client", f.Id, f.Command("1", new("history.undo")))).Accepted);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadLyricsWindowAsync("client", f.Id, "primary", "0")).Error!.Code);
        Assert.Equal(new LyricTimeDto(null, null), Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, "primary", "2")).Lines[0].Time);
        Assert.Equal(1, f.Workspace.History.RedoCount);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("-1")] [InlineData("+0")] [InlineData(" 0")]
    [InlineData("1.0")] [InlineData("9223372036854775808")]
    public async Task 非法修订返回结构化拒绝(string? revision)
    {
        await using Fixture f = await Fixture.Create();
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.ReadSettingsAsync("client", f.Id, revision)).Error!.Code);
    }

    [Theory]
    [InlineData(-1, 1)] [InlineData(7, 1)] [InlineData(0, 0)] [InlineData(0, 101)]
    public async Task 非法窗口拒绝且不会截断或修正(int offset, int limit)
    {
        await using Fixture f = await Fixture.Create();
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0", offset, limit)).Error!.Code);
    }

    [Fact]
    public async Task 不存在与跨客户端会话中立拒绝而分支单独报告()
    {
        await using Fixture f = await Fixture.Create();
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadSettingsAsync("other", f.Id, "0")).Error!.Code);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadLyricBranchesAsync("client", "missing", "0")).Error!.Code);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadLyricsWindowAsync("other", f.Id, "lead", "0")).Error!.Code);
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Registry.ReadLyricsWindowAsync("client", f.Id, "missing", "0")).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.ReadLyricsWindowAsync("client", f.Id, "", "0")).Error!.Code);
    }

    [Fact]
    public async Task Core支持的长来源ID可由列表发现并精确读取()
    {
        await using Fixture f = await Fixture.Create();
        string branchId = new('x', 129);
        Project original = f.Workspace.Project;
        LyricBranch branch = original.Lyrics.Branches[0] with { Id = branchId };
        f.Replace(original with { Lyrics = original.Lyrics with
        {
            PrimaryBranchId = branchId, Branches = original.Lyrics.Branches.SetItem(0, branch),
        } });
        Assert.Equal(branchId, Accepted(await f.Registry.ReadLyricBranchesAsync("client", f.Id, "0")).PrimaryBranchId);
        Assert.Equal(branchId, Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, branchId, "0")).BranchId);
    }

    [Fact]
    public async Task 标量设置保留有理帧率透明底色和超JS安全整数时长()
    {
        await using Fixture f = await Fixture.Create();
        f.Replace(f.Workspace.Project with { Composition = Composition.Create(641, 359, new(30000, 1001), new(1, 2, 3, 4), 9007199254740993) });
        var settings = Accepted(await f.Registry.ReadSettingsAsync("client", f.Id, "0"));
        Assert.Equal((641, 359, 30000, 1001), (settings.CanvasWidth, settings.CanvasHeight, settings.FrameRateNumerator, settings.FrameRateDenominator));
        Assert.Equal((1, 2, 3, 4), ((int)settings.BackgroundRed, (int)settings.BackgroundGreen, (int)settings.BackgroundBlue, (int)settings.BackgroundAlpha));
        Assert.Equal("9007199254740993", settings.OutputDurationUs); Assert.Equal("3000000", settings.AudioDurationUs);
        f.Replace(f.Workspace.Project with { Composition = new() });
        Assert.Null(Accepted(await f.Registry.ReadSettingsAsync("client", f.Id, "0")).OutputDurationUs);
    }

    [Fact]
    public async Task 读取等待写者并在取消或关闭后释放而不回传旧内容()
    {
        await using Fixture f = await Fixture.Create();
        SemaphoreSlim writer = f.Session.Writer;
        await writer.WaitAsync();
        using CancellationTokenSource cancelled = new();
        Task<BackendOperationResult<ProjectSettingsResponse>> pending = f.Registry.ReadSettingsAsync("client", f.Id, "0", cancelled.Token);
        Assert.False(pending.IsCompleted); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Task<BackendOperationResult<ProjectSettingsResponse>> closed = f.Registry.ReadSettingsAsync("client", f.Id, "0");
        Assert.False(closed.IsCompleted);
        f.Session.IsClosed = true;
        writer.Release();
        Assert.Equal(BackendErrorCode.SessionNotFound, (await closed).Error!.Code);
    }

    [Fact]
    public async Task JSON预算计入转义字节且单行不截断并可缩小跨行窗口()
    {
        await using Fixture f = await Fixture.Create();
        f.SetLines([LyricLine.Create("big", new string('汉', 180000))]);
        Project before = f.Workspace.Project;
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0")).Error!.Code);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
        f.SetLines([LyricLine.Create("a", new string('a', 600000)), LyricLine.Create("b", new string('b', 600000))]);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0")).Error!.Code);
        Assert.Single(Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0", limit: 1)).Lines);
        Assert.Single(Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0", 1, 1)).Lines);
        f.SetLines([LyricLine.Create("a", new string('a', ProjectReadModelLimits.MaxResponseBytes + 1))]);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0", limit: 1)).Error!.Code);
    }

    [Fact]
    public async Task 项数预算合计行元素注音且拒绝不保留状态()
    {
        await using Fixture f = await Fixture.Create();
        var tokens = Enumerable.Range(0, 10000).Select(i => LyricToken.Create("t" + i.ToString(CultureInfo.InvariantCulture), "")).ToArray();
        var ruby = tokens.Select(token => RubyAnnotation.Create("r" + token.Id, token.Id, "r")).ToArray();
        f.SetLines([LyricLine.Create("items", "", tokens: tokens) with { RubyAnnotations = [.. ruby] }]);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0")).Error!.Code);
        // Keep a small exact response to prove the rejection did not retain state in the next read.
        f.SetLines([LyricLine.Create("ok", "")]);
        Assert.Single(Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0")).Lines);
    }

    [Fact]
    public async Task 传输省略空值并保留missing空时间和字符串枚举()
    {
        await using Fixture f = await Fixture.Create();
        var response = Accepted(await f.Registry.ReadLyricsWindowAsync("client", f.Id, "lead", "0"));
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(response, Json));
        JsonElement first = document.RootElement.GetProperty("lines")[0];
        Assert.Equal("{}", first.GetProperty("time").GetRawText()); Assert.Equal("missing", first.GetProperty("timingStatus").GetString());
        Assert.False(first.TryGetProperty("primaryLineId", out _)); Assert.False(document.RootElement.TryGetProperty("nextOffset", out _));
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("projectRevision").ValueKind);
    }

    private static T Accepted<T>(BackendOperationResult<T> result)
    {
        Assert.True(result.Accepted, result.Error?.Message); return result.Response!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal Project Baseline = null!;
        internal string Id => Session.ProjectSessionId;
        internal ProjectWorkspace Workspace => Session.Workspace;
        internal void Replace(Project project) => typeof(ProjectWorkspace).GetProperty("Project")!.SetValue(Workspace, project);
        internal void SetLines(LyricLine[] lines) => Replace(Workspace.Project with
        {
            Lyrics = Workspace.Project.Lyrics.ReplaceBranch(Workspace.Project.Lyrics.Branches[0] with { Lines = [.. lines] }),
        });
        internal ProjectCommandRequest Command(string revision, ProjectCommandDto command) =>
            new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));

        internal static async Task<Fixture> Create()
        {
            Fixture f = new();
            ResourceStore resources = new(new() { ResourceRoot = f._directory.Path });
            StoredAsset audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1]));
            StoredAsset lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new("host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)));
            f.Session = (await f.Registry.CreateAsync("client", new("读取工程", audio.AssetId, lyrics.AssetId))).Session!;
            f.Baseline = f.Workspace.Project;
            LyricLine[] lines = [.. Enum.GetValues<TimingStatus>().Select((status, index) => LyricLine.Create(
                "line-" + index.ToString(CultureInfo.InvariantCulture), index == 0 ? "行の原文 🎵" : "line",
                status == TimingStatus.Missing ? null : status == TimingStatus.Invalid ? new TimeRange(9, 8) : new TimeRange(0, 9007199254740993), status,
                [LyricToken.Create("token-" + index.ToString(CultureInfo.InvariantCulture), "基")]))];
            lines[0] = lines[0] with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token-0", "き\u3099", "ja-JP", 0.75, 4.25, RubyAlignment.End)] };
            f.Replace(f.Workspace.Project with
            {
                Lyrics = new LyricsDocument
                {
                    PrimaryBranchId = "lead",
                    Branches = [LyricBranch.Create("lead", "主歌词", LyricBranchRole.Primary, lines, "ja-JP"),
                        LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
                            [LyricLine.Create("translated", "译文") with { PrimaryLineId = "line-1" }], "zh-CN", LyricBranchTimingMode.FollowPrimary),
                        LyricBranch.Create("annotation", "读音", LyricBranchRole.Annotation, timingMode: LyricBranchTimingMode.None)],
                },
            });
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
