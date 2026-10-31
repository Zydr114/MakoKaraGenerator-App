using System.Collections.Immutable;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>读取固定包中的全部独立 shader 程序声明；不推断 SkSL 类型，也不代表完整导出已经可运行。</summary>
public static class EffectPackageShaderPrograms
{
    /// <summary>独立程序 JSON 的精确格式版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>按包相对程序路径建立唯一目录，包括未被图引用的声明；实际 ABI 仍须由后端反射验证。</summary>
    public static ImmutableDictionary<string, ShaderProgramDefinition> Read(EffectPackageContents contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents); cancellationToken.ThrowIfCancellationRequested();
        var files = contents.Files.ToImmutableDictionary(file => file.Path, StringComparer.Ordinal);
        var programs = ImmutableDictionary.CreateBuilder<string, ShaderProgramDefinition>(StringComparer.Ordinal);
        foreach (var file in contents.Files.Where(file => file.Role == "shaderProgram"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = ReadJson(file.Content.AsMemory(), file.Path);
            var root = document.RootElement;
            Keys(root, file.Path, ["programVersion", "source", "profile", "uniforms", "children"], []);
            if (Whole(root.GetProperty("programVersion"), file.Path + ".programVersion") != CurrentVersion)
                throw Error("PackageShaderProgramVersionMismatch", file.Path + ".programVersion", "shader 程序版本不受支持。");
            string source = RequiredText(root, "source", file.Path), profile = RequiredText(root, "profile", file.Path);
            EffectPackageContents.ValidatePath(source);
            if (!files.TryGetValue(source, out var sourceFile) || sourceFile.Role != "shader")
                throw Error("PackageShaderSourceMissing", file.Path + ".source", "程序须引用同闭包明确声明 role:shader 的源码。");
            if (profile != contents.Profile.ShaderProfile)
                throw Error("PackageShaderProfileMismatch", file.Path + ".profile", "程序和包的 shader profile 不一致。");
            var uniforms = ImmutableArray.CreateBuilder<ShaderUniformDeclaration>();
            var children = ImmutableArray.CreateBuilder<ShaderChildDeclaration>();
            HashSet<string> names = new(StringComparer.Ordinal); string location = file.Path;
            try
            {
                foreach (var item in Array(root.GetProperty("uniforms"), file.Path + ".uniforms"))
                {
                    cancellationToken.ThrowIfCancellationRequested(); location = file.Path + ".uniforms[" + uniforms.Count + "]";
                    Keys(item, location, ["name", "type"], ["arrayLength"]);
                    string name = RequiredText(item, "name", location); AddName(names, name, location);
                    ShaderUniformKind kind = RequiredText(item, "type", location) switch
                    {
                        "float" => ShaderUniformKind.Float, "float2" => ShaderUniformKind.Float2, "float3" => ShaderUniformKind.Float3,
                        "float4" => ShaderUniformKind.Float4, "int" => ShaderUniformKind.Int, "color" => ShaderUniformKind.Color,
                        "matrix3x3" => ShaderUniformKind.Matrix3x3,
                        _ => throw Error("PackageShaderUniformTypeUnsupported", location + ".type", "未知 uniform 类型。"),
                    };
                    int? arrayLength = null;
                    if (item.TryGetProperty("arrayLength", out var length))
                    {
                        long count = Whole(length, location + ".arrayLength");
                        if (count is <= 0 or > int.MaxValue)
                            throw Error("PackageShaderArrayLengthInvalid", location + ".arrayLength", "数组长度须为可寻址正整数。");
                        arrayLength = (int)count;
                    }
                    uniforms.Add(new(name, kind, arrayLength));
                }
                foreach (var item in Array(root.GetProperty("children"), file.Path + ".children"))
                {
                    cancellationToken.ThrowIfCancellationRequested(); location = file.Path + ".children[" + children.Count + "]";
                    Keys(item, location, ["name", "type"], []);
                    string name = RequiredText(item, "name", location); AddName(names, name, location);
                    var kind = RequiredText(item, "type", location) switch
                    {
                        "color" => ShaderSurfaceKind.Color, "coverage" => ShaderSurfaceKind.Coverage,
                        _ => throw Error("PackageShaderChildTypeUnsupported", location + ".type", "child 仅支持 color／coverage。"),
                    };
                    children.Add(new(name, kind));
                }
                location = file.Path + ".source";
                programs.Add(file.Path, new(source, sourceFile.ReadUtf8(), profile, uniforms, children));
            }
            catch (ArgumentException error)
            { throw Error("PackageShaderDeclarationInvalid", location, "shader 声明名称、源码或类型无效。", error); }
        }
        cancellationToken.ThrowIfCancellationRequested(); return programs.ToImmutable();
    }

    private static void AddName(HashSet<string> names, string name, string location)
    {
        if (!names.Add(name)) throw Error("PackageShaderDeclarationDuplicate", location + ".name", "uniform 与 child 共享的名称重复。");
    }
}
