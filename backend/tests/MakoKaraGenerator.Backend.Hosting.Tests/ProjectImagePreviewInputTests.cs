using System.Security.Cryptography;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class ProjectImagePreviewInputTests
{
    [Fact]
    public async Task 图片捕获同步取得引用不读文件且精确修订与所有者先行()
    {
        using TemporaryDirectory directory = new();
        ProjectSession session = Session(directory.Path, [1, 2, 3]);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await Capture(session, "foreign", "0")).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await Capture(session, "client", "1")).Error!.Code);
        Assert.True(session.DrainPinnedInputReadsAsync().IsCompleted);
        BackendPreviewInputCaptureResult result = await Capture(session);
        using BackendProjectImageReadLease lease = result.ImageSource!;
        Assert.Null(result.Error); Assert.NotNull(lease);
        Assert.False(session.DrainPinnedInputReadsAsync().IsCompleted);
        Assert.True(session.Writer.Wait(0)); session.Writer.Release();
        Assert.Equal("ProjectImageSourceUnavailable", (await lease.ReadAsync()).Error!.FailureCode);
        Assert.True(session.DrainPinnedInputReadsAsync().IsCompleted);
    }

    [Fact]
    public async Task 默认旧捕获不会隐式取得图片读取引用()
    {
        using TemporaryDirectory directory = new();
        ProjectSession session = Session(directory.Path, [1]);
        BackendPreviewInputCaptureResult result = await session.CapturePreviewInputAsync("client", "0", new NeverFactory());
        Assert.Null(result.ImageSource); Assert.True(session.DrainPinnedInputReadsAsync().IsCompleted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 图片声明大小或同长度内容篡改稳定拒绝(bool changeSize)
    {
        using TemporaryDirectory directory = new();
        ProjectSession session = Session(directory.Path, [1, 2, 3]);
        Write(session, changeSize ? [1, 2] : [3, 2, 1]);
        BackendPreviewInputCaptureResult capture = await Capture(session);
        BackendOperationResult<byte[]> result = await capture.ImageSource!.ReadAsync();
        Assert.False(result.Accepted); Assert.Equal(BackendErrorCode.CommandNotSupported, result.Error!.Code);
        Assert.Equal("ProjectImageSourceChanged", result.Error.FailureCode); Assert.Equal("background", result.Error.Field);
        Assert.DoesNotContain(directory.Path, result.Error.Message, StringComparison.Ordinal);
        Assert.True(session.DrainPinnedInputReadsAsync().IsCompleted);
    }

    [Fact]
    public async Task 图片读取完成后内容独立于原文件且释放引用()
    {
        using TemporaryDirectory directory = new();
        byte[] original = [10, 20, 30]; ProjectSession session = Session(directory.Path, original); Write(session, original);
        BackendPreviewInputCaptureResult capture = await Capture(session);
        BackendOperationResult<byte[]> result = await capture.ImageSource!.ReadAsync();
        Assert.True(result.Accepted); Assert.Equal(original, result.Response);
        Write(session, [0]); Directory.Delete(directory.Path, true);
        Assert.Equal(original, result.Response); Assert.True(session.DrainPinnedInputReadsAsync().IsCompleted);
        capture.ImageSource.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capture.ImageSource.ReadAsync());
    }

    [Fact]
    public async Task 图片读取取消仍释放短期引用且不可再次读取()
    {
        using TemporaryDirectory directory = new();
        byte[] bytes = [1, 2, 3]; ProjectSession session = Session(directory.Path, bytes); Write(session, bytes);
        BackendPreviewInputCaptureResult capture = await Capture(session);
        using BackendProjectImageReadLease lease = capture.ImageSource!;
        Assert.False(session.DrainPinnedInputReadsAsync().IsCompleted);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.ReadAsync(cancellation.Token));
        Assert.True(session.DrainPinnedInputReadsAsync().IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.ReadAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 文件或父目录链接拒绝且不读取目标(bool parent)
    {
        if (OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new(); using TemporaryDirectory target = new();
        byte[] bytes = [1, 2, 3]; ProjectSession session = Session(directory.Path, bytes);
        string path = session.Workspace.Project.Background.Path!;
        if (parent)
        {
            File.WriteAllBytes(Path.Combine(target.Path, Path.GetFileName(path)), bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!);
            Directory.CreateSymbolicLink(Path.GetDirectoryName(path)!, target.Path);
        }
        else
        {
            string destination = Path.Combine(target.Path, "target"); File.WriteAllBytes(destination, bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.CreateSymbolicLink(path, destination);
        }
        BackendPreviewInputCaptureResult capture = await Capture(session);
        BackendOperationResult<byte[]> result = await capture.ImageSource!.ReadAsync();
        Assert.Equal("ProjectImageSourceUnavailable", result.Error!.FailureCode);
        Assert.True(session.DrainPinnedInputReadsAsync().IsCompleted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 非闭包路径或缺少保存身份拒绝(bool missingMedia)
    {
        using TemporaryDirectory directory = new();
        ProjectSession session = Session(directory.Path, [1, 2, 3]);
        Project project = session.Workspace.Project;
        session = Session(directory.Path, [1, 2, 3], project.Background with
        { Path = missingMedia ? project.Background.Path : Path.Combine(directory.Path, "outside.png"),
            Media = missingMedia ? null : project.Background.Media });
        BackendPreviewInputCaptureResult capture = await Capture(session);
        Assert.Equal("ProjectImageSourceUnavailable", (await capture.ImageSource!.ReadAsync()).Error!.FailureCode);
    }

    [Fact]
    public async Task 超预算拒绝且不触碰文件并主动放弃可排水()
    {
        using TemporaryDirectory directory = new();
        ProjectSession session = Session(directory.Path, [1], new() { Kind = BackgroundKind.Image,
            Path = Path.Combine(directory.Path, "unused"), Media = new(new string('a', 64),
                ProjectImagePreviewSnapshotCodec.MaximumImageBytes + 1L, "image.png") });
        BackendPreviewInputCaptureResult capture = await Capture(session);
        BackendOperationResult<byte[]> result = await capture.ImageSource!.ReadAsync();
        Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code);
        Assert.Equal("SnapshotSizeExceeded", result.Error.FailureCode);
        BackendPreviewInputCaptureResult second = await Capture(session);
        Task drained = session.DrainPinnedInputReadsAsync(); Assert.False(drained.IsCompleted);
        second.ImageSource!.Dispose(); await drained.WaitAsync(TimeSpan.FromSeconds(2));
        session.ClosePending = true;
        Assert.Equal(BackendErrorCode.SessionClosing, (await Capture(session)).Error!.Code);
    }

    private static Task<BackendPreviewInputCaptureResult> Capture(ProjectSession session, string owner = "client", string revision = "0") =>
        session.CapturePreviewInputAsync(owner, revision, new NeverFactory(), null, captureImageSource: true);

    private static ProjectSession Session(string root, byte[] bytes, BackgroundSource? source = null)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); MediaFileRevision media = new(hash, bytes.Length, "image.png");
        Project project = ProjectWorkspace.CreateNew("image").Project with { Background = source ?? new()
        { Kind = BackgroundKind.Image, Path = Path.Combine(root, media.RelativePath.Replace('/', Path.DirectorySeparatorChar)), Media = media } };
        return new("project", "host", "client", ProjectWorkspace.Adopt(project), string.Empty, 0,
            TimeProvider.System, new(), new("host", 100)) { PinnedInputDirectory = root };
    }

    private static void Write(ProjectSession session, byte[] bytes)
    { string path = session.Workspace.Project.Background.Path!; Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }

    private sealed class NeverFactory : IRenderFrameSessionFactory
    {
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Capture must not execute Runtime.");
    }
}
