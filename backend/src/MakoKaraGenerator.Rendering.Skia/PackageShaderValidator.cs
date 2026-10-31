using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>正式 CPU 后端完整编译／反射验证包中全部程序；验证结束不保留 native 对象。</summary>
public sealed class PackageShaderValidator : IPackageShaderValidator
{
    private readonly int _scalarBudget;
    /// <summary>采用明确的每程序标量预算；不以 shader 文件数猜测预算。</summary>
    public PackageShaderValidator(int scalarBudget = ShaderProgramDefinition.DefaultScalarBudget)
    { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scalarBudget); _scalarBudget = scalarBudget; }
    /// <inheritdoc/>
    public void Validate(ImmutableDictionary<string, ShaderProgramDefinition> programs, CancellationToken cancellationToken = default)
    { using var compiled = CompiledPackageShaderPrograms.Compile(programs, _scalarBudget, cancellationToken); }
}
