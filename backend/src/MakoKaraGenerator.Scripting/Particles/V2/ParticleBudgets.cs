using System.Numerics;

namespace MakoKaraGenerator.Scripting.Particles.V2;

/// <summary>宿主准备操作的共享硬限额；构造参数只允许收紧默认值。</summary>
public sealed class ParticlePrepareBudget
{
    private int _emitters, _rateEntries, _intervals, _episodes, _burstRows;
    private long _bytes;
    internal int MaximumBits { get; }
    private readonly int _emitterLimit, _rateEntryLimit, _intervalLimit, _episodeLimit, _burstRowLimit;
    private readonly long _byteLimit;

    /// <summary>建立准备预算；时间轨段与启用窗合计，源区段与 episode 也合计计数。</summary>
    public ParticlePrepareBudget(int emitterLimit = 32, int rateEntryLimit = 4096, int intervalLimit = 200_000,
        int episodeLimit = 200_000, int burstRowLimit = 50_000, int maximumBits = 4096, long byteLimit = 256L * 1024 * 1024)
    {
        Require(emitterLimit, 32); Require(rateEntryLimit, 4096); Require(intervalLimit, 200_000);
        Require(episodeLimit, 200_000); Require(burstRowLimit, 50_000); Require(maximumBits, 4096);
        if (byteLimit is <= 0 or > 256L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        _emitterLimit = emitterLimit; _rateEntryLimit = rateEntryLimit; _intervalLimit = intervalLimit;
        _episodeLimit = episodeLimit; _burstRowLimit = burstRowLimit; MaximumBits = maximumBits; _byteLimit = byteLimit;
    }

    /// <summary>准备目录、表和暂存分配的保守字节上界；跨实例累计。</summary>
    public void ReserveBytes(ParticleOwner owner, long bytes)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (bytes < 0 || bytes > _byteLimit - _bytes) throw owner.Failure("PrepareParticles", 0, "ParticlePrepareMemoryExceeded", "粒子准备内存超过固定预算。");
        _bytes += bytes;
    }

    internal void Reserve(ParticleOwner owner, int emitters = 0, int rateEntries = 0, int intervals = 0, int episodes = 0, int burstRows = 0)
    {
        if (Exceeds(_emitters, emitters, _emitterLimit) || Exceeds(_rateEntries, rateEntries, _rateEntryLimit)
            || Exceeds(_intervals, intervals, _intervalLimit) || Exceeds(_episodes, episodes, _episodeLimit)
            || Exceeds(_intervals + _episodes, intervals + episodes, Math.Min(_intervalLimit, _episodeLimit))
            || Exceeds(_burstRows, burstRows, _burstRowLimit))
            throw owner.Failure("PrepareParticles", 0, "ParticlePrepareBudgetExceeded", "粒子计划、源区段、episode 或 Burst 行超过准备预算。");
        _emitters += emitters; _rateEntries += rateEntries; _intervals += intervals; _episodes += episodes; _burstRows += burstRows;
    }

    private static bool Exceeds(int used, int count, int limit) => count < 0 || count > limit - used;
    private static void Require(int value, int maximum)
    { if (value <= 0 || value > maximum) throw new ArgumentOutOfRangeException(nameof(value)); }
}

/// <summary>本次请求由全部实例共享的计数器；失败后不发布局部出生表。</summary>
public sealed class ParticleQueryBudget
{
    private int _births, _alive, _trailNodes, _inversions;
    private long _bytes;
    private readonly int _birthLimit, _aliveLimit, _trailLimit, _inversionLimit;
    private readonly long _byteLimit;
    internal int MaximumBits { get; }

    /// <summary>建立查询硬门禁；允许测试或宿主收紧，不允许放大。</summary>
    public ParticleQueryBudget(int birthLimit = 50_000, int aliveLimit = 20_000, int inversionLimit = 4_000_000,
        int trailNodeLimit = 200_000, int maximumBits = 4096, long byteLimit = 64L * 1024 * 1024)
    {
        Require(birthLimit, 50_000); Require(aliveLimit, 20_000); Require(inversionLimit, 4_000_000);
        Require(trailNodeLimit, 200_000); Require(maximumBits, 4096);
        if (byteLimit is <= 0 or > 64L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        _birthLimit = birthLimit; _aliveLimit = aliveLimit; _inversionLimit = inversionLimit;
        _trailLimit = trailNodeLimit; MaximumBits = maximumBits; _byteLimit = byteLimit;
    }

    /// <summary>从固定出生表筛选后，独立预留本请求全部实例存活数量。</summary>
    public void ReserveAlive(ParticleOwner owner, int count, long timeUs)
        => Reserve(owner, count, ref _alive, _aliveLimit, timeUs, "LiveParticleBudgetExceeded");
    /// <summary>解析拖尾先计算节点数，再统一预留。</summary>
    public void ReserveTrailNodes(ParticleOwner owner, int count, long timeUs)
        => Reserve(owner, count, ref _trailNodes, _trailLimit, timeUs, "TrailSampleBudgetExceeded");
    /// <summary>本请求出生、拖尾和宿主暂存的保守内存上界；跨实例累计。</summary>
    public void ReserveBytes(ParticleOwner owner, long bytes, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (bytes < 0 || bytes > _byteLimit - _bytes) throw owner.Failure("ReadParticleBirths", timeUs, "ParticleFrameMemoryExceeded", "粒子请求内存超过固定预算。");
        _bytes += bytes;
    }

    internal void ReserveBirths(ParticleOwner owner, BigInteger count, long timeUs)
    {
        if (count.Sign < 0 || count > _birthLimit - _births)
            throw owner.Failure("ReadParticleBirths", timeUs, "EmissionRecordBudgetExceeded", "出生窗超过全部实例的固定记录预算。");
        _births += (int)count;
    }

    internal void Inversion(ParticleOwner owner, long timeUs)
        => Reserve(owner, 1, ref _inversions, _inversionLimit, timeUs, "ParticleInversionBudgetExceeded");
    private static void Reserve(ParticleOwner owner, int count, ref int used, int limit, long timeUs, string code)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (count < 0 || count > limit - used) throw owner.Failure("ReadParticleBirths", timeUs, code, "粒子请求超过独立计数预算。");
        used += count;
    }
    private static void Require(int value, int maximum)
    { if (value <= 0 || value > maximum) throw new ArgumentOutOfRangeException(nameof(value)); }
}
