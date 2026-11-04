namespace MakoKaraGenerator.Scripting;

/// <summary>封闭图元输出的统一保守字节账；不包含固定包资源或 native 绘制工作。</summary>
public static class OverlayMemoryCost
{
    /// <summary>按真实实例、拖尾对象和节点数估算输出字节；透明项同样计费，算术溢出失败。</summary>
    public static long Estimate(OverlayBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!batch.IsValid) throw new ArgumentException("输出字节估算需要有效的封闭批次。", nameof(batch));
        checked
        {
            return 256 + (batch switch
            {
                DotBatch dots => dots.ItemCount * 64,
                SegmentBatch segments => segments.ItemCount * 80,
                TrailBatch trails => trails.ItemCount * 64 + (long)trails.Trails.Length * 64,
                SpriteBatch sprites => sprites.ItemCount * 128,
                PathBatch paths => paths.ItemCount * 112,
                AtlasSpriteBatch sprites => sprites.ItemCount * 112,
                _ => throw new ArgumentException("未知封闭图元批次。", nameof(batch)),
            });
        }
    }
}
