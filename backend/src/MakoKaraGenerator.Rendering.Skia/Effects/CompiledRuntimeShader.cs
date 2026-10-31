using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>渲染后端的借用 shader 输入；不是脚本 SDK 资源或可持久化模型。</summary>
public sealed class RuntimeShaderInput
{
    /// <summary>标明实际 surface 语义；调用者负责使用正确的颜色或 raw coverage shader。</summary>
    public RuntimeShaderInput(SKShader shader, ShaderSurfaceKind kind)
    {
        ArgumentNullException.ThrowIfNull(shader);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Shader = shader; Kind = kind;
    }
    /// <summary>借用的 native shader；创建时必须有效，调用者保留释放责任。</summary>
    public SKShader Shader { get; }
    /// <summary>输入的实际语义编码，不从像素推断。</summary>
    public ShaderSurfaceKind Kind { get; }
}

/// <summary>一次编译、严格反射校验、可重复创建帧 shader 的后端原语；不执行通用图。</summary>
public sealed class CompiledRuntimeShader : IDisposable
{
    private readonly object _gate = new();
    private readonly SKRuntimeEffect _effect;
    private bool _disposed;
    private CompiledRuntimeShader(ShaderProgramDefinition definition, SKRuntimeEffect effect)
    { Definition = definition; _effect = effect; ProgramKey = MakeKey(definition); }
    /// <summary>本程序的唯一固定声明。</summary>
    public ShaderProgramDefinition Definition { get; }
    /// <summary>包含源码、profile、声明、固定 ABI 和锁定后端版本的内容身份。</summary>
    public string ProgramKey { get; }
    /// <summary>编译并严格验证实际 native 类型；失败释放 effect，取消仍为取消。</summary>
    public static CompiledRuntimeShader Compile(ShaderProgramDefinition definition, int scalarBudget = ShaderProgramDefinition.DefaultScalarBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scalarBudget);
        cancellationToken.ThrowIfCancellationRequested();
        if (definition.Profile != ShaderProgramDefinition.RasterProfile)
            throw new ShaderProgramException("UnsupportedShaderProfile", definition.SourcePath, "profile", definition.Profile);
        if (definition.ScalarCount > scalarBudget)
            throw new ShaderProgramException("ShaderUniformBudgetExceeded", definition.SourcePath, "uniforms", $"{definition.ScalarCount} > {scalarBudget}");
        SKRuntimeEffect? effect = null;
        try
        {
            effect = SKRuntimeEffect.CreateShader(definition.Source, out string error);
            if (effect == null) throw new ShaderProgramException("ShaderCompilationFailed", definition.SourcePath, "source", error ?? "原生编译未返回程序。");
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeShaderReflection.Validate(effect, definition, cancellationToken);
            return new(definition, effect);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            effect?.Dispose();
            throw new ShaderProgramException("ShaderNativeInterfaceUnavailable", definition.SourcePath, "profile", exception.Message, exception);
        }
        catch { effect?.Dispose(); throw; }
    }
    /// <summary>创建完整值集的帧 shader；返回值须释放，不拥有 child。此处不编译源码。</summary>
    public SKShader CreateShader(UniformValues values, IReadOnlyDictionary<string, RuntimeShaderInput> children,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values); ArgumentNullException.ThrowIfNull(children);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            ValidateNames(values.Values.Keys, Definition.Uniforms.Select(value => value.Name), "uniforms");
            ValidateNames(children.Keys, Definition.Children.Select(value => value.Name), "children");
            if (Definition.Uniforms.Length == 0 && Definition.Children.Length == 0)
            {
                return CompleteShader(_effect.ToShader(), cancellationToken);
            }
            using var uniforms = new SKRuntimeEffectUniforms(_effect);
            using var nativeChildren = new SKRuntimeEffectChildren(_effect);
            foreach (var declaration in Definition.Uniforms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = values.Values[declaration.Name];
                if (!Matches(declaration.Kind, value.Kind) || declaration.ArrayLength != value.ArrayLength)
                    throw Error("ShaderUniformValueMismatch", "uniforms." + declaration.Name, "值类型或数组长度与固定声明不同。");
                if (declaration.Kind == ShaderUniformKind.Int)
                    uniforms.Add(declaration.Name, declaration.ArrayLength.HasValue ? value.Integers.ToArray() : value.Integers[0]);
                else
                {
                    float[] components = ToNative(value, declaration.Name);
                    if (declaration.Kind == ShaderUniformKind.Float && !declaration.ArrayLength.HasValue)
                        uniforms.Add(declaration.Name, components[0]);
                    else uniforms.Add(declaration.Name, components);
                }
            }
            foreach (var declaration in Definition.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = children[declaration.Name];
                if (child == null || child.Kind != declaration.Kind || child.Shader.Handle == nint.Zero)
                    throw Error("ShaderChildValueMismatch", "children." + declaration.Name, "输入缺失、编码不符或已经释放。");
                nativeChildren.Add(declaration.Name, child.Shader);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return CompleteShader(_effect.ToShader(uniforms, nativeChildren), cancellationToken);
        }
    }
    private SKShader CompleteShader(SKShader? shader, CancellationToken cancellationToken)
    {
        if (shader == null) throw Error("ShaderCreationFailed", "source", "原生未返回 shader。");
        if (cancellationToken.IsCancellationRequested) { shader.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return shader;
    }
    private void ValidateNames(IEnumerable<string> actual, IEnumerable<string> expected, string field)
    {
        var names = actual.ToHashSet(StringComparer.Ordinal);
        foreach (string name in expected)
            if (!names.Remove(name)) throw Error("ShaderInputMissing", field + "." + name, "声明的输入缺失。");
        if (names.Count != 0) throw Error("ShaderInputUnknown", field + "." + names.Order(StringComparer.Ordinal).First(), "输入未在固定声明中出现。");
    }
    private float[] ToNative(UniformValue value, string name)
    {
        var components = new float[value.Numbers.Length];
        for (int index = 0; index < components.Length; index++)
        {
            double component = value.Numbers[index]; float converted = (float)component;
            if (!float.IsFinite(converted) || (component != 0 && converted == 0))
                throw Error("ShaderUniformNativeRange", "uniforms." + name, "分量溢出 float32 或非零下溢为零。");
            components[index] = converted;
        }
        if (value.Kind == UniformValueKind.Matrix3x3)
            for (int offset = 0; offset < components.Length; offset += 9)
            {
                (components[offset + 1], components[offset + 3]) = (components[offset + 3], components[offset + 1]);
                (components[offset + 2], components[offset + 6]) = (components[offset + 6], components[offset + 2]);
                (components[offset + 5], components[offset + 7]) = (components[offset + 7], components[offset + 5]);
            }
        return components;
    }
    private static bool Matches(ShaderUniformKind kind, UniformValueKind value) => kind switch
    {
        ShaderUniformKind.Float => value == UniformValueKind.Float, ShaderUniformKind.Float2 => value == UniformValueKind.Float2,
        ShaderUniformKind.Float3 => value == UniformValueKind.Float3, ShaderUniformKind.Float4 => value == UniformValueKind.Float4,
        ShaderUniformKind.Int => value == UniformValueKind.Int, ShaderUniformKind.Color => value == UniformValueKind.Color,
        ShaderUniformKind.Matrix3x3 => value == UniformValueKind.Matrix3x3, _ => false,
    };
    private static string MakeKey(ShaderProgramDefinition definition)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("uniform-abi-v1"); writer.Write("SkiaSharp-3.119.4");
            writer.Write(typeof(SKRuntimeEffect).Assembly.GetName().Version!.ToString()); writer.Write(SkiaSharpVersion.Native.ToString());
            writer.Write(definition.Profile); writer.Write(definition.Source);
            writer.Write(definition.Uniforms.Length);
            foreach (var value in definition.Uniforms.OrderBy(value => value.Name, StringComparer.Ordinal))
            { writer.Write(value.Name); writer.Write((int)value.Kind); writer.Write(value.ArrayLength ?? 0); }
            writer.Write(definition.Children.Length);
            foreach (var value in definition.Children.OrderBy(value => value.Name, StringComparer.Ordinal))
            { writer.Write(value.Name); writer.Write((int)value.Kind); }
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }
    private ShaderProgramException Error(string code, string field, string message) => new(code, Definition.SourcePath, field, message);
    /// <summary>幂等释放编译程序；已创建 shader 的原生引用独立有效。</summary>
    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; _effect.Dispose(); } }
}
