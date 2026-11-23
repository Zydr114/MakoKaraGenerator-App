using System;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record RepeatData(bool ProjectScope, double ScaleStep, double MaximumScale,
    double DarkenStep, double MinimumBrightness) : PreparedEffect;

public sealed class RepeatEmphasis : IPureKaraokeEffect<RepeatData>
{
    public RepeatData Declare(DeclareContext source) => new RepeatData(
        source.Parameters.GetEnum("countScope") == "project", source.Parameters.GetNumber("scaleStep"),
        source.Parameters.GetNumber("maxScale"), source.Parameters.GetNumber("darkenStep"),
        source.Parameters.GetNumber("minBrightness"));

    public void Evaluate(in TextFrame frame, RepeatData settings, TextOutput output)
    {
        foreach (var token in frame.Presentations.TokensInBinding)
        {
            var source = frame.Source.Content.GetLine(token.LineId);
            var current = frame.Timeline.Last(new TimelineSelector(TimelineEventKind.LinePerformanceStarted,
                new TimelineScope.Line(token.LineId)), frame.TimeUs);
            if (current is null) continue;
            TimelineScope scope = settings.ProjectScope ? new TimelineScope.Project() : new TimelineScope.Branch(token.BranchId);
            int ordinal = frame.Timeline.Ordinal(new TimelineSelector(TimelineEventKind.LinePerformanceStarted, scope, source.Text), current.EventId) ?? 1;
            if (ordinal <= 1) continue;
            double scale = Math.Min(settings.MaximumScale, 1 + (ordinal - 1) * settings.ScaleStep);
            double brightness = Math.Max(settings.MinimumBrightness, 1 - (ordinal - 1) * settings.DarkenStep);
            foreach (var geometry in frame.Geometry)
                if (geometry.WithRuby == token.WithRuby)
                    output.AppendTransform(token.WithRuby, Affine2.ScaleAt(scale, geometry.BaseGeometry.AdvanceBounds.Center));
            var color = frame.Baseline.Get(token.WithRuby).RequireSungSolidColor();
            output.SetFill(token.WithRuby, TextState.Sung, MaterialValue.Solid(
                new ColorRgba(color.Red * brightness, color.Green * brightness, color.Blue * brightness, color.Alpha)));
        }
    }
}
