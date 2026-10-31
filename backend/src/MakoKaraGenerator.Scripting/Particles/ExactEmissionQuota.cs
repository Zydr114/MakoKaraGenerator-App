using System.Numerics;

namespace MakoKaraGenerator.Scripting.Particles;

// 保留输入 double 的精确值，避免反复相加在边界漏生或重复生。
internal readonly record struct ExactEmissionQuota(BigInteger Numerator, BigInteger Denominator)
{
    public static ExactEmissionQuota Zero => new(BigInteger.Zero, BigInteger.One);
    public static ExactEmissionQuota PerMicrosecond(double rate)
    {
        if (rate == 0) return Zero;
        ulong bits = BitConverter.DoubleToUInt64Bits(rate);
        int exponent = (int)((bits >> 52) & 2047);
        BigInteger mantissa = bits & 0xfffffffffffffUL;
        if (exponent != 0) mantissa += BigInteger.One << 52;
        int power = (exponent == 0 ? -1022 : exponent - 1023) - 52;
        return Reduced(power >= 0 ? mantissa << power : mantissa, power >= 0 ? 1_000_000 : new BigInteger(1_000_000) << -power);
    }
    public static ExactEmissionQuota Reduced(BigInteger numerator, BigInteger denominator)
    {
        if (numerator.IsZero) return Zero;
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return new(numerator / divisor, denominator / divisor);
    }
    public Schedule During(ExactEmissionQuota rate, long duration)
    {
        var denominator = Denominator / BigInteger.GreatestCommonDivisor(Denominator, rate.Denominator) * rate.Denominator;
        return new(Numerator * (denominator / Denominator), rate.Numerator * (denominator / rate.Denominator), denominator, duration);
    }
    internal sealed class Schedule(BigInteger start, BigInteger rate, BigInteger denominator, long duration)
    {
        private BigInteger _next = BigInteger.One;
        public long? NextOffset()
        {
            var missing = _next * denominator - start;
            if (rate.IsZero && missing.Sign > 0) return null;
            var offset = missing.Sign <= 0 ? BigInteger.Zero : (missing + rate - 1) / rate;
            if (offset >= duration) return null;
            _next++; return (long)offset;
        }
        public ExactEmissionQuota Remaining => Reduced(start + rate * duration - (_next - 1) * denominator, denominator);
    }
}
