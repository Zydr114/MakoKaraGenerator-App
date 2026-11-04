using System.Numerics;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Particles.V2;

internal sealed class ExactParticleContext(ParticleOwner owner, int maximumBits, CancellationToken token,
    ParticleQueryBudget? query = null, long timeUs = 0)
{
    internal ParticleOwner Owner { get; } = owner;
    internal void Check(BigInteger value)
    {
        token.ThrowIfCancellationRequested();
        if (BigInteger.Abs(value).GetBitLength() > maximumBits)
            throw Owner.Failure(query is null ? "PrepareParticles" : "ReadParticleBirths", timeUs,
                "ParticleIntegerBudgetExceeded", "精确粒子积分的整数位数超过固定预算。");
    }
    internal void Cancel() => token.ThrowIfCancellationRequested();
    internal void Inversion() { token.ThrowIfCancellationRequested(); query?.Inversion(Owner, timeUs); }
    internal BigInteger Multiply(BigInteger left, BigInteger right)
    { var result = left * right; Check(result); return result; }
    internal BigInteger Add(BigInteger left, BigInteger right)
    { var result = left + right; Check(result); return result; }
}

internal readonly record struct ExactQuantity(BigInteger Numerator, BigInteger Denominator)
{
    internal static ExactQuantity Zero => new(BigInteger.Zero, BigInteger.One);
    internal static ExactQuantity Create(BigInteger numerator, BigInteger denominator, ExactParticleContext context)
    {
        context.Check(numerator); context.Check(denominator);
        if (denominator.Sign <= 0) throw new InvalidOperationException("精确积分分母必须为正。");
        if (numerator.IsZero) return Zero;
        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        return new(numerator / gcd, denominator / gcd);
    }
    internal static ExactQuantity PerMicrosecond(double value, ExactParticleContext context)
    {
        if (!double.IsFinite(value) || value < 0)
            throw context.Owner.Failure("PrepareParticles", 0, "InvalidParticleRate", "速率必须有限且非负。");
        if (value == 0) return Zero;
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        int exponent = (int)((bits >> 52) & 2047);
        BigInteger significand = bits & 0x000f_ffff_ffff_ffffUL;
        if (exponent != 0) significand += BigInteger.One << 52;
        int power = (exponent == 0 ? -1022 : exponent - 1023) - 52;
        return Create(power >= 0 ? significand << power : significand,
            power >= 0 ? new BigInteger(1_000_000) : new BigInteger(1_000_000) << -power, context);
    }
    internal ExactQuantity Add(ExactQuantity other, ExactParticleContext context)
    {
        var gcd = BigInteger.GreatestCommonDivisor(Denominator, other.Denominator);
        var left = other.Denominator / gcd; var right = Denominator / gcd;
        return Create(context.Add(context.Multiply(Numerator, left), context.Multiply(other.Numerator, right)),
            context.Multiply(Denominator, left), context);
    }
    internal ExactQuantity Subtract(ExactQuantity other, ExactParticleContext context)
        => Add(new(-other.Numerator, other.Denominator), context);
    internal BigInteger Floor => Numerator / Denominator;
    internal bool AtLeast(ulong value, ExactParticleContext context)
        => Numerator >= context.Multiply(value, Denominator);
}

// 原始轨道前缀与 episode 起点相减；不在 token／启用裁剪边界重新积分。
internal sealed class CompiledRateTrack
{
    private readonly Entry[] _entries;
    private sealed record Entry(TimeRange Range, BigInteger Start, BigInteger End, BigInteger Denominator,
        ExactQuantity Prefix, ExactQuantity EndPrefix);

    internal CompiledRateTrack(RateTrack track, ExactParticleContext context)
    {
        _entries = new Entry[track.Segments.Length];
        ExactQuantity prefix = ExactQuantity.Zero;
        for (int index = 0; index < _entries.Length; index++)
        {
            context.Cancel(); var segment = track.Segments[index];
            var start = ExactQuantity.PerMicrosecond(segment.StartPerSecond, context);
            var end = ExactQuantity.PerMicrosecond(segment.EndPerSecond, context);
            var gcd = BigInteger.GreatestCommonDivisor(start.Denominator, end.Denominator);
            var denominator = context.Multiply(start.Denominator, end.Denominator / gcd);
            var p0 = context.Multiply(start.Numerator, denominator / start.Denominator);
            var p1 = context.Multiply(end.Numerator, denominator / end.Denominator);
            // 完整段先化简；不保留可约掉的 D 分母。
            var total = ExactQuantity.Create(context.Multiply(context.Add(p0, p1), segment.Range.DurationUs),
                context.Multiply(denominator, 2), context);
            var next = prefix.Add(total, context);
            _entries[index] = new(segment.Range, p0, p1, denominator, prefix, next); prefix = next;
        }
    }

    internal ExactQuantity IntegralAt(long timeUs, ExactParticleContext context)
    {
        context.Cancel(); int low = 0, high = _entries.Length;
        while (low < high)
        {
            context.Cancel(); int middle = low + (high - low) / 2;
            if (_entries[middle].Range.StartUs <= timeUs) low = middle + 1; else high = middle;
        }
        if (low == 0) return ExactQuantity.Zero;
        var entry = _entries[low - 1];
        if (timeUs >= entry.Range.EndUs) return entry.EndPrefix;
        long offset = timeUs - entry.Range.StartUs;
        if (offset == 0) return entry.Prefix;
        ExactQuantity increment;
        if (entry.Start == entry.End)
            increment = ExactQuantity.Create(context.Multiply(entry.Start, offset), entry.Denominator, context);
        else
        {
            BigInteger duration = entry.Range.DurationUs, x = offset;
            var first = context.Multiply(context.Multiply(context.Multiply(2, duration), entry.Start), x);
            var second = context.Multiply(entry.End - entry.Start, context.Multiply(x, x));
            increment = ExactQuantity.Create(context.Add(first, second),
                context.Multiply(context.Multiply(2, duration), entry.Denominator), context);
        }
        return entry.Prefix.Add(increment, context);
    }
}
