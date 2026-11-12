using System;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record ClusterExitData(double DriftPx, double StaggerFraction) : PreparedEffect;

public sealed class ClusterExit : IPureKaraokeEffect<ClusterExitData>
{
    public ClusterExitData Declare(DeclareContext source) => new ClusterExitData(
        source.Parameters.GetNumber("driftPx"), source.Parameters.GetNumber("staggerFraction"));

    public void Evaluate(in TextFrame frame, ClusterExitData settings, TextOutput output)
    {
        var clusters = frame.Clusters;
        foreach (var load in frame.LineLoads)
        {
            if (load.Phase != TextLinePhase.ExitReplica || load.ExitProgress <= 0) continue;
            double progress = load.ExitProgress;
            double smooth = progress * progress * (3 - 2 * progress);
            bool shared = false;
            foreach (var cluster in clusters)
                if (cluster.Target.BindingId == load.Line.BindingId && !cluster.Geometry.CanTransformIndependently)
                    shared = true;

            // A shared ligature belongs to the complete line. Move that line, including Ruby, as one shape.
            if (shared)
            {
                output.AppendTransform(load.Target, Affine2.Translation(0, -(frame.CanvasSize.Y + 128) * smooth));
                continue;
            }

            foreach (var token in frame.Geometry)
                if (token.WithRuby.BindingId == load.Line.BindingId)
                    output.MultiplyOpacity(token.WithRuby, 1 - smooth);

            foreach (var cluster in clusters)
            {
                if (cluster.Target.BindingId != load.Line.BindingId) continue;
                double delay = settings.StaggerFraction * (cluster.Target.UnitIndex % 4) / 3;
                double local = Math.Clamp((progress - delay) / (1 - delay), 0, 1);
                double eased = local * local * (3 - 2 * local);
                if (eased == 0) continue;
                output.AppendTransform(cluster.Target, Affine2.Translation(settings.DriftPx * .35 * eased, -settings.DriftPx * eased));
            }
        }
    }
}
