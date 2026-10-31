using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>固定包图片的世界图元；归一化图片裁片映射到单位方形后应用仿射一次。</summary>
public readonly record struct Sprite
{
    /// <summary>指定真实句柄、单位方形到世界矩阵、直乘 sRGB 颜色乘数和可选归一化裁片。</summary>
    public Sprite(ImageHandle image, Affine2 unitToWorld, ColorRgba tint, Rect2? source = null)
    {
        Image = image; UnitToWorld = unitToWorld; Tint = tint; Source = source ?? new(0, 0, 1, 1);
        if (!IsValid) throw new ArgumentException("精灵须有固定图片、可逆有限几何及图片内非空裁片。");
    }
    /// <summary>宿主固定图片，不是文件路径或 native 对象。</summary>
    public ImageHandle Image { get; }
    /// <summary>裁片单位方形 [0,1]² 到世界逻辑空间；支持旋转、镜像及非等比缩放。</summary>
    public Affine2 UnitToWorld { get; }
    /// <summary>图片直乘 RGB 及 Alpha 分别乘此值，白色保持原图片；只预乘一次。</summary>
    public ColorRgba Tint { get; }
    /// <summary>原图内归一化裁片，完整图片为 (0,0,1,1)。</summary>
    public Rect2 Source { get; }
    /// <summary>default 精灵无效；检查完整世界四角和可逆矩阵。</summary>
    public bool IsValid
    {
        get
        {
            if (Image is null || Source.Left < 0 || Source.Top < 0 || Source.Right > 1 || Source.Bottom > 1
                || Source.Width <= 0 || Source.Height <= 0 || !UnitToWorld.TryInverse(out _)) return false;
            try { UnitToWorld.Map(new(0, 0)); UnitToWorld.Map(new(1, 0)); UnitToWorld.Map(new(0, 1)); UnitToWorld.Map(new(1, 1)); return true; }
            catch (ArgumentOutOfRangeException) { return false; }
        }
    }
}

/// <summary>不可变图片精灵批次；每项（包括透明项）计入图元预算。</summary>
public sealed record SpriteBatch(EffectInstanceId InstanceId, OutputId Id, OverlayLayer Layer, ImmutableArray<Sprite> Sprites)
    : OverlayBatch(InstanceId, Id, Layer)
{
    /// <inheritdoc/>
    public override bool IsValid => !Sprites.IsDefault && Sprites.All(static sprite => sprite.IsValid);
    /// <inheritdoc/>
    public override long ItemCount => Sprites.Length;
}
