using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.TestSupport;

internal static class PersistedAudioEffectFixture
{
    internal static EffectPackageContents Package(bool required = true, bool failAfter200ms = false)
    {
        var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("""
            using System;
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase
            {
                private int ticks;
                public override void OnEvent(TimelineEvent evt, EventContext context)
                {
                    if (evt is AudioFeatureTick tick)
                    {
                        if (tick.AnalysisRevision != context.Audio.Revision || tick.Point.Values != context.Audio.GetAt(tick.TimeUs).Values)
                            throw new Exception("fixed analysis must preserve real hop identity");
                        ticks++;
                    }
                }
                public override void Evaluate(FrameContext frame, FrameWriter writer)
                {
                    if (FAIL && frame.TimeUs >= 200000)
                    { frame.Diagnostics.Report(DiagnosticSeverity.Error, "PersistedAudioFailure", "real author failure after 200ms"); return; }
                    if (frame.History.Records.Count(record => record.Event is AudioFeatureTick) != ticks)
                        throw new Exception("replayed history and callback counts must match");
                    var sample = frame.Audio.GetAt(frame.TimeUs);
                    if (sample.Values is not { } values)
                    {
                        frame.Diagnostics.Report(DiagnosticSeverity.Warning, "ActualAudioAvailability", sample.Availability.ToString()); return;
                    }
                    double green = Math.Round(values.BassLevel * 255) / 255;
                    foreach (var token in frame.Presentations.TokensInBinding)
                    {
                        writer.Style.SetUnsungMaterial(token.WithRuby, MaterialValue.Solid(new ColorRgba(.1, green, .1, 1)));
                        writer.Style.SetSungMaterial(token.WithRuby, MaterialValue.Solid(new ColorRgba(.1, green, .1, 1)));
                    }
                    frame.Diagnostics.Report(DiagnosticSeverity.Info, "RealHopCount", ticks.ToString());
                }
            }
            """.Replace("FAIL", failAfter200ms ? "frame.TimeUs >= 0" : "frame.TimeUs < 0", StringComparison.Ordinal));
        var manifest = EffectPackageFixture.Manifest(files); var output = manifest["exports"]![0]!["outputs"]![0]!;
        output["kind"] = "StyleModulation"; output["requires"] = required ? new JsonArray("AudioFeatures") : new JsonArray();
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }
}
