using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendProjectImageExportJobTests
{
    [Theory]
    [InlineData(BackgroundKind.Image)]
    [InlineData(BackgroundKind.Video)]
    public async Task 固定媒体导出复用原任务与报告并保持私有副本直到执行器排水(BackgroundKind kind)
    {
        await using Fixture f = await Fixture.Create(runtime: true, kind: kind);
        JobResponse started = await f.Start();
        Call call = await f.Executor.Next();
        Assert.Equal(ProjectRuntimeInputCodec.Profile, started.Input!.SourceProfile);
        Assert.Equal(WorkerProtocol.ComputeSha256(call.EncodedImage), started.Input.SourceSha256);
        Assert.Null(started.ExportInput!.TaskProfile);
        var source = ProjectRuntimeInputCodec.Decode(call.EncodedImage);
        Assert.Equal(kind, source.Grant!.Kind);
        Assert.NotEqual(f.Session.Workspace.Project.Background.Path, source.Grant.FilePath);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(source.Grant.FilePath));
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        Assert.Equal(f.BaselineReferenceCount + 1, f.ReferenceCount);
        call.Release.TrySetResult(Pass());
        Assert.Equal("succeeded", (await f.Terminal(started.JobId)).State);
        Assert.False(File.Exists(source.Grant.FilePath));
        Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
        byte[] report = f.Registry.ReadExportResult("client", started.JobId).Response!;
        await f.Registry.DisposeAsync();
        f.Restart();
        Assert.Equal(report, f.Registry.ReadExportResult("local-bootstrap", started.JobId, true).Response);
        f.AssertScheduler(0, 0);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("close")]
    [InlineData("identity")]
    [InlineData("owner")]
    public async Task 固定媒体导出二次Writer复核工程变更且拒绝后释放全部租约(string mutation)
    {
        await using Fixture f = await Fixture.Create(runtime: true, kind: BackgroundKind.Video);
        (Task<BackendOperationResult<JobResponse>> pending, Task writerWaiter) = f.StartBehindCaptureGate();
        await writerWaiter.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            switch (mutation)
            {
                case "revision": SetProjectRevision(f.Session, 1); break;
                case "close": f.Session.ClosePending = true; break;
                case "identity": f.ReplaceSessionIdentity(); break;
                case "owner": SetOwner(f.Session, "foreign"); break;
            }
        }
        finally { f.Session.Writer.Release(); }
        try
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Accepted);
            Assert.Equal(mutation switch { "revision" => BackendErrorCode.RevisionConflict,
                "close" => BackendErrorCode.SessionClosing, _ => BackendErrorCode.SessionNotFound }, result.Error!.Code);
            Assert.Equal(0, f.Executor.Calls);
            Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
            Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
            Assert.Equal(0, f.ArtifactUsedBytes);
            f.AssertScheduler(0, 0);
        }
        finally
        {
            f.RestoreSessionIdentity(); SetOwner(f.Session, "client"); SetProjectRevision(f.Session, 0); f.Session.ClosePending = false;
        }
    }

    [Fact]
    public async Task 固定媒体导出排队取消和关闭须等待执行器排水才删除副本()
    {
        await using Fixture f = await Fixture.Create(runtime: true, kind: BackgroundKind.Video);
        f.Executor.HoldCancellationDrain = true;
        JobResponse started = await f.Start();
        Call active = await f.Executor.Next();
        string file = ProjectRuntimeInputCodec.Decode(active.EncodedImage).Grant!.FilePath;
        JobResponse queued = await f.Start();
        Assert.Equal("queued", queued.State);
        Assert.True(f.Registry.CancelJob("client", queued.JobId).Accepted);
        Assert.Equal("cancelled", (await f.Terminal(queued.JobId)).State);
        Assert.Equal(1, f.Executor.Calls);
        var close = await f.Registry.StartCloseAsync("client", f.SessionId,
            new("0", "discard", ActiveJobs: "cancelAndWait", Metadata: f.Meta()));
        Assert.True(close.Accepted);
        await active.CancellationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(file));
        Assert.Equal("running", f.Registry.ReadJob("client", started.JobId).Response!.State);
        active.Drain.TrySetResult();
        Assert.Equal("cancelled", (await f.Terminal(started.JobId)).State);
        Assert.Equal("succeeded", (await f.Terminal(close.Response!.JobId)).State);
        Assert.False(File.Exists(file));
        Assert.Equal(0, f.ReferenceCount);
        f.AssertScheduler(0, 0);
    }

    // M13 C1：三条导出入口（固定媒体运行输入、图片背景、纯色通用）对实际使用 SDK v2 的工程，
    // 未选 GPU 时都在登记任务、取输入和调用执行器之前拒绝。
    [Theory]
    [InlineData("runtime")]
    [InlineData("image")]
    [InlineData("generic")]
    public async Task SDK2工程未选GPU的导出在登记任务与取输入前拒绝(string path)
    {
        await using Fixture f = await Fixture.Create(runtime: path != "image", kind: BackgroundKind.Image);
        Project project = SdkV2ProjectFixture.WithEnabledV2Instance(f.Session.Workspace.Project);
        if (path == "generic") project = project with { Background = new BackgroundSource() };
        f.Session.Workspace.Replace(project);
        BackendOperationResult<JobResponse> result = await f.StartOperation();
        Assert.False(result.Accepted);
        Assert.Equal(BackendErrorCode.CommandNotSupported, result.Error!.Code);
        Assert.Equal("renderBackend", result.Error.Field);
        Assert.Equal("PackageBackendUnsupported", result.Error.FailureCode);
        Assert.Equal(0, f.Executor.Calls);
        Assert.Empty((await f.Registry.ReadClientStateAsync("client")).Jobs!);
        Assert.Equal(f.BaselineReferenceCount, f.ReferenceCount);
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        Assert.Equal(0, f.ArtifactUsedBytes);
        f.AssertScheduler(0, 0);
    }
}
