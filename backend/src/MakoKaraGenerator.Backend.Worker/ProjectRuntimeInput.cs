using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
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
    private readonly MediaBindingOwner? _media;
    private ProjectRuntimeInput(ProjectSnapshot snapshot, string profile, ProjectRuntimeBackgroundGrant? grant,
        ReadOnlyMemory<byte> source, IScriptCompilationService compiler, MediaBindingOwner? media)
    { Snapshot = snapshot; SourceProfile = profile; _grant = grant; Source = source; Compiler = compiler; _media = media; }
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
        token.ThrowIfCancellationRequested();
        MediaBindingOwner? media = input.Snapshot.MediaFeatureInput is { } mediaInput
            ? await MediaBindingOwner.OpenAsync(input.Snapshot, mediaInput, ProjectRuntimeInputCodec.ReadMediaGrant(bytes)?.ArtifactPath, token).ConfigureAwait(false)
            : null;
        return new(input.Snapshot, input.SourceProfile, input.Grant, bytes, prepared.Compiler, media);
    }

    internal ProjectRenderSessionFactory CreateFactory(ReplayReason reason,
        bool layoutReference = false, string? editingLineId = null, string? editingCueId = null, string? renderBackend = null,
        bool enableGpuPreview = false, bool enableGpuPreviewBenchmark = false, RuntimeGpuPreparation? gpuPreparation = null)
        => new(Compiler, _videos, reason,
            _grant is null ? null : () => new GrantedBackgroundResolver(_grant, _videos), layoutReference, editingLineId, editingCueId, renderBackend, enableGpuPreview, enableGpuPreviewBenchmark, gpuPreparation,
            _media?.Binding);
    /// <summary>
    /// 双输出验证的透明半程：固定输入属于普通输出时，按同一快照另建透明无媒体输入和会话工厂；
    /// 没有媒体输入或已是透明输入时返回 null，沿用原工厂。
    /// </summary>
    internal async Task<TransparentRuntimeOutput?> CreateTransparentOutputAsync(ReplayReason reason, string? renderBackend, CancellationToken token)
    {
        if (Snapshot.MediaFeatureInput is not { } input || input.Context.Mapping.OutputMode == MediaOutputMode.Transparent) return null;
        var basis = Snapshot with { MediaFeatureInput = null };
        var transparent = ProjectRuntimeMediaOutputs.TransparentNoMedia(basis, token)
            ?? throw new WorkerProtocolException("固定媒体输入没有对应的媒体依赖。", "ProjectRuntimeSourceMismatch");
        var snapshot = basis with { MediaFeatureInput = transparent };
        var media = await MediaBindingOwner.OpenAsync(snapshot, transparent, null, token).ConfigureAwait(false);
        try
        {
            ProjectRenderSessionFactory factory = new(Compiler, _videos, reason,
                _grant is null ? null : () => new GrantedBackgroundResolver(_grant, _videos), renderBackend: renderBackend,
                mediaBinding: media.Binding);
            return new(snapshot, factory, media);
        }
        catch { media.Dispose(); throw; }
    }

    public void Dispose()
    {
        try { _media?.Dispose(); }
        finally { _videos.Dispose(); }
    }

    /// <summary>透明半程的快照、工厂及其媒体绑定所有权。</summary>
    internal sealed class TransparentRuntimeOutput(ProjectSnapshot snapshot, ProjectRenderSessionFactory factory, IDisposable owner) : IDisposable
    {
        internal RuntimeTransparentOutput Output { get; } = new(snapshot, factory);
        public void Dispose() => owner.Dispose();
    }

    /// <summary>Owns the validated artifact reader and the bounded sampler for this fixed runtime input only.</summary>
    private sealed class MediaBindingOwner : IDisposable
    {
        private static readonly MediaFeatureTrackCodec Codec = new(new MediaMomentKernel(), new MediaAbValueValidator());
        private readonly MediaFeatureSampler _sampler;
        private readonly IMediaFeatureTrackReader? _reader;
        private MediaBindingOwner(MediaFeatureSampler sampler, IMediaFeatureTrackReader? reader)
        { _sampler = sampler; _reader = reader; Binding = new(sampler); }
        internal ProjectMediaFeatureBinding Binding { get; }

        internal static async Task<MediaBindingOwner> OpenAsync(ProjectSnapshot snapshot, MediaFeatureInput input,
            string? artifactPath, CancellationToken token)
        {
            var plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
                snapshot.LyricPresentation, snapshot.PresetApplications, token);
            var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);
            IMediaFeatureTrackReader? reader = null;
            try
            {
                if (input is MediaFeatureInput.AvailableFile file)
                {
                    if (artifactPath is null) throw new WorkerProtocolException("固定媒体特征缺少制品授权。", "ProjectRuntimeSourceUnavailable");
                    EnsureNoLinks(artifactPath);
                    var stream = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                        FileOptions.Asynchronous | FileOptions.RandomAccess);
                    try { reader = await Codec.OpenValidatedAsync(stream, file.Source, file.Artifact, budget, token).ConfigureAwait(false); }
                    catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
                }
                return new(new MediaFeatureSampler(input, plan, reader, budget, token), reader);
            }
            catch (MediaAbException error)
            {
                if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
                throw new WorkerProtocolException("固定媒体特征制品不可用：" + error.Failure, "ProjectRuntimeSourceChanged");
            }
            catch
            {
                if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public void Dispose()
        {
            try { _sampler.Dispose(); }
            finally { _reader?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
    }

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
