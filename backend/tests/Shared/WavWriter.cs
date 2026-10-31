using System.Buffers.Binary;

namespace MakoKaraGenerator.TestSupport;

/// <summary>
/// 生成测试用的 WAV 文件（16 位 PCM 单声道）。
/// </summary>
/// <remarks>
/// 刻意在测试里自己写文件而不是用 FFmpeg 生成：
/// 一是避免 lavfi 过滤串在不同 shell/转义规则下的差异，
/// 二是这样能精确控制每段的振幅，"波形峰值对不对"才有确定的期望值。
/// </remarks>
internal static class WavWriter
{
    /// <summary>把一个浮点采样序列写成 WAV 文件。</summary>
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        int dataSize = samples.Length * sizeof(short);

        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);                          // fmt 块大小
        writer.Write((short)1);                    // PCM
        writer.Write((short)1);                    // 单声道
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));  // 字节率
        writer.Write((short)sizeof(short));        // 块对齐
        writer.Write((short)16);                   // 位深

        writer.Write("data"u8);
        writer.Write(dataSize);

        foreach (float sample in samples)
        {
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }
    }

    /// <summary>
    /// 生成一段"振幅分段变化"的测试信号。
    /// </summary>
    /// <param name="sampleRate">采样率。</param>
    /// <param name="segments">每段：时长（秒）与振幅。</param>
    /// <returns>单声道采样。</returns>
    /// <remarks>
    /// 每段中间放一个固定频率的正弦波，因此该段的峰值绝对值就等于给定振幅，
    /// 便于对峰值金字塔做精确断言。
    /// </remarks>
    public static float[] BuildSegmentedTone(int sampleRate, params (double Seconds, float Amplitude)[] segments)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentNullException.ThrowIfNull(segments);

        List<float> samples = [];
        double phase = 0d;
        const double Frequency = 440d;

        foreach ((double seconds, float amplitude) in segments)
        {
            int count = (int)Math.Round(seconds * sampleRate);

            for (int i = 0; i < count; i++)
            {
                samples.Add(amplitude * (float)Math.Sin(phase));
                phase += 2d * Math.PI * Frequency / sampleRate;

                if (phase > 2d * Math.PI)
                {
                    phase -= 2d * Math.PI;
                }
            }
        }

        return [.. samples];
    }

    /// <summary>把 32 位浮点采样按小端写入字节数组，供构造 PCM 流使用。</summary>
    public static byte[] ToFloat32LittleEndian(ReadOnlySpan<float> samples)
    {
        byte[] bytes = new byte[samples.Length * sizeof(float)];

        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), samples[i]);
        }

        return bytes;
    }
}
