using MakoKaraGenerator.Scripting;

public sealed class WordEmphasis : EffectBase
{
    private double peak;
    private long returnUs;

    public override void Initialize(EffectInitializationContext context)
    {
        peak = context.Parameters.GetNumber("peakScale");
        returnUs = context.Parameters.GetDuration("returnUs");
    }

    public override void Evaluate(FrameContext frame, FrameWriter output)
    {
        foreach (FrameToken token in frame.Presentations.TokensInBinding)
        {
            PerformanceSample p = token.Performance;
            if (p.ProgressSource != ProgressSource.Token || p.TimeRange is not { } range)
                continue;
            double factor = 1;
            if (p.IsCurrent && p.Progress is { } progress)
                factor = 1 + (peak - 1) * Curves.EaseOut(progress);
            else if (p.IsSung && returnUs > 0 && frame.TimeUs < checked(range.EndUs + returnUs))
                factor = 1 + (peak - 1) * (1 - (double)(frame.TimeUs - range.EndUs) / returnUs);
            output.Transforms.ScaleTokenFamily(token.WithRuby, factor, Pivot.BaseAdvanceCenter);
        }
    }
}
