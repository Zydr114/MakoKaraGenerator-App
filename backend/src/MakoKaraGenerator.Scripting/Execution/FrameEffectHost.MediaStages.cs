using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    // This is the actual visual call path. Factories retain admission; this helper
    // centralizes the immediate stage scope across constructor, author and publish failures.
    private static void EvaluatePureVisualStage(ActiveEffect active, IPureVisualEffect visual, long timeUs, OutputMode outputMode,
        ImmutableArray<ParticleView> alive, ParticleBirthWindow births,
        Func<ParticleIdentity, ImmutableArray<ParticleTrailSample>> readTrail, Action<long> reserveBytes,
        Func<FinalSceneGeometry> finalGeometry, Action<OverlayBatch> accept, VisualOutputBudget budget, CancellationToken token)
    {
        var pure = active.Pure!;
        MediaFeatures media = pure.MediaProjection?.CreateStage(token) ?? new(pure.Source.Media, pure.Source.CanvasSize, token: token);
        try
        {
            ParticleFrame? particles = null; VisualOutput? output = null;
            try
            {
                particles = new(active.Definition.Id, timeUs, alive, births, readTrail, token);
                output = new(active.Definition.Id, active.Definition.PackageDefinition!.Contents.Requires,
                    pure.Prepared.VisualOutputs, active.Definition.Resources, budget, reserveBytes, token);
                pure.Source.Indexes.SetQueryCancellation(token);
                VisualFrame frame = new(pure.Source, timeUs, active.Definition.Binding, outputMode, finalGeometry(), particles, media);
                using (MeasureAuthor(active, "EvaluateVisuals")) visual.EvaluateVisuals(in frame, pure.Prepared, output);
                token.ThrowIfCancellationRequested();
                foreach (var original in output.Complete()) accept(original);
            }
            finally
            {
                try { output?.Invalidate(); }
                finally { particles?.Invalidate(); }
            }
        }
        finally { media.Invalidate(); }
    }
}
