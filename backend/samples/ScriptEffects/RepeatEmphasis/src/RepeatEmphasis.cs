using System;
using System.Collections.Generic;
using MakoKaraGenerator.Scripting;

public sealed class RepeatEmphasis : EffectBase
{
    private readonly Dictionary<(string Scope, string Text), int> counts = new();
    private readonly Dictionary<OccurrenceId, int> ordinal = new();
    private bool projectScope;
    private double scaleStep, maxScale, darkenStep, minBrightness;

    public override void Initialize(EffectInitializationContext context)
    {
        projectScope = context.Parameters.GetEnum("countScope") == "project";
        scaleStep = context.Parameters.GetNumber("scaleStep");
        maxScale = context.Parameters.GetNumber("maxScale");
        darkenStep = context.Parameters.GetNumber("darkenStep");
        minBrightness = context.Parameters.GetNumber("minBrightness");
    }

    public override void OnEvent(TimelineEvent evt, EventContext context)
    {
        if (evt is not TokenStarted started || ordinal.ContainsKey(started.OccurrenceId))
            return;
        SourceToken token = context.Project.Content.GetToken(started.TokenId);
        var key = (projectScope ? "project" : token.BranchId.Value, token.Text);
        counts.TryGetValue(key, out int previous);
        int current = checked(previous + 1);
        counts[key] = current;
        ordinal[started.OccurrenceId] = current;
    }

    public override void Evaluate(FrameContext frame, FrameWriter output)
    {
        foreach (FrameToken token in frame.Presentations.TokensInBinding)
        {
            if (token.OccurrenceId is not { } id || !ordinal.TryGetValue(id, out int n))
                continue;
            double scale = Math.Min(maxScale, 1 + (n - 1) * scaleStep);
            double brightness = Math.Max(minBrightness, 1 - (n - 1) * darkenStep);
            ColorRgba baseColor = frame.Baseline.Get(token.WithRuby).RequireSungSolidColor();
            output.Style.SetSungMaterial(token.WithRuby,
                MaterialValue.Solid(baseColor.MultiplyRgb(brightness)));
            output.Transforms.ScaleTokenFamily(token.WithRuby, scale, Pivot.BaseAdvanceCenter);
        }
    }
}
