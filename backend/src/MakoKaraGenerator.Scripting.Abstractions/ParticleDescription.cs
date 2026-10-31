namespace MakoKaraGenerator.Scripting;

/// <summary>宿主保持模拟，作者可以选择只接收视图并自行绘制。</summary>
public enum ParticleRenderMode
{
    /// <summary>宿主自动绘制圆点及点状拖尾。</summary>
    Dots,
    /// <summary>纯视觉求值从 Particles 输出图元，宿主不重复绘制。</summary>
    Custom,
}

/// <summary>出生时固定的粒子外观与解析运动，不包含发射率或事件数量。</summary>
public abstract record ParticleDescription
{
    /// <summary>只有 SDK 的封闭发射描述能继承，避免保存作者可执行对象。</summary>
    private protected ParticleDescription() { }
    /// <summary>严格正长整数微秒寿命，末端不显示。</summary>
    public required long LifetimeUs { get; init; }
    /// <summary>严格正有限逻辑像素直径。</summary>
    public required double SizePx { get; init; }
    /// <summary>直乘 sRGB 颜色。</summary>
    public required ColorRgba Color { get; init; }
    /// <summary>世界逻辑像素／秒的出生速度。</summary>
    public Vector2 Velocity { get; init; }
    /// <summary>横纵出生散布的半宽，逻辑像素，有限非负。</summary>
    public double SpreadPx { get; init; }
    /// <summary>固定采样拖尾窗口，整数微秒，零关闭。</summary>
    public long TrailDurationUs { get; init; }
    /// <summary>默认自动圆点；Custom 只关闭自动绘制，模拟和预算保持。</summary>
    public ParticleRenderMode Rendering { get; init; } = ParticleRenderMode.Dots;
    /// <summary>公共图元层。</summary>
    public OverlayLayer Layer { get; init; } = OverlayLayer.AboveText;
    /// <summary>纯索引随机分流键。</summary>
    public required string SeedStreamKey { get; init; }
    /// <summary>恒定加速度及年龄曲线。</summary>
    public ParticleMotion Motion { get; init; } = ParticleMotion.ConstantVelocity;
    /// <summary>校验物理描述，不夹值、裁剪或隐式降级。</summary>
    public virtual void Validate()
    {
        if (LifetimeUs <= 0 || !double.IsFinite(SizePx) || SizePx <= 0 || !double.IsFinite(SpreadPx) || SpreadPx < 0
            || TrailDurationUs < 0 || !Enum.IsDefined(Rendering) || !Enum.IsDefined(Layer) || string.IsNullOrWhiteSpace(SeedStreamKey)
            || Motion?.Opacity is null || Motion.Size is null)
            throw new EffectOutputException("InvalidEmitter", "粒子外观、运动或随机身份无效。");
        if (Motion.Opacity.Start is < 0 or > 1 || Motion.Opacity.End is < 0 or > 1 || Motion.Size.Start < 0 || Motion.Size.End < 0)
            throw new EffectOutputException("InvalidEmitter", "年龄外观端值必须是有效透明度及非负尺寸。");
    }
}
