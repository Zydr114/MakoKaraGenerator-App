using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>使用分发参考集离线编译固定源码；不读取 UI、工程或开发机 SDK。</summary>
public sealed class ScriptCompiler : IScriptCompilationService
{
    // 进程内编译也串行化，避免多个热编译请求同时进入原生／Roslyn元数据出口。
    private static readonly SemaphoreSlim CompilationGate = new(1, 1);
    private readonly ImmutableArray<MetadataReference> _references;
    private readonly ScriptReferenceIdentity _referenceIdentity;
    private readonly string? _artifactRoot;
    private readonly ScriptArtifactBudget _artifactBudget;
    private readonly long _maximumCacheBytes;
    private int _compilationCount;
    private static readonly object FlightsGate = new();
    private static readonly Dictionary<string, Flight> Flights = new(StringComparer.Ordinal);

    /// <summary>创建固定编译服务。</summary>
    /// <param name="referenceDirectory">随应用分发的 net10.0 参考 DLL 目录。</param>
    /// <param name="artifactRoot">可选的本机内容地址产物根；损坏代次明确失败，不回退旧DLL。</param>
    /// <param name="artifactBudget">独立于源码和 IPC 的内部 PE／PDB／清单读取预算。</param>
    /// <param name="maximumCacheBytes">持久缓存总配额；在用租约不能被淘汰。</param>
    public ScriptCompiler(string referenceDirectory, string? artifactRoot = null, ScriptArtifactBudget? artifactBudget = null,
        long maximumCacheBytes = ScriptArtifactStore.DefaultMaximumCacheBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceDirectory);
        if (artifactRoot is not null) ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCacheBytes);
        _artifactRoot = artifactRoot is null ? null : Path.GetFullPath(artifactRoot);
        _artifactBudget = artifactBudget ?? ScriptArtifactBudget.Default; _artifactBudget.Validate(); _maximumCacheBytes = maximumCacheBytes;
        _referenceIdentity = ScriptReferenceIdentity.Read(referenceDirectory, out var referenceImages);
        _references = [.. referenceImages.Select(image => MetadataReference.CreateFromImage(image))];
        Identity = new(typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(), ScriptCompilationIdentity.V1Options,
            _referenceIdentity.Identity, _referenceIdentity.SdkIdentity, ScriptCompilationIdentity.CurrentEngineIdentity, ScriptCompilationIdentity.CurrentAnalysisRules);
    }

    /// <summary>实际编译参考集合的内容身份；由 Worker 能力握手报告。</summary>
    public string ReferenceIdentity => _referenceIdentity.Identity;
    /// <summary>实际编译参考集合的程序集数量。</summary>
    public int ReferenceCount => _referenceIdentity.Count;
    /// <summary>本编译服务固定的完整部署身份。</summary>
    public ScriptCompilationIdentity Identity { get; }
    /// <summary>此服务实际进入 Roslyn Emit 的次数；缓存命中及合并等待不计数。</summary>
    public int CompilationCount => Volatile.Read(ref _compilationCount);

    /// <summary>在后台编译源码；取消不会被转换为编译诊断。</summary>
    /// <param name="sources">固定且无重复相对路径的源码集合。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>不可变的产物或具名诊断。</returns>
    public Task<ScriptCompilationResult> CompileAsync(IEnumerable<ScriptSource> sources, CancellationToken cancellationToken = default)
        => CompileAsync(sources, [], cancellationToken);

    /// <summary>编译包源码与完整入口集合；入口改变建立新 Artifact，参数／资源不进入编译指纹。</summary>
    public Task<ScriptCompilationResult> CompileAsync(IEnumerable<ScriptSource> sources, IEnumerable<string> entryTypes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = ScriptCompilationInput.Create(sources, entryTypes, Identity);
        string key = (_artifactRoot ?? "memory") + "|" + input.ArtifactHash + "|" + _maximumCacheBytes.ToString(CultureInfo.InvariantCulture)
            + "|" + _artifactBudget.ToString();
        Flight flight;
        lock (FlightsGate)
        {
            if (!Flights.TryGetValue(key, out flight!))
            {
                flight = new(); Flights.Add(key, flight);
                Flight created = flight;
                flight.Task = Task.Run(async () =>
                {
                    try { return await CompileSharedAsync(input, created.Cancellation.Token).ConfigureAwait(false); }
                    finally
                    {
                        CancellationTokenSource? dispose;
                        lock (FlightsGate)
                        {
                            created.Finished = true;
                            if (Flights.TryGetValue(key, out var current) && ReferenceEquals(current, created)) Flights.Remove(key);
                            dispose = TakeDisposableCancellation(created);
                        }
                        dispose?.Dispose();
                    }
                });
            }
            flight.Waiters++;
        }
        return AwaitFlightAsync(key, flight, cancellationToken);
    }

    private static async Task<ScriptCompilationResult> AwaitFlightAsync(string key, Flight flight, CancellationToken token)
    {
        try { return await flight.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            CancellationTokenSource? cancel = null; CancellationTokenSource? dispose;
            lock (FlightsGate)
            {
                if (--flight.Waiters == 0 && !flight.Finished)
                {
                    flight.CancellationStarted = true; cancel = flight.Cancellation;
                    if (Flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) Flights.Remove(key);
                }
                dispose = TakeDisposableCancellation(flight);
            }
            // Never execute cancellation callbacks under the process-wide membership gate.
            if (cancel is not null)
            {
                try { cancel.Cancel(); }
                finally
                {
                    lock (FlightsGate) { flight.CancellationFinished = true; dispose = TakeDisposableCancellation(flight); }
                    dispose?.Dispose();
                }
            }
            else dispose?.Dispose();
        }
    }
    private static CancellationTokenSource? TakeDisposableCancellation(Flight flight)
    {
        if (flight.Disposed || flight.Waiters != 0 || !flight.Finished || flight.CancellationStarted && !flight.CancellationFinished) return null;
        flight.Disposed = true; return flight.Cancellation;
    }
    private async Task<ScriptCompilationResult> CompileSharedAsync(ScriptCompilationInput input, CancellationToken token)
    {
        using IDisposable? processLock = _artifactRoot is null ? null
            : await ScriptArtifactStore.AcquireCompilationLockAsync(_artifactRoot, input.ArtifactHash, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var lease = _artifactRoot is null ? null : ScriptArtifactStore.TryAcquireLease(_artifactRoot, input.ArtifactHash, _artifactBudget, _maximumCacheBytes, token);
        if (lease is not null)
        {
            var cached = lease.Artifact;
            if (cached.Descriptor!.Identity != Identity || !cached.Descriptor.EntryTypes.SequenceEqual(input.EntryTypes))
                throw new InvalidDataException("脚本缓存与固定编译身份不一致。");
            return cached;
        }
        await CompilationGate.WaitAsync(token).ConfigureAwait(false);
        try { return Compile(input, token); }
        finally { CompilationGate.Release(); }
    }

    private ScriptCompilationResult Compile(ScriptCompilationInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string fingerprint = input.ArtifactHash;
        var sources = input.Sources;
        var parse = new CSharpParseOptions(LanguageVersion.CSharp14);
        IEnumerable<SyntaxTree> trees = sources.Select(source => CSharpSyntaxTree.ParseText(
            SourceText.From(source.Text, Encoding.UTF8), parse, source.Path, token));
        CSharpCompilation compilation = CSharpCompilation.Create("MakoKaraEffect_" + fingerprint,
            trees, _references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable, concurrentBuild: false));
        var analysis = ScriptV2PurityAnalyzer.Analyze(compilation, input.EntryTypes, token);
        if (analysis.Any(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            return new(fingerprint, [], [], [.. compilation.GetDiagnostics(token).Concat(analysis)
                .Where(static diagnostic => diagnostic.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Warning or Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(ToDiagnostic)]);
        using MemoryStream dll = new();
        using MemoryStream pdb = new();
        Interlocked.Increment(ref _compilationCount);
        EmitResult emitted = compilation.Emit(dll, pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb), cancellationToken: token);
        ImmutableArray<ScriptCompileDiagnostic> diagnostics = [.. emitted.Diagnostics.Concat(analysis)
            .Where(static d => d.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Warning or Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(ToDiagnostic)];
        var result = new ScriptCompilationResult(fingerprint,
            emitted.Success ? [.. dll.ToArray()] : [], emitted.Success ? [.. pdb.ToArray()] : [], diagnostics);
        token.ThrowIfCancellationRequested();
        if (result.Succeeded)
        {
            result = result with { Descriptor = ScriptArtifactDescriptor.Create(input, result) };
            if (_artifactRoot is not null) ScriptArtifactStore.Publish(_artifactRoot, result, _artifactBudget, _maximumCacheBytes, token);
            else result.Descriptor.ValidateResult(result, _artifactBudget);
        }
        return result;
    }

    private static ScriptCompileDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        FileLinePositionSpan location = diagnostic.Location.GetLineSpan();
        bool hasPosition = diagnostic.Location.IsInSource;
        return new(diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture),
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error, hasPosition ? location.Path : null,
            hasPosition ? location.StartLinePosition.Line + 1 : 0, hasPosition ? location.StartLinePosition.Character + 1 : 0);
    }

    private sealed class Flight
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task<ScriptCompilationResult> Task { get; set; } = null!;
        internal int Waiters { get; set; }
        internal bool Finished { get; set; }
        internal bool CancellationStarted { get; set; }
        internal bool CancellationFinished { get; set; }
        internal bool Disposed { get; set; }
    }
}
