using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>使用分发参考集离线编译固定源码；不读取 UI、工程或开发机 SDK。</summary>
public sealed class ScriptCompiler
{
    // 进程内编译也串行化，避免多个热编译请求同时进入原生／Roslyn元数据出口。
    private static readonly SemaphoreSlim CompilationGate = new(1, 1);
    private readonly ImmutableArray<MetadataReference> _references;
    private readonly string _referenceHash;
    private readonly string? _artifactRoot;
    private const string Options = "csharp14|net10.0|release|nullable|deterministic|portable-pdb|unsafe:false|concurrentBuild:false|serial-emit-v1|artifact-format2";

    /// <summary>创建固定编译服务。</summary>
    /// <param name="referenceDirectory">随应用分发的 net10.0 参考 DLL 目录。</param>
    /// <param name="artifactRoot">可选的本机内容地址产物根；损坏代次明确失败，不回退旧DLL。</param>
    public ScriptCompiler(string referenceDirectory, string? artifactRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceDirectory);
        if (artifactRoot is not null) ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        _artifactRoot = artifactRoot;
        string[] paths = [.. Directory.EnumerateFiles(referenceDirectory, "*.dll").Order(StringComparer.Ordinal)];
        if (paths.Length == 0 || !paths.Any(static p => Path.GetFileName(p) == "System.Runtime.dll"))
            throw new ArgumentException("脚本参考程序集集合不完整。", nameof(referenceDirectory));
        List<MetadataReference> references = [];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths.Append(typeof(TokenId).Assembly.Location))
        {
            byte[] bytes = File.ReadAllBytes(path);
            Add(hash, Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            Add(hash, SHA256.HashData(bytes));
            references.Add(MetadataReference.CreateFromImage(bytes));
        }
        _references = [.. references];
        _referenceHash = Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>实际编译参考集合的内容身份；由 Worker 能力握手报告。</summary>
    public string ReferenceIdentity => _referenceHash;
    /// <summary>实际编译参考集合的程序集数量。</summary>
    public int ReferenceCount => _references.Length;

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
        ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(entryTypes);
        string[] fixedEntries = [.. entryTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (fixedEntries.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("入口集合不能包含空身份。", nameof(entryTypes));
        ScriptSource[] fixedSources = [.. sources];
        if (fixedSources.Length == 0 || fixedSources.Any(static s => s is null || s.Text is null || !ValidPath(s.Path))
            || fixedSources.Select(static s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fixedSources.Length)
            throw new ArgumentException("源码路径必须非空、相对、唯一且使用正斜线。", nameof(sources));
        Array.Sort(fixedSources, static (a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return CompileSerialAsync(fixedSources, fixedEntries, cancellationToken);
    }

    private async Task<ScriptCompilationResult> CompileSerialAsync(ScriptSource[] sources, string[] entryTypes, CancellationToken token)
    {
        await CompilationGate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => Compile(sources, entryTypes, token), token).ConfigureAwait(false); }
        finally { CompilationGate.Release(); }
    }

    private ScriptCompilationResult Compile(ScriptSource[] sources, string[] entryTypes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, Encoding.UTF8.GetBytes(Options));
        Add(hash, Encoding.UTF8.GetBytes(typeof(CSharpCompilation).Assembly.GetName().Version!.ToString()));
        Add(hash, Encoding.UTF8.GetBytes(_referenceHash));
        Add(hash, Encoding.UTF8.GetBytes("package-entries-v1"));
        Add(hash, Encoding.UTF8.GetBytes(entryTypes.Length.ToString(CultureInfo.InvariantCulture)));
        foreach (string entry in entryTypes) Add(hash, Encoding.UTF8.GetBytes(entry));
        foreach (ScriptSource source in sources)
        {
            Add(hash, Encoding.UTF8.GetBytes(source.Path));
            Add(hash, Encoding.UTF8.GetBytes(source.Text));
        }
        string fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (_artifactRoot is not null && Directory.Exists(Path.Combine(_artifactRoot, fingerprint)))
        {
            token.ThrowIfCancellationRequested();
            var cached = ScriptArtifactStore.Load(Path.Combine(_artifactRoot, fingerprint));
            if (cached.ArtifactHash != fingerprint) throw new InvalidDataException("脚本缓存身份与固定源码不一致。");
            return cached;
        }
        var parse = new CSharpParseOptions(LanguageVersion.CSharp14);
        IEnumerable<SyntaxTree> trees = sources.Select(source => CSharpSyntaxTree.ParseText(
            SourceText.From(source.Text, Encoding.UTF8), parse, source.Path, token));
        CSharpCompilation compilation = CSharpCompilation.Create("MakoKaraEffect_" + fingerprint,
            trees, _references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable, concurrentBuild: false));
        using MemoryStream dll = new();
        using MemoryStream pdb = new();
        EmitResult emitted = compilation.Emit(dll, pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb), cancellationToken: token);
        ImmutableArray<ScriptCompileDiagnostic> diagnostics = [.. emitted.Diagnostics
            .Where(static d => d.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Warning or Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(ToDiagnostic)];
        var result = new ScriptCompilationResult(fingerprint,
            emitted.Success ? [.. dll.ToArray()] : [], emitted.Success ? [.. pdb.ToArray()] : [], diagnostics);
        token.ThrowIfCancellationRequested();
        if (result.Succeeded && _artifactRoot is not null) ScriptArtifactStore.Publish(_artifactRoot, result);
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

    private static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
        && !path.Contains('\\') && !path.Contains(':') && !path.Split('/').Any(static s => s is "" or "." or "..");

    private static void Add(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
