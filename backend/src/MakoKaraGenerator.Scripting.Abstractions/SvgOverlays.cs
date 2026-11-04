using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>固定 SVG 的矢量实例；descriptor viewport 归一化到单位方形后应用仿射一次。</summary>
public readonly record struct VectorVisual
{
    /// <summary>指定真实矢量句柄、完整单位方形到世界矩阵和直乘 sRGB 颜色。</summary>
    public VectorVisual(VectorHandle vector, Affine2 unitToWorld, ColorRgba tint)
    {
        Vector = vector; UnitToWorld = unitToWorld; Tint = tint;
        if (!IsValid) throw new ArgumentException("矢量实例须有固定资源及可逆有限的世界几何。");
    }
    /// <summary>固定 SVG 矢量资源，不是原始路径或 native Picture。</summary>
    public VectorHandle Vector { get; }
    /// <summary>单位方形 [0,1]² 到世界逻辑空间；支持旋转、镜像、非等比缩放和斜切。</summary>
    public Affine2 UnitToWorld { get; }
    /// <summary>RGB 和 Alpha 独立直乘，白色保留原资源颜色；只预乘一次。</summary>
    public ColorRgba Tint { get; }
    /// <summary>default 无效；四角必须有限且矩阵可逆。</summary>
    public bool IsValid => Vector is not null && SvgOverlayValidation.UnitAffine(UnitToWorld);
}

/// <summary>固定 SVG atlas 的一个实例；作者不能指定裁片或任意 atlas UV。</summary>
public readonly record struct AtlasSprite
{
    /// <summary>指定真实资源档位、完整单位方形到世界矩阵和直乘 sRGB 颜色。</summary>
    public AtlasSprite(SpriteHandle sprite, Affine2 unitToWorld, ColorRgba tint)
    {
        Sprite = sprite; UnitToWorld = unitToWorld; Tint = tint;
        if (!IsValid) throw new ArgumentException("Atlas 精灵须有固定资源档位及可逆有限的世界几何。");
    }
    /// <summary>固定 SVG 资源与声明的 atlas 档位，不包含设备纹理。</summary>
    public SpriteHandle Sprite { get; }
    /// <summary>资源内容单位方形 [0,1]² 到世界逻辑空间；支持完整仿射。</summary>
    public Affine2 UnitToWorld { get; }
    /// <summary>RGB 和 Alpha 独立直乘，白色保留原资源颜色；只预乘一次。</summary>
    public ColorRgba Tint { get; }
    /// <summary>default 无效；四角必须有限且矩阵可逆。</summary>
    public bool IsValid => Sprite is not null && SvgOverlayValidation.UnitAffine(UnitToWorld);
}

/// <summary>不可变 SVG 矢量批次；每个实例（包括透明项）计入绘制项预算。</summary>
public sealed record PathBatch(EffectInstanceId InstanceId, OutputId Id, OverlayLayer Layer, ImmutableArray<VectorVisual> Paths)
    : OverlayBatch(InstanceId, Id, Layer)
{
    /// <inheritdoc/>
    public override bool IsValid => !Paths.IsDefault && Paths.All(static path => path.IsValid);
    /// <inheritdoc/>
    public override long ItemCount => Paths.Length;
}

/// <summary>不可变 SVG atlas 批次；每个实例（包括透明项）计入绘制项预算。</summary>
public sealed record AtlasSpriteBatch(EffectInstanceId InstanceId, OutputId Id, OverlayLayer Layer, ImmutableArray<AtlasSprite> Sprites)
    : OverlayBatch(InstanceId, Id, Layer)
{
    /// <inheritdoc/>
    public override bool IsValid => !Sprites.IsDefault && Sprites.All(static sprite => sprite.IsValid);
    /// <inheritdoc/>
    public override long ItemCount => Sprites.Length;
}

internal static class SvgOverlayValidation
{
    internal static bool UnitAffine(Affine2 matrix)
    {
        if (!matrix.TryInverse(out _)) return false;
        try
        {
            matrix.Map(new(0, 0)); matrix.Map(new(1, 0)); matrix.Map(new(0, 1)); matrix.Map(new(1, 1));
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
