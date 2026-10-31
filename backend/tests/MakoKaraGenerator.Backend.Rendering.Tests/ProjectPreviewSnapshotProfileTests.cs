using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class ProjectPreviewSnapshotProfileTests
{
    [Fact]
    public void 三种静态来源仍使用原互斥合同()
    {
        var plain = StaticSnapshotFixture.Create();
        var presets = PresetSnapshotFixture.Create();
        Assert.Equal(StaticProjectSnapshotCodec.Profile, ProjectPreviewSnapshotProfile.Select(plain));
        Assert.Equal(StaticProjectSnapshotCodec.AnalysisProfile, ProjectPreviewSnapshotProfile.Select(plain with
        { AudioAnalysis = PackageSnapshotContractFixture.Analysis("available") }));
        Assert.Equal(StaticProjectSnapshotCodec.PresetsProfile, ProjectPreviewSnapshotProfile.Select(presets));
        Assert.Equal(StaticProjectSnapshotCodec.Encode(presets, StaticProjectSnapshotCodec.PresetsProfile),
            ProjectPreviewSnapshotProfile.Encode(presets));
        Assert.False(BackendSnapshotProfiles.IsSupported(PackageProjectSnapshotCodec.Profile));
    }

    [Theory]
    [InlineData("complete")] [InlineData("packages")] [InlineData("disabled")] [InlineData("budget")]
    [InlineData("fingerprint")] [InlineData("material")]
    public void 包源每种独立入口都选择包能力且不污染静态选择器(string input)
    {
        var source = PackageSnapshotContractFixture.Create();
        var plain = StaticSnapshotFixture.Create();
        source = input switch
        {
            "packages" => plain with { Effects = new(source.Effects.Packages) },
            "disabled" => plain with { Effects = new(source.Effects.Packages, source.Effects.Instances.Select(i => i with { Enabled = false })) },
            "budget" => plain with { Effects = new(budgets: new() { LiveParticles = 1 }) },
            "fingerprint" => plain with { EffectInputsFingerprint = "untrusted" },
            "material" => plain with { LyricStyle = plain.LyricStyle with
                { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial("missing", "sha256:" + new string('a', 64), "material") } } },
            _ => source,
        };
        Assert.Equal(PackageProjectSnapshotCodec.Profile, ProjectPreviewSnapshotProfile.Select(source));
        Assert.Equal(StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.SelectProfile(source));
        Assert.Equal("SnapshotEffectsUnsupported", Assert.Throws<SnapshotContractException>(() =>
            StaticProjectSnapshotCodec.Encode(source)).Code);
        if (input == "complete") Assert.Equal(PackageProjectSnapshotCodec.Encode(source), ProjectPreviewSnapshotProfile.Encode(source));
    }

    [Fact]
    public void 包与预设组合在预览与统一来源使用同一混合合同且旧包codec拒绝混用()
    {
        var packaged = PackageSnapshotContractFixture.Create();
        var source = PackageSnapshotContractFixture.Fingerprint(PresetSnapshotFixture.Create() with { Effects = packaged.Effects });
        Assert.Equal(PackagePresetProjectSnapshotCodec.Profile, ProjectPreviewSnapshotProfile.Select(source));
        Assert.Equal(PackagePresetProjectSnapshotCodec.Profile, ProjectSnapshotSourceProfile.Select(source));
        byte[] encoded = ProjectSnapshotSourceProfile.Encode(source);
        Assert.Equal(encoded, ProjectPreviewSnapshotProfile.Encode(source));
        Assert.Equal(PackagePresetProjectSnapshotCodec.Profile, System.Text.Json.JsonDocument.Parse(encoded).RootElement.GetProperty("profile").GetString());
        Assert.Equal("SnapshotVersionMismatch", Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Decode(encoded)).Code);
        ProjectSnapshot copy = PackagePresetProjectSnapshotCodec.Decode(encoded);
        Assert.Equal(source.Effects, copy.Effects);
        Assert.Equal(source.PresetApplications, copy.PresetApplications);
        Assert.Equal(encoded, PackagePresetProjectSnapshotCodec.Encode(copy));
        Assert.Equal("SnapshotEffectsUnsupported", Assert.Throws<SnapshotContractException>(() => PackageProjectSnapshotCodec.Encode(source)).Code);
    }
}
