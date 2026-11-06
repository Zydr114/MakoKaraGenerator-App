using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>独立固定输入，最后一个引用结束后清理私有媒体副本。</summary>
public sealed class BackendPreparedProjectRuntimeInput : IDisposable
{
    private readonly SharedState _state;
    private IDisposable? _reference;
    private readonly object _disposeGate = new();
    internal BackendPreparedProjectRuntimeInput(ProjectSnapshot snapshot, byte[] bytes, ProjectRuntimeBackgroundGrant? grant, string? directory)
    { Snapshot = snapshot; SourceBytes = bytes; Grant = grant; _state = new(directory); _reference = _state.Acquire(); }
    /// <summary>Host固定工程源对象。</summary>
    public ProjectSnapshot Snapshot { get; }
    /// <summary>完整私有Worker输入。</summary>
    public ReadOnlyMemory<byte> SourceBytes { get; }
    /// <summary>固定媒体私有授权，无背景时为空。</summary>
    public ProjectRuntimeBackgroundGrant? Grant { get; }
    /// <summary>实际固定输入能力。</summary>
    public string SourceProfile => ProjectRuntimeInputCodec.IsRuntimeInput(SourceBytes.Span) ? ProjectRuntimeInputCodec.Profile : ProjectSnapshotSourceProfile.Select(Snapshot);
    /// <summary>调用者须在持有现有引用或注册表闸门时取得独立所有权。</summary>
    public IDisposable AcquireReference() => _state.Acquire();
    /// <summary>归还当前独立引用。</summary>
    public void Dispose()
    {
        lock (_disposeGate)
        {
            _reference?.Dispose();
            _reference = null;
        }
    }

    internal static void DeletePreparedDirectory(string directory)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                foreach (string folder in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).Prepend(directory))
                    File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, true);
        }
        catch (DirectoryNotFoundException) when (!Directory.Exists(directory) && !File.Exists(directory)) { }
    }

    private sealed class SharedState(string? directory)
    {
        private readonly object _gate = new();
        private int _references;
        private bool _releaseStarted;
        internal IDisposable Acquire()
        {
            lock (_gate) { ObjectDisposedException.ThrowIf(_releaseStarted, this); _references++; return new Reference(this); }
        }
        private void Release()
        {
            lock (_gate)
            {
                if (_references > 1) { _references--; return; }
                // 文件系统清理失败仍由最后引用持有目录，禁止对部分清理的输入重新借用。
                _releaseStarted = true;
                if (directory is not null) DeletePreparedDirectory(directory);
                _references = 0;
            }
        }
        private sealed class Reference(SharedState owner) : IDisposable
        {
            private SharedState? _owner = owner;
            private readonly object _disposeGate = new();
            public void Dispose()
            {
                lock (_disposeGate)
                {
                    _owner?.Release();
                    _owner = null;
                }
            }
        }
    }
}

/// <summary>Writer 内捕获授权；Writer 外复制固定媒体并验证完整长度/SHA。</summary>
public sealed class BackendProjectRuntimeInputLease : IDisposable
{
    private readonly ProjectSnapshot _snapshot;
    private readonly string? _directory;
    private readonly bool _assetPath;
    private Action? _release;
    private int _started;
    internal BackendProjectRuntimeInputLease(ProjectSnapshot snapshot, string? directory, bool assetPath, Action release)
    { _snapshot = snapshot; _directory = directory; _assetPath = assetPath; _release = release; }

    /// <summary>Host背景媒体特征分析服务；未安装时媒体查询得到无背景贡献输入。</summary>
    public BackendMediaFeatureService? MediaFeatures { get; set; }

    /// <summary>消费者以透明背景输出；媒体查询得到透明输出无背景贡献输入。</summary>
    public bool TransparentOutput { get; set; }

    /// <summary>导出等最终消费者等待背景分析完成，而不是固定为分析中输入。</summary>
    public bool WaitForMediaFeatures { get; set; }

    /// <summary>独立模板样本没有工程文件句柄，仅允许Color背景并复用完整固定包闭包准备。</summary>
    public static BackendProjectRuntimeInputLease CreateDetached(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Background != BackgroundSource.Color) throw new ArgumentException("独立样本须使用Color背景。", nameof(snapshot));
        return new(snapshot, null, false, static () => { });
    }

    /// <summary>可信组合根生成的自有工坊媒体；准备结束前调用者持有其目录，仍复制并核验内容。</summary>
    public static BackendProjectRuntimeInputLease CreateOwnedTemplateSample(ProjectSnapshot snapshot, string directory)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string root = Path.GetFullPath(directory);
        if (snapshot.Background is not { Kind: BackgroundKind.Image, Media: not null, Path: { } path }
            || Path.GetFullPath(Path.GetDirectoryName(path)!) != root)
            throw new ArgumentException("试样背景必须属于其独立自有媒体目录。", nameof(snapshot));
        return new(snapshot, root, true, static () => { });
    }

    /// <summary>复制已授权媒体，完整校验后发布独立固定输入。</summary>
    public async Task<BackendOperationResult<BackendPreparedProjectRuntimeInput>> PrepareAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Interlocked.Exchange(ref _started, 1) != 0 || _release is null, this);
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            bool externalPackages = ProjectRuntimeInputCodec.RequiresPackagePreparation(_snapshot);
            ProjectRuntimePackageGrant[] packageGrants = [];
            if (externalPackages)
            {
                temporary = Directory.CreateTempSubdirectory("makokara-runtime-input-").FullName;
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                List<ProjectRuntimePackageGrant> packages = [];
                EffectPackageContentUsage usage = new();
                foreach (var package in _snapshot.Effects.Packages)
                {
                    token.ThrowIfCancellationRequested();
                    usage.Claim("manifest.json", package.Manifest.Length);
                    foreach (var file in package.Files) usage.Claim(file.Path, file.Content.Length);
                    string directory = Path.Combine(temporary, "packages", package.Revision);
                    EffectPackageStorage.Publish(directory, package);
                    if (!OperatingSystem.IsWindows())
                    {
                        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                            File.SetUnixFileMode(file, UnixFileMode.UserRead);
                        foreach (string folder in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(value => value.Length).Append(directory))
                            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    }
                    packages.Add(new(package.PackageId, package.Revision, directory));
                }
                packageGrants = [.. packages];
            }
            if (_snapshot.Background == BackgroundSource.Color)
            {
                var (solid, _) = await BackendMediaFeatureInputs.AttachAsync(_snapshot, null, MediaFeatures, TransparentOutput, WaitForMediaFeatures, token).ConfigureAwait(false);
                var result = new BackendPreparedProjectRuntimeInput(_snapshot, ProjectRuntimeInputCodec.Encode(solid, null, packageGrants), null, temporary);
                temporary = null; return BackendOperationResult<BackendPreparedProjectRuntimeInput>.Success(result);
            }
            if (_directory is null || _snapshot.Background is not { Media: { } media, Path: { } path } background
                || background.Kind is not (BackgroundKind.Image or BackgroundKind.Video)) return Unavailable();
            string root = Path.GetFullPath(_directory);
            string expected = _assetPath ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, media.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (expected != path || Path.GetFullPath(path) != expected || !Directory.Exists(root) || !File.Exists(expected)) return Unavailable();
            ProjectDirectoryHandles.EnsurePathHasNoLinks(expected);
            temporary ??= Directory.CreateTempSubdirectory("makokara-runtime-input-").FullName;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string output = Path.Combine(temporary, Path.GetFileName(media.RelativePath));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (FileStream input = new(expected, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream target = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (input.Length != media.ByteLength) return Changed();
                byte[] buffer = new byte[64 * 1024]; long length = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break;
                    length = checked(length + read); if (length > media.ByteLength) return Changed();
                    hash.AppendData(buffer.AsSpan(0, read)); await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (length != media.ByteLength || input.Length != media.ByteLength
                    || Convert.ToHexStringLower(hash.GetHashAndReset()) != media.Hash) return Changed();
            }
            ProjectDirectoryHandles.EnsurePathHasNoLinks(expected);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead);
            var grant = new ProjectRuntimeBackgroundGrant(background.Kind, media.RelativePath, output, media.Hash,
                media.ByteLength, media.OriginalName, background.Fit, background.Playback);
            var (attached, artifact) = await BackendMediaFeatureInputs.AttachAsync(_snapshot, output, MediaFeatures, TransparentOutput, WaitForMediaFeatures, token).ConfigureAwait(false);
            var prepared = new BackendPreparedProjectRuntimeInput(_snapshot, ProjectRuntimeInputCodec.Encode(attached, grant, packageGrants, artifact), grant, temporary);
            temporary = null; return BackendOperationResult<BackendPreparedProjectRuntimeInput>.Success(prepared);
        }
        catch (SnapshotContractException error)
        { return BackendOperationResult<BackendPreparedProjectRuntimeInput>.Rejected(new(error.Code == "SnapshotSizeExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandNotSupported,
            "固定运行输入无法准备。", "source", FailureCode: error.Code)); }
        catch (EffectPackageException error)
        { return BackendOperationResult<BackendPreparedProjectRuntimeInput>.Rejected(new(error.Code is "PackageFileCapacityExceeded" or "PackageFileBudgetExceeded"
            or "PackageFileCountExceeded" or "PackageContentBudgetExceeded" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandNotSupported,
            "固定包闭包无法准备。", "source", FailureCode: error.Code)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        { return Unavailable(); }
        finally
        {
            try { if (temporary is not null) BackendPreparedProjectRuntimeInput.DeletePreparedDirectory(temporary); }
            finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
        }
    }
    /// <summary>归还当前独立引用。</summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0) Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    private static BackendOperationResult<BackendPreparedProjectRuntimeInput> Unavailable() => BackendOperationResult<BackendPreparedProjectRuntimeInput>.Rejected(new(
        BackendErrorCode.CommandNotSupported, "固定媒体授权闭包不可用。", "background", FailureCode: "ProjectRuntimeSourceUnavailable"));
    private static BackendOperationResult<BackendPreparedProjectRuntimeInput> Changed() => BackendOperationResult<BackendPreparedProjectRuntimeInput>.Rejected(new(
        BackendErrorCode.CommandNotSupported, "固定媒体长度或内容已改变。", "background", FailureCode: "ProjectRuntimeSourceChanged"));
}

public sealed partial class ProjectSession
{
    /// <summary>Registry-installed background media-feature analysis; never owned by this session.</summary>
    internal BackendMediaFeatureService? MediaFeatures { get; set; }

    // Only under Writer after owner/revision/close checks. No IO or Worker operation.
    internal BackendProjectRuntimeInputLease AcquireProjectRuntimeInput(ProjectSnapshot snapshot)
    {
        lock (_pinnedInputReadGate)
            if (_pinnedInputReaders++ == 0) _pinnedInputReadsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? assetReference = null;
        bool assetPath = snapshot.Background.Path is { } path && _backgroundHistory.ContainsKey(path);
        try
        {
            if (assetPath) assetReference = _backgroundHistory[snapshot.Background.Path!].AcquireReference();
            return new(snapshot, assetPath ? Path.GetDirectoryName(snapshot.Background.Path) : PinnedInputDirectory,
                assetPath, () => { try { assetReference?.Dispose(); } finally { ReleaseProjectImageRead(); } }) { MediaFeatures = MediaFeatures };
        }
        catch { ReleaseProjectImageRead(); throw; }
    }
}
