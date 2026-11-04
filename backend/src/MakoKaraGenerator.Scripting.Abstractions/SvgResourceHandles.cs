namespace MakoKaraGenerator.Scripting;

/// <summary>固定包 SVG 矢量资源句柄；不包含路径、DOM 或 native 对象。</summary>
public sealed record VectorHandle
{
    internal VectorHandle(ResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
    }
    /// <summary>固定包、完整修订和 SVG descriptor 逻辑身份。</summary>
    public ResourceIdentity Identity { get; }
}

/// <summary>固定包 SVG atlas 档位句柄；档位与资源身份独立保存。</summary>
public sealed record SpriteHandle
{
    internal SpriteHandle(ResourceIdentity identity, string variantId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantId);
        Identity = identity; VariantId = variantId;
    }
    /// <summary>固定包、完整修订和 SVG descriptor 逻辑身份。</summary>
    public ResourceIdentity Identity { get; }
    /// <summary>本资源内的固定 atlas 档位，不拼接到资源逻辑身份。</summary>
    public string VariantId { get; }
}
