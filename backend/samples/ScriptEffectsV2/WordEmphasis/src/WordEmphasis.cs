using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record WordData(double Peak, long ReturnUs) : PreparedEffect;

public sealed class WordEmphasis : IPureKaraokeEffect<WordData>
{
    public WordData Declare(DeclareContext source) => new WordData(
        source.Parameters.GetNumber("peakScale"), source.Parameters.GetDuration("returnUs"));

    public void Evaluate(in TextFrame frame, WordData settings, TextOutput output)
    {
        foreach (var token in frame.Presentations.TokensInBinding)
        {
            var progress = token.Performance;
            if (progress.ProgressSource != ProgressSource.Token || progress.TimeRange is not { } range) continue;
            double factor = 1;
            if (progress.IsCurrent && progress.Progress is { } amount)
                factor += (settings.Peak - 1) * Curves.EaseOut(amount);
            else if (progress.IsSung && settings.ReturnUs > 0 && frame.TimeUs - range.EndUs < settings.ReturnUs)
                factor += (settings.Peak - 1) * (1 - (double)(frame.TimeUs - range.EndUs) / settings.ReturnUs);
            if (factor == 1) continue;
            foreach (var geometry in frame.Geometry)
                if (geometry.WithRuby == token.WithRuby)
                    output.AppendTransform(token.WithRuby, Affine2.ScaleAt(factor, geometry.BaseGeometry.AdvanceBounds.Center));
        }
    }
}
