namespace MakoKaraGenerator.Scripting;

/// <summary>封闭区域材质描述；没有 native shader 或可写工程引用。</summary>
public abstract record MaterialValue
{
    private MaterialValue() { }
    /// <summary>建立纯色材质；透明色用于明确禁用填充。</summary>
    public static MaterialValue Solid(ColorRgba color) => new SolidMaterial(color);
    /// <summary>建立规范渐变材质，不保留第二个基色。</summary>
    public static MaterialValue Gradient(GradientValue gradient) => new GradientMaterial(gradient);
    /// <summary>引用固定包导出及固定参数；不编译帧内源码。</summary>
    public static MaterialValue PackageExport(MaterialHandle handle, ParameterValues parameters) => new PackageMaterial(handle, parameters);
    /// <summary>唯一纯色数据。</summary>
    public sealed record SolidMaterial : MaterialValue
    {
        internal SolidMaterial(ColorRgba color) { Color = color; }
        /// <summary>直乘颜色。</summary>
        public ColorRgba Color { get; }
    }
    /// <summary>唯一规范渐变数据。</summary>
    public sealed record GradientMaterial : MaterialValue
    {
        internal GradientMaterial(GradientValue gradient) { ArgumentNullException.ThrowIfNull(gradient); Value = gradient; }
        /// <summary>完整渐变，不提供猜测纯色的方法。</summary>
        public GradientValue Value { get; }
    }
    /// <summary>固定包材质导出。</summary>
    public sealed record PackageMaterial : MaterialValue
    {
        internal PackageMaterial(MaterialHandle handle, ParameterValues parameters)
        { ArgumentNullException.ThrowIfNull(handle); ArgumentNullException.ThrowIfNull(parameters); Handle = handle; Parameters = parameters; }
        /// <summary>固定完整修订的逻辑句柄。</summary>
        public MaterialHandle Handle { get; }
        /// <summary>固定参数。</summary>
        public ParameterValues Parameters { get; }
        /// <summary>本帧显式节点／uniform 覆盖；不修改包、参数或程序。</summary>
        public GraphUniformValues Uniforms { get; private init; } = GraphUniformValues.Empty;
        internal PackageMaterial WithUniform(string nodeId, string name, UniformValue value)
            => this with { Uniforms = Uniforms.Set(nodeId, name, value) };
        internal PackageMaterial WithoutUniforms() => Uniforms.Values.IsEmpty ? this : this with { Uniforms = GraphUniformValues.Empty };
    }
}
