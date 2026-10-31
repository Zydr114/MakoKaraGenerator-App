using System.Reflection;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>当前程序的三段产品版本；取自统一的程序集信息版本。</summary>
public static class ProductVersion
{
    /// <summary>当前程序版本。</summary>
    public static string Current { get; } = typeof(ProductVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion
        ?? throw new InvalidOperationException("Core 程序集缺少信息版本。");
}
