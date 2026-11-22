using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendLineLocalTransformTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ProjectLineLocalTransformDto Transform = new(.125, -.25, 15, 1.5);

    [Fact]
    public async Task 本句微调一次历史与精确投影撤销重做且保留源时间和预览闭包()
    {
        await using Fixture f = await Fixture.Create();
        Project original = f.Workspace.Project;
        var changed = Accept(await f.Command("0", new SetLineLocalTransformCommandDto("primary", f.LineId, Transform)));
        Assert.True(changed.Changed); Assert.Equal("1", changed.ProjectRevision);
        Assert.Equal(1, f.Workspace.History.UndoCount); Assert.Same(original.Lyrics, f.Workspace.Project.Lyrics);
        Assert.Equal(new LineLocalTransformCommandResultDto("primary", f.LineId, Transform, true), changed.LineLocalTransformResult);
        Assert.Contains("lineLocalTransform", changed.ChangeSet.Areas); Assert.Contains("renderInputs", changed.ChangeSet.Areas);
        var read = (await f.Read("1")).Response!; Assert.Equal(Transform, read.Transform);
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(f.Workspace.Project);
        var decoded = StaticProjectSnapshotCodec.Decode(StaticProjectSnapshotCodec.Encode(snapshot, StaticProjectSnapshotCodec.SelectProfile(snapshot)));
        Assert.Equal(new LyricLocalTransform { OffsetXRatio = .125, OffsetYRatio = -.25, RotationDegrees = 15, Scale = 1.5 }, decoded.LyricPresentation!.FindLine(f.LineId)!.LocalTransform);
        Accept(await f.Command("1", new("history.undo"))); Assert.Same(original, f.Workspace.Project);
        Assert.Null((await f.Read("2")).Response!.Transform);
        var noop = Accept(await f.Command("2", new SetLineLocalTransformCommandDto("primary", f.LineId, null)));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo); Assert.Equal("2", noop.ProjectRevision);
        Assert.Same(original, f.Workspace.Project);
        Accept(await f.Command("2", new("history.redo"))); Assert.Equal(Transform, (await f.Read("3")).Response!.Transform);
        var same = Accept(await f.Command("3", new SetLineLocalTransformCommandDto("primary", f.LineId, Transform)));
        Assert.False(same.Changed); Assert.Equal(1, f.Workspace.History.UndoCount);
        var clear = Accept(await f.Command("3", new SetLineLocalTransformCommandDto("primary", f.LineId, null)));
        Assert.True(clear.Changed); Assert.Null(clear.LineLocalTransformResult!.Transform);
        Assert.Same(original.Lyrics, f.Workspace.Project.Lyrics);
    }

    [Fact]
    public async Task 明确中性值与清除不同且过期修订目标数值失败保工程历史和重做()
    {
        await using Fixture f = await Fixture.Create();
        Accept(await f.Command("0", new SetLineLocalTransformCommandDto("primary", f.LineId, new(0, 0, 0, 1))));
        Assert.NotNull((await f.Read("1")).Response!.Transform);
        Accept(await f.Command("1", new("history.undo"))); Project before = f.Workspace.Project;
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Command("0", new SetLineLocalTransformCommandDto("primary", f.LineId, null))).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Read("0")).Error!.Code);
        foreach (var command in new SetLineLocalTransformCommandDto[]
        {
            new("wrong", f.LineId, Transform), new("primary", "wrong", Transform),
            new("primary", f.LineId, new(double.NaN, 0, 0, 1)),
            new("primary", f.LineId, new(0, double.PositiveInfinity, 0, 1)),
            new("primary", f.LineId, new(0, 0, double.NegativeInfinity, 1)),
            new("primary", f.LineId, new(0, 0, 0, 0)), new("primary", f.LineId, new(0, 0, 0, -1)),
        })
        {
            var result = await f.Command("2", command); Assert.False(result.Accepted);
            Assert.Same(before, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount); Assert.Equal(1, f.Workspace.History.RedoCount);
        }
        Assert.Equal("TargetStale", (await f.Registry.ReadLineLocalTransformAsync("client", f.Id, "2", "wrong", f.LineId)).Error!.FailureCode);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadLineLocalTransformAsync("foreign", f.Id, "2", "primary", f.LineId)).Error!.Code);
        var results = await Task.WhenAll(f.Command("2", new SetLineLocalTransformCommandDto("primary", f.LineId, Transform)),
            f.Command("2", new SetLineLocalTransformCommandDto("primary", f.LineId, new(0, 0, 0, 2))));
        Assert.Single(results, item => item.Accepted); Assert.Single(results, item => item.Error?.Code == BackendErrorCode.RevisionConflict);
    }

    [Fact]
    public async Task 清除微调保留本句动画且展示校验失败完整拒绝()
    {
        await using Fixture f = await Fixture.Create();
        Project source = f.Workspace.Project;
        var animation = new LineAnimation { DurationUs = 800000, StartScale = .8f };
        typeof(ProjectWorkspace).GetProperty("Project")!.SetValue(f.Workspace, source with
        {
            LyricPresentation = source.LyricPresentation with
            {
                LineOverrides = [new() { BranchId = "primary", LineId = f.LineId, EnterAnimation = animation }],
            },
        });
        Accept(await f.Command("0", new SetLineLocalTransformCommandDto("primary", f.LineId, Transform)));
        Accept(await f.Command("1", new SetLineLocalTransformCommandDto("primary", f.LineId, null)));
        Assert.Same(animation, f.Workspace.Project.LyricPresentation.FindLine(f.LineId)!.EnterAnimation);
        var valid = f.Workspace.Project;
        typeof(ProjectWorkspace).GetProperty("Project")!.SetValue(f.Workspace, valid with
        {
            LyricPresentation = valid.LyricPresentation with
            {
                LineOverrides = valid.LyricPresentation.LineOverrides.Add(new()
                { BranchId = "primary", LineId = "orphan", LocalTransform = new() { Scale = -1 } }),
            },
        });
        Project before = f.Workspace.Project;
        var failed = await f.Command("2", new SetLineLocalTransformCommandDto("primary", f.LineId, Transform));
        Assert.False(failed.Accepted); Assert.Equal("PresentationInvalid", failed.Error!.FailureCode);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(2, f.Workspace.History.UndoCount);
    }

    [Fact]
    public void 严格JSON必须显式完整有限变换或null且null投影总是输出()
    {
        string valid = "{\"type\":\"orchestration.setLineLocalTransform\",\"branchId\":\"primary\",\"lineId\":\"line\",\"transform\":{\"offsetXRatio\":0,\"offsetYRatio\":-0.2,\"rotationDegrees\":10,\"scale\":1}}";
        var value = Assert.IsType<SetLineLocalTransformCommandDto>(JsonSerializer.Deserialize<ProjectCommandDto>(valid, Json));
        Assert.Equal(-.2, value.Transform!.OffsetYRatio);
        Assert.IsType<SetLineLocalTransformCommandDto>(JsonSerializer.Deserialize<ProjectCommandDto>(JsonSerializer.Serialize<ProjectCommandDto>(value, Json), Json));
        foreach (string invalid in new[]
        {
            valid.Replace("\"scale\":1", "\"scale\":0"), valid.Replace("\"scale\":1", "\"scale\":1e400"),
            valid.Replace("\"scale\":1", "\"scale\":\"1\""), valid.Replace("\"scale\":1", "\"scale\":null"),
            valid.Replace(",\"scale\":1", ""), valid.Replace("\"scale\":1", "\"scale\":1,\"scale\":2"),
            valid.Replace("\"scale\":1", "\"scale\":1,\"opacity\":1"), valid.Replace("\"branchId\":\"primary\",", ""),
            "{\"type\":\"orchestration.setLineLocalTransform\",\"branchId\":\"primary\",\"lineId\":\"line\"}",
        }) Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(invalid, Json));
        JsonSerializerOptions omit = new(Json) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        using var read = JsonDocument.Parse(JsonSerializer.Serialize(new ProjectLineLocalTransformResponse("s", "0", "b", "l", null), omit));
        Assert.Equal(JsonValueKind.Null, read.RootElement.GetProperty("transform").ValueKind);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(new LineLocalTransformCommandResultDto("b", "l", null, false), omit));
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("transform").ValueKind);
    }

    private static ProjectCommandResponse Accept(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, result.Error?.Message); return result.Response!; }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal string Id => Session.ProjectSessionId;
        internal ProjectWorkspace Workspace => Session.Workspace;
        internal string LineId => Workspace.Project.Lyrics.Lines[0].Id;
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Registry.ExecuteCommandAsync("client", Id,
            new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1)));
        internal Task<BackendOperationResult<ProjectLineLocalTransformResponse>> Read(string revision) => Registry.ReadLineLocalTransformAsync("client", Id, revision, "primary", LineId);
        internal static async Task<Fixture> Create()
        {
            Fixture f = new(); ResourceStore resources = new(new() { ResourceRoot = f._directory.Path });
            var audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1]));
            var lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]主\n"u8.ToArray()));
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new("host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)));
            f.Session = (await f.Registry.CreateAsync("client", new("transforms", audio.AssetId, lyrics.AssetId))).Session!;
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
