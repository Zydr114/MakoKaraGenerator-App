namespace MakoKaraGenerator.Core.Media;

/// <summary>An absolute presentation timestamp retaining signed integer ticks.</summary>
public readonly record struct RationalTimestamp
{
    /// <summary>Creates a read-only RationalTimestamp value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="ticks">Signed source ticks; negative and nonzero origins are retained.</param>
    /// <param name="timeBase">The positive reduced number of seconds per tick.</param>
    public RationalTimestamp(
        long ticks,
        RationalTimeBase timeBase)
    {
        Ticks = ticks;
        TimeBase = timeBase;
    }

    /// <summary>Signed source ticks; negative and nonzero origins are retained.</summary>
    public long Ticks { get; }

    /// <summary>The positive reduced number of seconds per tick.</summary>
    public RationalTimeBase TimeBase { get; }

}
