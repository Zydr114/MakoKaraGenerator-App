namespace MakoKaraGenerator.Export;

/// <summary>把渲染帧从预乘 Alpha 换成直乘（straight）Alpha，就地改字节。</summary>
/// <remarks>
/// <para>
/// 渲染管线产出的是<b>预乘</b> BGRA（ADR-0021：颜色已经乘过 Alpha），而 ffmpeg 的
/// <c>bgra</c> 指的是<b>直乘</b>。全不透明时两者数值相同，所以不透明成片一直没有问题；
/// 但透明输出里半透明像素占了多数，直接喂预乘数据会让颜色被二次压暗
/// （红色的 <c>alpha=128</c> 会变成 <c>(128,0,0,128)</c>，合成后看起来是暗红）。
/// </para>
/// <para>
/// 只处理 <c>0 &lt; alpha &lt; 255</c> 的像素：Alpha 为 0 时颜色必然也是 0（预乘的性质），
/// 为 255 时除法是恒等变换，跳过它们既省时间又不引入误差。
/// </para>
/// </remarks>
internal static class AlphaFrameConverter
{
    /// <summary>把一帧的像素换成直乘 Alpha。</summary>
    /// <param name="source">源像素（BGRA8888 预乘）。</param>
    /// <param name="destination">目标缓冲，长度不小于 <paramref name="source"/>。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 太短时抛出。</exception>
    /// <remarks>
    /// 写成"源 → 目标"而不是就地改：<c>RenderFrame</c> 只暴露只读的像素视图，
    /// 而编码器本来就是先拷进自己的缓冲区再写管道，顺手在这一步换算代价为零。
    /// </remarks>
    internal static void ToStraight(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("目标缓冲区比源像素短。", nameof(destination));
        }

        source.CopyTo(destination);

        for (int offset = 0; offset < source.Length; offset += 4)
        {
            byte alpha = source[offset + 3];

            if (alpha is 0 or 255)
            {
                continue;
            }

            // 向上取整再夹到 255：除法丢的那点精度不该让半透明白变成灰。
            destination[offset] = (byte)Math.Min(255, ((source[offset] * 255) + (alpha / 2)) / alpha);
            destination[offset + 1] = (byte)Math.Min(255, ((source[offset + 1] * 255) + (alpha / 2)) / alpha);
            destination[offset + 2] = (byte)Math.Min(255, ((source[offset + 2] * 255) + (alpha / 2)) / alpha);
        }
    }
}
