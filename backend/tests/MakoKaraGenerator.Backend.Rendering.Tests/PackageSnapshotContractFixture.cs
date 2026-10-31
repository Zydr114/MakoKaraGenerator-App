using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

internal static class PackageSnapshotContractFixture
{
    internal static readonly string AudioHash = new('a', 64);

    internal static ProjectSnapshot Create(string? analysisState = null)
    {
        ProjectSnapshot basis = StaticSnapshotFixture.Create();
        EffectPackageContents mixed = MixedEffectPackageFixture.Contents(manifest =>
            manifest["exports"]![0]!["outputs"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "overlay", ["kind"] = "Overlay", ["target"] = "tokenFamily",
                ["requires"] = new System.Text.Json.Nodes.JsonArray(), ["dependsOn"] = new System.Text.Json.Nodes.JsonArray(),
            }));
        EffectPackageContents scale = EffectPackageFixture.Contents();
        var effects = new ProjectEffects([mixed, scale],
        [
            new()
            {
                Id = "first-instance", Name = "Unicode实例🎵", PackageId = mixed.PackageId, Revision = mixed.Revision,
                ExportId = "controller", Binding = new(ProjectEffectBindingKind.Tokens, ids: ["first-b", "first-a"]),
                Subscription = new("branch", "translation"), Seed = ulong.MaxValue,
                Parameters = EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567}"), OverlayLayer = EffectOverlayLayer.AboveText,
            },
            new()
            {
                Id = "second-instance", Name = "套用来源", PackageId = scale.PackageId, Revision = scale.Revision,
                ExportId = "scale", Binding = new(ProjectEffectBindingKind.Tokens, ids: ["first-a"]),
                Subscription = new("project"), Seed = 9_007_199_254_740_993,
                Parameters = EffectParameterOverrides.Parse("{\"peak\":2.25}"),
                TemplateOrigin = new("fixed-template", "recipe", StyleTemplateKind.Token, "first-a"),
            },
            new()
            {
                Id = "disabled-instance", Name = "独立禁用", Enabled = false, PackageId = mixed.PackageId, Revision = mixed.Revision,
                ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project), Seed = 0,
                OverlayLayer = EffectOverlayLayer.BehindText,
            },
        ], new() { SurfaceMemoryBytes = 123_456_789, LiveParticles = 1234, TrailSamples = 2345, EmissionRecords = 3456, DrawCommands = 4567 });
        ProjectSnapshot source = basis with { Effects = effects };
        if (analysisState is not null)
            source = source with
            {
                AudioPath = "/never-read/音频.wav", AudioMedia = new(AudioHash, 4096, "音频.wav"),
                AudioAnalysis = Analysis(analysisState),
            };
        return Fingerprint(source);
    }

    internal static ProjectAudioAnalysis Analysis(string state) => state switch
    {
        "failed" => ProjectAudioAnalysis.Failed("ActualDecodeFailure", "真实失败\n保留理由🎵"),
        "noStream" => ProjectAudioAnalysis.NoStream("ActualNoStream", "真实无流\r\n保留理由"),
        _ => ProjectAudioAnalysis.Available(new(AudioHash, new string('b', 64), 961,
            [new(0, .12345678901234567, .4, null, 0), new(20_000, .3, .2, .75, .8)])),
    };

    internal static ProjectSnapshot Fingerprint(ProjectSnapshot snapshot) => snapshot with
    { EffectInputsFingerprint = ProjectEffectInputs.Fingerprint(ToProject(snapshot)) };

    internal static Project ToProject(ProjectSnapshot snapshot) => new()
    {
        Name = snapshot.Name, SchemaVersion = snapshot.SchemaVersion, Composition = snapshot.Composition,
        Lyrics = snapshot.Lyrics, LyricPresentation = snapshot.LyricPresentation, LyricStyle = snapshot.LyricStyle,
        LyricStylePresetId = snapshot.LyricStylePresetId, EnterAnimation = snapshot.EnterAnimation,
        Background = snapshot.Background, AudioPath = snapshot.AudioPath, AudioMedia = snapshot.AudioMedia,
        AudioAnalysis = snapshot.AudioAnalysis, Effects = snapshot.Effects, PresetApplications = snapshot.PresetApplications,
    };
}
