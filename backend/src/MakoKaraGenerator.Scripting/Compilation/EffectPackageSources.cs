using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>将已固定包内容投影到离线编译器；不重新读取外部文件或图片。</summary>
public static class EffectPackageSources
{
    /// <summary>读取明确 csharp 用途的固定源码；空源码包不能作为控制器编译。</summary>
    /// <remarks>内容闭包不等于导出语义验证；调用者仍须验证入口及输出合同。</remarks>
    public static ImmutableArray<ScriptSource> Read(EffectPackageContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ImmutableArray<ScriptSource> sources = [.. contents.Files.Where(file => file.Role == "csharp").Select(file => new ScriptSource(file.Path, file.ReadUtf8()))];
        if (sources.IsEmpty) throw new EffectPackageException("PackageCSharpMissing", "files", "包中没有声明的 C# 源码。");
        return sources;
    }
}
