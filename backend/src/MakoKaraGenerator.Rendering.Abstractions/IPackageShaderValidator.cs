using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering;

/// <summary>正式后端验证包内全部 shader 声明及真实源码的门禁；不向脚本提供 native 对象。</summary>
public interface IPackageShaderValidator
{
    /// <summary>编译并反射全部固定程序，包括未使用的声明；失败不能部分启用控制器包。</summary>
    void Validate(ImmutableDictionary<string, ShaderProgramDefinition> programs, CancellationToken cancellationToken = default);
}
