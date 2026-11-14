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
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    if (FAIL && frame.TimeUs >= 200000)
                        throw new EffectOutputException("PersistedAudioFailure", "real author failure after 200ms");
                    var sample = frame.Audio.GetAt(frame.TimeUs);
                    if (sample.Values is not { } values) return;
                    double green = Math.Round(values.BassLevel * 255) / 255;
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.SetFill(token.WithRuby, TextState.Both, MaterialValue.Solid(new ColorRgba(.1, green, .1, 1)));
                }
            }
            """.Replace("FAIL", failAfter200ms ? "frame.TimeUs >= 0" : "frame.TimeUs < 0", StringComparison.Ordinal));
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["requires"] = new JsonArray("style.fill");
        var output = manifest["exports"]![0]!["outputs"]![0]!;
        output["kind"] = "StyleModulation"; output["requires"] = new JsonArray("style.fill");
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }
}
