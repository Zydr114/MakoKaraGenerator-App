using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

// Internal composition-root limits may only tighten the production ceilings.
// They never enter the author SDK or select a different sampling algorithm.
internal sealed class V2RuntimeBudgetOptions
{
    public static V2RuntimeBudgetOptions Default { get; } = new();
    private readonly int _anchorTimes, _textCalls, _futureEndpoints;
    private readonly long _frameBytes, _wallMilliseconds;
    private readonly TimeProvider _time;

    public V2RuntimeBudgetOptions(long frameBytes = ParticleLimits.MaximumFrameBytes,
        int anchorTimes = ParticleLimits.MaximumAnchorTimes, int textCalls = ParticleLimits.MaximumTextCalls,
        int futureEndpoints = ParticleLimits.MaximumFutureEndpoints,
        long wallMilliseconds = ParticleLimits.MaximumAnchorWallMilliseconds, TimeProvider? timeProvider = null)
    {
        Tighten(frameBytes, ParticleLimits.MaximumFrameBytes, nameof(frameBytes));
        Tighten(anchorTimes, ParticleLimits.MaximumAnchorTimes, nameof(anchorTimes));
        Tighten(textCalls, ParticleLimits.MaximumTextCalls, nameof(textCalls));
        Tighten(futureEndpoints, ParticleLimits.MaximumFutureEndpoints, nameof(futureEndpoints));
        Tighten(wallMilliseconds, ParticleLimits.MaximumAnchorWallMilliseconds, nameof(wallMilliseconds));
        _frameBytes = frameBytes; _anchorTimes = anchorTimes; _textCalls = textCalls;
        _futureEndpoints = futureEndpoints; _wallMilliseconds = wallMilliseconds;
        _time = timeProvider ?? TimeProvider.System;
        if (_time.TimestampFrequency <= 0) throw new ArgumentException("单调时钟频率必须为正。", nameof(timeProvider));
    }

    public ParticleQueryBudget CreateParticleQuery() => new(byteLimit: _frameBytes);
    public V2AnchorQueryBudget CreateAnchorQuery(CancellationToken token)
        => new(token, _anchorTimes, _textCalls, _futureEndpoints, _wallMilliseconds, _time);

    private static void Tighten(long value, long maximum, string name)
    {
        if (value <= 0 || value > maximum) throw new ArgumentOutOfRangeException(name, "内部预算只能收紧生产固定上限。");
    }
}
