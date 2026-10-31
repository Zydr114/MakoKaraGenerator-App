using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Effects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

// ABI 来源：SkiaSharp v3.119.4 的 C 绑定及固定 native 子模块 7dbfc07。
// native 查询直接复制 std::string_view；其前两个槽与 C 头的 pointer/length 描述不同。
// 同官方 managed 绑定一样，不解释这两个槽，名字通过 SKRuntimeEffect 公共名字查询取得。
// 两个查询仅返回反射信息；SKRuntimeEffect 的创建、持有和释放仍由公共绑定负责。
internal static partial class RuntimeShaderReflection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUniform
    {
        public nint NameStorage0;
        public nint NameStorage1;
        public nint Offset;
        public int Type;
        public int Count;
        public int Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeChild
    {
        public nint NameStorage0;
        public nint NameStorage1;
        public int Type;
        public int Index;
    }
    [LibraryImport("libSkiaSharp", EntryPoint = "sk_runtimeeffect_get_uniform_from_index")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void GetUniform(nint effect, int index, out NativeUniform uniform);
    [LibraryImport("libSkiaSharp", EntryPoint = "sk_runtimeeffect_get_child_from_index")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void GetChild(nint effect, int index, out NativeChild child);

    internal static void Validate(SKRuntimeEffect effect, ShaderProgramDefinition definition, CancellationToken cancellationToken)
    {
        if (effect.Uniforms.Count != definition.Uniforms.Length)
            throw Error(definition, "ShaderDeclarationMismatch", "uniforms", "清单与原生 uniform 数量不同。");
        var declarations = definition.Uniforms.ToDictionary(value => value.Name, StringComparer.Ordinal);
        var occupied = new HashSet<int>();
        for (int index = 0; index < effect.Uniforms.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = effect.Uniforms[index];
            if (!declarations.TryGetValue(name, out var declaration))
                throw Error(definition, "ShaderDeclarationMismatch", "uniforms." + name, "原生 uniform 未在清单声明。");
            GetUniform(effect.Handle, index, out var native);
            if (native.Type != NativeType(declaration.Kind) || ((native.Flags & 1) != 0) != declaration.ArrayLength.HasValue
                || native.Count != (declaration.ArrayLength ?? 1) || ((native.Flags & 2) != 0) != (declaration.Kind == ShaderUniformKind.Color))
                throw Error(definition, "ShaderUniformTypeMismatch", "uniforms." + name, "实际类型、数组长度或 color 标记与清单不同。");
            // Array=1, Color=2, Vertex=4, Fragment=8, HalfPrecision=16；half 仍使用 float32 uniform ABI。
            long bytes = declaration.ScalarCount * sizeof(float), offset = native.Offset;
            if ((native.Flags & ~31) != 0 || offset < 0 || offset > effect.UniformSize || bytes > effect.UniformSize - offset
                || offset % sizeof(float) != 0)
                throw Error(definition, "ShaderNativeAbiMismatch", "uniforms." + name, "原生标志或缓冲区边界无效。");
            for (long position = offset; position < offset + bytes; position += sizeof(float))
                if (!occupied.Add((int)position)) throw Error(definition, "ShaderNativeAbiMismatch", "uniforms." + name, "原生 uniform 缓冲区重叠。");
        }
        if (effect.Children.Count != definition.Children.Length)
            throw Error(definition, "ShaderDeclarationMismatch", "children", "清单与原生 child 数量不同。");
        var children = definition.Children.ToDictionary(value => value.Name, StringComparer.Ordinal);
        for (int index = 0; index < effect.Children.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = effect.Children[index];
            if (!children.ContainsKey(name)) throw Error(definition, "ShaderDeclarationMismatch", "children." + name, "原生 child 未在清单声明。");
            GetChild(effect.Handle, index, out var native);
            if (native.Type != 0 || native.Index != index)
                throw Error(definition, "ShaderChildTypeMismatch", "children." + name, "只接受原生 shader child；类型或顺序无效。");
        }
    }
    private static int NativeType(ShaderUniformKind kind) => kind switch
    {
        ShaderUniformKind.Float => 0, ShaderUniformKind.Float2 => 1, ShaderUniformKind.Float3 => 2,
        ShaderUniformKind.Float4 or ShaderUniformKind.Color => 3, ShaderUniformKind.Matrix3x3 => 5,
        ShaderUniformKind.Int => 7, _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    private static ShaderProgramException Error(ShaderProgramDefinition definition, string code, string field, string message)
        => new(code, definition.SourcePath, field, message);
}
