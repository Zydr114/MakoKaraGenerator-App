using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace MakoKaraGenerator.Scripting;

/// <summary>PCG32-v1 的纯索引随机服务；种子／实例／分流键固定，调用顺序不消耗全局游标。</summary>
public sealed class DeterministicRandom
{
    private const ulong Multiplier = 6364136223846793005;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ulong _seed;
    private readonly EffectInstanceId _instance;
    /// <summary>固定宿主种子及实例身份；不从系统钟或机器环境取种子。</summary>
    public DeterministicRandom(ulong seed, EffectInstanceId instance)
    {
        if (!instance.IsValid) throw new ArgumentException("随机服务需要有效实例身份。", nameof(instance));
        _ = StrictUtf8.GetByteCount(instance.Value); _seed = seed; _instance = instance;
    }
    /// <summary>算法合同；修改编码／播种／输出需提升模拟 profile。</summary>
    public static string Algorithm => "PCG32-v1";
    /// <summary>读取分流键第 index 项；支持完整 UInt64 索引，O(log index)，无状态推进。</summary>
    public uint GetUInt32(string streamKey, ulong index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        byte[] identity = StrictUtf8.GetBytes(_instance.Value), stream = StrictUtf8.GetBytes(streamKey);
        ReadOnlySpan<byte> profile = "MakoKara/PCG32-v1"u8;
        byte[] encoded = new byte[checked(profile.Length + 16 + identity.Length + stream.Length)];
        profile.CopyTo(encoded); int offset = profile.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(encoded.AsSpan(offset), _seed); offset += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(offset), checked((uint)identity.Length)); offset += 4;
        identity.CopyTo(encoded, offset); offset += identity.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(offset), checked((uint)stream.Length)); offset += 4;
        stream.CopyTo(encoded, offset);
        Span<byte> digest = stackalloc byte[32]; SHA256.HashData(encoded, digest);
        ulong initial = BinaryPrimitives.ReadUInt64LittleEndian(digest), sequence = BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]);
        return Sample(initial, sequence, index);
    }
    /// <summary>读取同一 UInt32 项除以 2³² 得到 [0,1)；不追加随机采样或舍入到 1。</summary>
    public double GetUnitDouble(string streamKey, ulong index) => GetUInt32(streamKey, index) / 4294967296d;
    internal static uint Sample(ulong initial, ulong sequence, ulong index)
    {
        unchecked
        {
            ulong increment = (sequence << 1) | 1;
            // 官方 PCG32 两步播种：先从零推进，再加入 initial，再推进一次。
            ulong state = (increment + initial) * Multiplier + increment;
            ulong accumulatedMultiplier = 1, accumulatedIncrement = 0, multiplier = Multiplier;
            while (index != 0)
            {
                if ((index & 1) != 0)
                {
                    accumulatedMultiplier *= multiplier;
                    accumulatedIncrement = accumulatedIncrement * multiplier + increment;
                }
                increment *= multiplier + 1; multiplier *= multiplier; index >>= 1;
            }
            state = accumulatedMultiplier * state + accumulatedIncrement;
            uint shifted = (uint)(((state >> 18) ^ state) >> 27);
            return BitOperations.RotateRight(shifted, (int)(state >> 59));
        }
    }
}
