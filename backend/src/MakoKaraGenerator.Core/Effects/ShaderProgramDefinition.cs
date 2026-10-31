using System.Collections.Immutable;

#pragma warning disable CA1720 // Float／Int 是本公开 SkSL 类型协议的名称。

namespace MakoKaraGenerator.Core.Effects;

/// <summary>固定 shader 声明中的 uniform 类型；颜色与数据向量不同。</summary>
public enum ShaderUniformKind
{
    /// <summary>单个浮点分量。</summary>
    Float,
    /// <summary>两个数据分量。</summary>
    Float2,
    /// <summary>三个数据分量。</summary>
    Float3,
    /// <summary>四个数据分量。</summary>
    Float4,
    /// <summary>32 位整数；布尔值显式映射为 0 或 1。</summary>
    Int,
    /// <summary>直乘 sRGB RGBA，须对应原生 layout(color)。</summary>
    Color,
    /// <summary>九分量矩阵数据，不要求可逆。</summary>
    Matrix3x3,
}

/// <summary>shader 输入的语义编码；原生反射只识别 shader 类型。</summary>
public enum ShaderSurfaceKind
{
    /// <summary>颜色图像，由图层协议管理颜色空间。</summary>
    Color,
    /// <summary>不进行颜色转换的原始覆盖数据。</summary>
    Coverage,
}

/// <summary>不可变 uniform 声明；null 长度是标量，长度 1 仍是数组。</summary>
public sealed class ShaderUniformDeclaration
{
    /// <summary>创建已经检查名字、类型和长度的声明。</summary>
    public ShaderUniformDeclaration(string name, ShaderUniformKind kind, int? arrayLength = null)
    {
        ShaderProgramDefinition.ValidateName(name);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (arrayLength is <= 0) throw new ArgumentOutOfRangeException(nameof(arrayLength));
        Name = name; Kind = kind; ArrayLength = arrayLength;
    }
    /// <summary>大小写敏感的 ASCII 标识符。</summary>
    public string Name { get; }
    /// <summary>清单的唯一类型。</summary>
    public ShaderUniformKind Kind { get; }
    /// <summary>显式定长数组长度，null 表示不是数组。</summary>
    public int? ArrayLength { get; }
    /// <summary>一个值占用的标量槽。</summary>
    public int ComponentCount => Kind switch
    {
        ShaderUniformKind.Float or ShaderUniformKind.Int => 1,
        ShaderUniformKind.Float2 => 2,
        ShaderUniformKind.Float3 => 3,
        ShaderUniformKind.Float4 or ShaderUniformKind.Color => 4,
        ShaderUniformKind.Matrix3x3 => 9,
        _ => throw new InvalidOperationException("未知 uniform 类型。"),
    };
    /// <summary>包含全部数组元素的标量槽数，不以 int 溢出绕过预算。</summary>
    public long ScalarCount => (long)ComponentCount * (ArrayLength ?? 1);
}

/// <summary>只接受 shader 类型的命名输入，不接受 filter 或 blender。</summary>
public sealed class ShaderChildDeclaration
{
    /// <summary>创建指定语义编码的命名输入。</summary>
    public ShaderChildDeclaration(string name, ShaderSurfaceKind kind)
    {
        ShaderProgramDefinition.ValidateName(name);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Name = name; Kind = kind;
    }
    /// <summary>原生 child 名称。</summary>
    public string Name { get; }
    /// <summary>图调用者须提供的实际编码。</summary>
    public ShaderSurfaceKind Kind { get; }
}

/// <summary>固定源码及完整声明的唯一模型；不包含原生对象或帧参数。</summary>
public sealed class ShaderProgramDefinition
{
    /// <summary>当前锁定的 CPU Skia profile。</summary>
    public const string RasterProfile = "skia-raster-3.119.4-v1";
    /// <summary>R0 接受的初始每 shader 标量预算。</summary>
    public const int DefaultScalarBudget = 128;
    /// <summary>固定逻辑路径及源码，并复制声明；不猜测声明或修正名字。</summary>
    public ShaderProgramDefinition(string sourcePath, string source, string profile,
        IEnumerable<ShaderUniformDeclaration> uniforms, IEnumerable<ShaderChildDeclaration> children)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (sourcePath.Contains('\\', StringComparison.Ordinal) || sourcePath.Contains(':', StringComparison.Ordinal)
            || sourcePath.Split('/').Any(part => part is "" or "." or ".." || part.Any(char.IsControl)))
            throw new ArgumentException("源码路径必须为包内相对逻辑路径。", nameof(sourcePath));
        ArgumentException.ThrowIfNullOrWhiteSpace(source); ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentNullException.ThrowIfNull(uniforms); ArgumentNullException.ThrowIfNull(children);
        Uniforms = [.. uniforms]; Children = [.. children];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in Uniforms)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!names.Add(value.Name)) throw new ArgumentException("shader 声明重名：" + value.Name, nameof(uniforms));
        }
        foreach (var value in Children)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!names.Add(value.Name)) throw new ArgumentException("shader 声明重名：" + value.Name, nameof(children));
        }
        SourcePath = sourcePath; Source = source; Profile = profile;
    }
    /// <summary>包内诊断位置，不是本地可读文件路径。</summary>
    public string SourcePath { get; }
    /// <summary>固定 SkSL 源码。</summary>
    public string Source { get; }
    /// <summary>声明的后端 profile；未知 profile 在编译前拒绝。</summary>
    public string Profile { get; }
    /// <summary>完整、不可变的 uniform 清单。</summary>
    public ImmutableArray<ShaderUniformDeclaration> Uniforms { get; }
    /// <summary>完整、不可变的子 shader 清单。</summary>
    public ImmutableArray<ShaderChildDeclaration> Children { get; }
    /// <summary>全部 uniform 的标量槽数。</summary>
    public long ScalarCount => Uniforms.Sum(value => value.ScalarCount);

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_') || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("shader 名称必须为 ASCII 标识符。", nameof(name));
    }
}
