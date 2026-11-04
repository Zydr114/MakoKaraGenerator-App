using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>只消费 Host 私有 pipe 输入；不接受 HTTP 文件路径。</summary>
internal sealed class ProjectRuntimeInput : IDisposable
{
    private readonly FfmpegVideoFrameProvider _videos = FfmpegVideoFrameProvider.CreateSelfContained();
    private readonly ProjectRuntimeBackgroundGrant? _grant;
    private ProjectRuntimeInput(ProjectSnapshot snapshot, string profile, ProjectRuntimeBackgroundGrant? grant,
        ReadOnlyMemory<byte> source, IScriptCompilationService compiler)
    { Snapshot = snapshot; SourceProfile = profile; _grant = grant; Source = source; Compiler = compiler; }
    internal ProjectSnapshot Snapshot { get; }
    internal string SourceProfile { get; }
    internal ReadOnlyMemory<byte> Source { get; }
    internal IScriptCompilationService Compiler { get; }

    internal static async Task<ProjectRuntimeInput> OpenAsync(ReadOnlyMemory<byte> bytes, CancellationToken token,
        IScriptCompilationService? fixedCompiler = null)
    {
        var prepared = fixedCompiler is null ? WorkerScriptInputs.Read(bytes, token) : (Source: bytes, Compiler: fixedCompiler);
        bytes = prepared.Source;
        Dictionary<(string, string), EffectPackageContents> packages = [];
        EffectPackageContentUsage packageUsage = new();
        foreach (var packageGrant in ProjectRuntimeInputCodec.ReadPackageGrants(bytes))
        {
            EnsureNoLinks(packageGrant.DirectoryPath, directory: true);
            var package = await EffectPackageStorage.ReadSupportedDirectoryWithUsageAsync(packageGrant.DirectoryPath,
                null, packageUsage, token).ConfigureAwait(false);
            if (package.PackageId != packageGrant.PackageId || package.Revision != packageGrant.Revision)
                throw new WorkerProtocolException("固定包目录身份已改变。", "ProjectRuntimeSourceChanged");
            packages.Add((package.PackageId, package.Revision), package);
        }
        var input = ProjectRuntimeInputCodec.Decode(bytes, packages.Count == 0 ? null : packages);
        if (input.Grant is { } grant)
        {
            EnsureNoLinks(grant.FilePath);
            await using var stream = new FileStream(grant.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != grant.ByteLength) throw Changed();
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (stream.Length != grant.ByteLength || hash != grant.Sha256) throw Changed();
            EnsureNoLinks(grant.FilePath);
            if (grant.Kind == BackgroundKind.Video)
                _ = FfmpegVideoFrameProvider.ValidateSelfContainedInput(grant.FilePath, token);
        }
        token.ThrowIfCancellationRequested(); return new(input.Snapshot, input.SourceProfile, input.Grant, bytes, prepared.Compiler);
    }

    internal ProjectRenderSessionFactory CreateFactory(ReplayReason reason,
        bool layoutReference = false, string? editingLineId = null, string? editingCueId = null, string? renderBackend = null,
        bool enableGpuPreview = false, bool enableGpuPreviewBenchmark = false, RuntimeGpuPreparation? gpuPreparation = null)
        => new(Compiler, _videos, reason,
            _grant is null ? null : () => new GrantedBackgroundResolver(_grant, _videos), layoutReference, editingLineId, editingCueId, renderBackend, enableGpuPreview, enableGpuPreviewBenchmark, gpuPreparation);
    public void Dispose() => _videos.Dispose();

    private sealed class GrantedBackgroundResolver(ProjectRuntimeBackgroundGrant grant, FfmpegVideoFrameProvider videos) : IBackgroundFrameSourceResolver
    {
        private readonly BackgroundFrameSourceResolver _resolver = new(videos);
        public IBackgroundFrameSource? Resolve(BackgroundSource source) => Resolve(source, CancellationToken.None);
        public IBackgroundFrameSource? Resolve(BackgroundSource source, CancellationToken token)
        {
            if (source.Kind != grant.Kind || source.Path != grant.LogicalPath || source.Media?.Hash != grant.Sha256
                || source.Media.ByteLength != grant.ByteLength || source.Fit != grant.Fit || source.Playback != grant.Playback)
                throw new WorkerProtocolException("背景来源与固定媒体授权不匹配。", "ProjectRuntimeSourceMismatch");
            EnsureNoLinks(grant.FilePath);
            return _resolver.Resolve(source with { Path = grant.FilePath }, token);
        }
        public void Dispose() => _resolver.Dispose();
    }
    private static void EnsureNoLinks(string path, bool directory = false)
    {
        FileSystemInfo? item = directory ? new DirectoryInfo(path) : new FileInfo(path);
        while (item is not null)
        {
            if (!item.Exists || item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new WorkerProtocolException("固定媒体授权文件不可用。", "ProjectRuntimeSourceUnavailable");
            item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent;
        }
    }
    private static WorkerProtocolException Changed() => new("固定媒体内容已改变。", "ProjectRuntimeSourceChanged");
}
