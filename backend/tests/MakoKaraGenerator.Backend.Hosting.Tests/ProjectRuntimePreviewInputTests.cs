using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class ProjectRuntimePreviewInputTests
{
    [Fact]
    public async Task 固定媒体预览会话在调用者归还输入后仍持有文件且关闭Worker后释放()
    {
        using TemporaryDirectory directory = new();
        var (project, prepared) = await Prepare(directory.Path);
        Assert.False(prepared.GraphPixelChecks); // 正式项目预览不为检查读回中间产物。
        string path = prepared.Grant!.FilePath;
        var factory = new WorkerPackageRenderFrameSessionFactory(Options());
        await using (var probe = new WorkerProcessProxy(Options()))
            Assert.Equal(ProjectRuntimeInputWorkerContract.Profile, (await probe.GetRuntimeInputCapabilitiesAsync(TimeSpan.FromSeconds(20))).RuntimeInputProfile);
        var bound = factory.Bind(prepared);
        await using var session = await bound.CreateAsync(prepared.Snapshot);
        prepared.Dispose();
        Assert.True(File.Exists(path));
        File.Delete(project.Workspace.Project.Background.Path!);
        foreach (bool transparent in new[] { false, true })
        {
            using var frame = await session.RenderAsync(RenderRequest.At(prepared.Snapshot, 0) with
            { Target = new() { PixelWidth = 64, PixelHeight = 36 }, TransparentBackground = transparent });
            Assert.Equal(64 * 36 * 4, frame.PixelMemory.Length);
            Assert.False(frame.DiagnosticSnapshot.HasError);
            if (!transparent) Assert.Contains(frame.PixelMemory.ToArray(), value => value != 0);
        }
        await session.DisposeAsync();
        Assert.False(File.Exists(path));
        await project.DrainPinnedInputReadsAsync();
    }

    [Fact]
    public async Task 候选测量在注册表闸门取得独立引用并跨预览关闭保留固定媒体()
    {
        using TemporaryDirectory directory = new();
        var (_, prepared) = await Prepare(directory.Path);
        string path = prepared.Grant!.FilePath;
        await using var registry = new BackendPreviewSessionRegistry(new(), new(), new NeverEncoder());
        BackendPreviewPinnedInput input = new(new("editCandidate", "project", "candidate-fixed"), prepared.Snapshot, new NeverFactory(), prepared)
        { PreparedInput = prepared, VerifiedProfile = prepared.SourceProfile,
            EditCandidate = new("project", "0", "candidate-fixed", new([], [], [], [], false), []) };
        var created = registry.Create("client", new(input.Source, new(64, 36), PreviewKind: "editCandidate", Committable: false), input);
        Assert.True(created.Accepted, created.Error?.Message);
        var session = created.Status!.Session;
        var read = registry.ReadEditCandidateInput("client", "project", session.PreviewId, session.PreviewGeneration, "candidate-fixed");
        Assert.True(read.Accepted);
        Assert.NotSame(input, read.Response);
        Assert.NotNull(read.Response!.RetentionLease);
        Assert.True((await registry.CloseAsync("client", session.PreviewId, session.PreviewGeneration, "candidate-fixed")).Accepted);
        Assert.True(File.Exists(path));
        Assert.False(registry.ReadEditCandidateInput("client", "project", session.PreviewId, session.PreviewGeneration, "candidate-fixed").Accepted);
        read.Response.RetentionLease!.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task 准备后的媒体被篡改时真实Worker打开拒绝并排水()
    {
        using TemporaryDirectory directory = new();
        var (_, prepared) = await Prepare(directory.Path);
        using (prepared)
        {
            string path = prepared.Grant!.FilePath;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await using var worker = new WorkerProcessProxy(Options());
            var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.OpenRuntimeInputSessionAsync(prepared.SourceBytes, TimeSpan.FromSeconds(20)));
            Assert.Equal("ProjectRuntimeSourceChanged", failure.Code);
            Assert.Null(worker.ProcessId);
        }
    }

    internal static async Task<(ProjectSession Session, BackendPreparedProjectRuntimeInput Input)> Prepare(string root)
    {
        using SKBitmap bitmap = new(8, 8); bitmap.Erase(new SKColor(30, 90, 180, 255));
        using SKImage image = SKImage.FromBitmap(bitmap); using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = encoded.ToArray();
        MediaFileRevision media = new(WorkerProtocol.ComputeSha256(bytes), bytes.Length, "image.png");
        string path = Path.Combine(root, media.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var project = ProjectWorkspace.CreateNew("runtime").Project with {
            Composition = new() { CanvasWidth = 64, CanvasHeight = 36 },
            Background = new() { Kind = BackgroundKind.Image, Path = path, Media = media } };
        ProjectSession session = new("project", "host", "client", ProjectWorkspace.Adopt(project), "", 0,
            TimeProvider.System, new(), new("host", 100)) { PinnedInputDirectory = root };
        var capture = await session.CapturePreviewInputAsync("client", "0", new NeverFactory(), null, true);
        Assert.Null(capture.Error);
        using var lease = capture.RuntimeInput!;
        var result = await lease.PrepareAsync();
        Assert.True(result.Accepted, result.Error?.Message);
        Assert.Same(capture.Input!.Snapshot, result.Response!.Snapshot);
        Assert.True(session.DrainPinnedInputReadsAsync().IsCompleted);
        return (session, result.Response);
    }

    internal static WorkerProcessOptions Options()
    {
        string path = WorkerBinaryLocator.LocateBackendWorkerDll();
        Assert.True(File.Exists(path)); return new("dotnet", [path]);
    }

    private sealed class NeverFactory : IRenderFrameSessionFactory
    {
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class NeverEncoder : IBackendPreviewPngEncoder
    {
        public byte[] Encode(BackendPreviewFrame frame) => throw new InvalidOperationException();
    }
}
