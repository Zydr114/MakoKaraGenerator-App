namespace MakoKaraGenerator.Rendering;

/// <summary>真实彩色固定字形部件；不是覆盖蒙版或组的虚拟填充。</summary>
public enum SceneColorPart
{
    /// <summary>基础文字填充。</summary>
    BaseFill,
    /// <summary>基础文字外部描边。</summary>
    BaseStroke,
    /// <summary>基础文字阴影。</summary>
    BaseShadow,
    /// <summary>关联注音填充。</summary>
    RubyFill,
    /// <summary>关联注音外部描边。</summary>
    RubyStroke,
    /// <summary>关联注音阴影。</summary>
    RubyShadow,
}
