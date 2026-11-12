namespace MakoKaraGenerator.Rendering;

/// <summary>真实彩色固定字形部件；不是覆盖蒙版或组的虚拟填充。</summary>
public enum SceneColorPart
{
    /// <summary>基础文字填充。</summary>
    BaseFill,
    /// <summary>基础文字描边：原样捕获时含全部各圈；描边材质的区域只是最内层一圈，外圈另由 <see cref="BaseOuterStroke"/> 原样捕获。</summary>
    BaseStroke,
    /// <summary>基础文字阴影。</summary>
    BaseShadow,
    /// <summary>关联注音填充。</summary>
    RubyFill,
    /// <summary>关联注音描边；含义同 <see cref="BaseStroke"/>。</summary>
    RubyStroke,
    /// <summary>关联注音阴影。</summary>
    RubyShadow,
    /// <summary>基础文字外发光。</summary>
    BaseGlow,
    /// <summary>关联注音外发光。</summary>
    RubyGlow,
    /// <summary>基础文字的外圈描边：多重描边第 2 层起的各圈，不含最内层占据的范围（D-P3-9）。</summary>
    BaseOuterStroke,
    /// <summary>关联注音的外圈描边。</summary>
    RubyOuterStroke,
}
