using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>固定包程序的真实 CPU 编译集合；一次反射校验后可逐帧创建 shader，统一释放编译资源。</summary>
public sealed class CompiledPackageShaderPrograms : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CompiledRuntimeShader> _programs = new(StringComparer.Ordinal);
    private bool _disposed;
    private CompiledPackageShaderPrograms(ImmutableDictionary<string, ShaderProgramDefinition> definitions) => Definitions = definitions;

    /// <summary>程序相对路径对应的唯一固定声明；不是清单导出表。</summary>
    public ImmutableDictionary<string, ShaderProgramDefinition> Definitions { get; }

    /// <summary>编译包括未引用项的全部程序；中途错误或取消时回收已编译资源，不发布部分集合。</summary>
    public static CompiledPackageShaderPrograms Compile(ImmutableDictionary<string, ShaderProgramDefinition> definitions,
        int scalarBudget = ShaderProgramDefinition.DefaultScalarBudget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scalarBudget);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new CompiledPackageShaderPrograms(definitions);
        try
        {
            foreach (var pair in definitions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { result._programs.Add(pair.Key, CompiledRuntimeShader.Compile(pair.Value, scalarBudget, cancellationToken)); }
                catch (ShaderProgramException error)
                { throw new EffectPackageException(error.Code, pair.Key + "." + error.Field, "真实 shader 编译／反射失败：" + error.Message, error); }
            }
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>从既有程序创建真实帧 shader；不重新编译源码，返回值由调用者释放，不接管 child。</summary>
    public SKShader CreateShader(string programPath, UniformValues values, IReadOnlyDictionary<string, RuntimeShaderInput> children,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            if (!_programs.TryGetValue(programPath, out var program))
                throw new EffectPackageException("PackageShaderProgramMissing", programPath, "固定包中不存在此程序身份。");
            return program.CreateShader(values, children, cancellationToken);
        }
    }

    /// <summary>读取包含固定源码／ABI 的原生程序指纹；不包含帧 uniform 值。</summary>
    public string GetProgramKey(string programPath)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _programs.TryGetValue(programPath, out var program) ? program.ProgramKey
                : throw new EffectPackageException("PackageShaderProgramMissing", programPath, "固定包中不存在此程序身份。");
        }
    }

    /// <summary>幂等回收全部编译程序；已经创建的帧 shader 保留自己的原生引用。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var program in _programs.Values) program.Dispose();
            _programs.Clear();
        }
    }
}
