using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class ProjectMediaHostSourceClosureTests
{
    [Theory]
    [InlineData("moved-path")] [InlineData("moved-region")] [InlineData("added")]
    [InlineData("removed")] [InlineData("parameters")] [InlineData("style")]
    [InlineData("lyrics")] [InlineData("presentation")] [InlineData("presets")]
    public async Task trusted入口保留旧指纹篡改源闭包仍在几何工厂前拒绝(string change)
    {
        var (basis, prepared, binding) = Fixed();
        var material = basis.LyricStyle.UnsungFill.Material;
        var snapshot = change switch
        {
            "moved-path" => basis with { LyricStyle = basis.LyricStyle with { UnsungFill = FillSpec.DefaultUnsung, SungFill = new() { Material = material } } },
            "moved-region" => basis with { LyricStyle = basis.LyricStyle with { UnsungFill = FillSpec.DefaultUnsung, Stroke = new() { Material = material, WidthPx = 2 } } },
            "added" => basis with { LyricStyle = basis.LyricStyle with { SungFill = new() { Material = material } } },
            "removed" => basis with { LyricStyle = basis.LyricStyle with { UnsungFill = FillSpec.DefaultUnsung } },
            "parameters" => basis with { LyricStyle = basis.LyricStyle with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(
                ((MaterialSpec.PackageMaterial)material).PackageId, ((MaterialSpec.PackageMaterial)material).Revision, "ink", EffectParameterOverrides.Parse("{\"peak\":3}")) } } },
            "style" => basis with { LyricStyle = basis.LyricStyle with { Opacity = .6f } },
            "lyrics" => basis with { Lyrics = basis.Lyrics with { Branches = basis.Lyrics.Branches.SetItem(0,
                basis.Lyrics.Branches[0] with { StyleOverride = new() { Opacity = .6f } }) } },
            "presentation" => basis with { LyricPresentation = basis.LyricPresentation with { Nodes = [.. basis.LyricPresentation.Nodes.Select(node =>
                node.Id == "center" ? node with { ParentId = basis.LyricPresentation.FindProgram("translation")!.RootContainerId } : node)] } },
            "presets" => basis with { PresetApplications = new(basis.PresetApplications.Items.Select(application => application with { Enabled = false })) },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        // Deliberately simulate misuse of the public immutable snapshot entrance.
        // Normal FromProject is still trusted to produce its original source identity.
        Assert.Equal(basis.EffectInputsFingerprint, snapshot.EffectInputsFingerprint);
        Assert.Equal(ProjectRuntimeEffectInputs.Fingerprint(basis), ProjectRuntimeEffectInputs.Fingerprint(snapshot));
        bool geometry = false;
        var factory = new PureFrameSourceFactory([], ProjectRuntimeEffectInputs.Fingerprint(snapshot)!,
            () => { geometry = true; throw new InvalidOperationException("geometry must not run"); },
            sourceMaterials: prepared, mediaBinding: binding);
        var failure = await Assert.ThrowsAsync<PreparedFrameException>(async () => await factory.CreateAsync(snapshot));
        Assert.Equal("SourceMaterialClosureMismatch", failure.Code);
        Assert.False(geometry);
    }

    [Fact]
    public void trusted入口源闭包独立等值副本可重用且不以reference为来源证明()
    {
        var (basis, prepared, binding) = Fixed();
        var clone = basis with
        {
            LyricStyle = basis.LyricStyle with { Typography = basis.LyricStyle.Typography with { FallbackFamilyNames = [.. basis.LyricStyle.FallbackFamilyNames] },
                UnsungFill = basis.LyricStyle.UnsungFill with { }, SungFill = basis.LyricStyle.SungFill with { } },
            Lyrics = basis.Lyrics with { Branches = [.. basis.Lyrics.Branches.Select(branch => branch with
                { Lines = [.. branch.Lines.Select(line => line with { Tokens = [.. line.Tokens.Select(token => token with { })] })] })] },
            LyricPresentation = basis.LyricPresentation with { Nodes = [.. basis.LyricPresentation.Nodes.Select(node => node with { })],
                Programs = [.. basis.LyricPresentation.Programs.Select(program => program with { Clips = [.. program.Clips], LeadIns = [.. program.LeadIns] })],
                LineOverrides = [.. basis.LyricPresentation.LineOverrides] },
            PresetApplications = new(basis.PresetApplications.Items.Select(application => application with { })),
        };
        Assert.NotSame(basis.Lyrics, clone.Lyrics);
        Assert.NotSame(basis.LyricPresentation, clone.LyricPresentation);
        Assert.NotSame(basis.PresetApplications, clone.PresetApplications);
        ProjectMediaFeatureAdmission.Validate(clone, [], prepared, binding, default);
        Assert.Equal(basis.EffectInputsFingerprint, prepared.Fingerprint);
    }

    [Fact]
    public void 旧无媒体ValidateSnapshot仍只按原源身份验证()
    {
        var (basis, prepared, _) = Fixed();
        var changed = basis with { MediaFeatureInput = null, LyricStyle = basis.LyricStyle with { Opacity = .6f } };
        prepared.ValidateSnapshot(changed);
        ProjectMediaFeatureAdmission.Validate(changed, [], prepared, null, default);
        Assert.Equal(changed.EffectInputsFingerprint, ProjectRuntimeEffectInputs.Fingerprint(changed));
    }

    private static (ProjectSnapshot Snapshot, PreparedSourceMaterials Prepared, ProjectMediaFeatureBinding Binding) Fixed()
    {
        var contents = NoQueryMaterial();
        // Formal reader proves this is a supported material; no pending media
        // capability, author DLL, native shader compilation or image decode.
        var package = EffectPackageDefinition.Read(contents);
        var material = new MaterialSpec.PackageMaterial(contents.PackageId, contents.Revision, "ink", EffectParameterOverrides.Parse("{\"peak\":2}"));
        var style = LyricStyle.Default with { UnsungFill = new() { Material = material }, SungFill = FillSpec.DefaultSung, Stroke = null };
        var preset = StyleTemplate.Create("source-plain", "Plain source", StyleTemplateKind.Line, LyricStyle.Default, [LyricStyleProperty.Opacity]);
        var project = LeadInFixture.Create(true, true) with { Effects = new([contents], []), LyricStyle = style,
            PresetApplications = new([new("plain-application", new(ProjectEffectBindingKind.Project), preset)]) };
        var snapshot = ProjectSnapshot.FromProject(project);
        var plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
        Assert.Empty(plan.Owners); Assert.Empty(plan.Dependencies);
        var source = ProjectMediaProjectionFixture.Source();
        var context = MediaInputContext.Create(source.Profile, MediaMapping.Create(640, 360, BackgroundFit.Contain, BackgroundPlaybackMode.Loop,
            new(0, 0, 0, 0), MediaOutputMode.Normal), plan.QueryCatalogDigest, plan.FixedDependencyDigest);
        var input = MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.NoBackgroundContribution,
            ProjectMediaProjectionFixture.Hash('c'));
        snapshot = snapshot with { MediaFeatureInput = input };
        var resources = new ResourceCatalog(contents.PackageId, contents.Revision, ["ink"], [], []);
        var value = (MaterialValue.PackageMaterial)MaterialValue.PackageExport(resources.GetMaterial("ink"),
            new([KeyValuePair.Create<string, ParameterValue>("peak", new ParameterValue.Number(2))]));
        var references = SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
        Assert.Single(references); SourceMaterialCatalog.Validate(references[0], package);
        var prepared = new PreparedSourceMaterials(snapshot, references.Select(reference => KeyValuePair.Create(reference, value)));
        return (snapshot, prepared, new(new ProjectMediaProjectionFixture.Sampler(input, plan)));
    }

    private static EffectPackageContents NoQueryMaterial()
    {
        var files = EffectPackageFixture.Files();
        files["graphs/source.graph.json"] = Encoding.UTF8.GetBytes("""
            {"graphVersion":1,"id":"source","inputs":[
              {"id":"original","source":"TargetColor","type":"color","availability":"required","backgroundRequirement":"none","space":"canvas",
               "capture":{"includeRuby":true,"includeStroke":true,"includeShadow":true,"includeDescendantGroups":true,"includeOwnedOverlay":false,"region":"fillAndStroke"}}],
             "parameters":[],"nodes":[],"output":"input:original","anchorMode":"ColorOnly",
             "bounds":{"space":"canvas","source":"targetRegion","maxExpandPx":0},"composite":{"mode":"sourceOver"}}
            """);
        var manifest = EffectPackageFixture.Manifest(files);
        foreach (var file in manifest["files"]!.AsArray())
            if (file!["path"]!.GetValue<string>() == "graphs/source.graph.json") file["role"] = "graph";
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("image.graph");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        manifest["exports"] = new JsonArray(new JsonObject { ["id"] = "ink", ["kind"] = "material", ["name"] = "Ink", ["graph"] = "graphs/source.graph.json",
            ["parameters"] = new JsonArray("peak"), ["regions"] = new JsonArray("fill", "stroke"), ["coverageMode"] = "masked",
            ["fallback"] = new JsonObject { ["kind"] = "solid", ["color"] = "#3380CCA0" },
            ["outputs"] = new JsonArray(new JsonObject { ["id"] = "color", ["kind"] = "ImageGraph", ["target"] = "region", ["requires"] = new JsonArray(),
                ["dependsOn"] = new JsonArray(), ["bypass"] = new JsonObject { ["kind"] = "materialFallback", ["mapping"] = "identity" } }) });
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }
}
