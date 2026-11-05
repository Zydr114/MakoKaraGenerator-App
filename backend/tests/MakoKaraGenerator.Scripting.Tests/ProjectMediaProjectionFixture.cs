using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

internal static class ProjectMediaProjectionFixture
{
    internal static MediaDigest Hash(char value) => new(new string(value, 64));

    internal static EffectPackageContents Package(Action<JsonObject>? change = null)
    {
        var files = EffectPackageFixture.Files(); files["assets/image.png"] = [1, 2, 3];
        files["graphs/background.graph.json"] = Encoding.UTF8.GetBytes("""
            {"graphVersion":1,"id":"background","inputs":[
              {"id":"original","source":"TargetColor","type":"color","availability":"required","backgroundRequirement":"none","space":"canvas",
               "capture":{"includeRuby":true,"includeStroke":true,"includeShadow":true,"includeDescendantGroups":true,"includeOwnedOverlay":false,"region":"fillAndStroke"}},
              {"id":"background","source":"BackgroundMedia","type":"color","availability":"required","backgroundRequirement":"media","space":"canvas"}],
             "parameters":[],"nodes":[],"output":"input:background","bypassInput":"input:original","anchorMode":"ColorOnly",
             "bounds":{"space":"canvas","source":"targetRegion","maxExpandPx":0},"composite":{"mode":"sourceOver"}}
            """);
        JsonObject manifest = EffectPackageFixture.NineParametersManifest(files);
        // A parameter-backed media window requires the complete declared range
        // to stay inside the Core media-query limit, not just its default.
        manifest["parameters"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "duration")!["max"] = 2_000_000L;
        foreach (JsonNode? file in manifest["files"]!.AsArray())
            if (file!["path"]!.GetValue<string>().EndsWith(".graph.json", StringComparison.Ordinal)) file["role"] = "graph";
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("media.features", "token.affine", "image.graph");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        manifest["mediaQueries"] = new JsonArray(
            new JsonObject { ["id"] = "source", ["space"] = "sourceNormalized", ["window"] = new JsonObject { ["kind"] = "parameter", ["parameter"] = "duration" } },
            new JsonObject { ["id"] = "canvas", ["space"] = "canvasPixels", ["window"] = new JsonObject { ["kind"] = "literal", ["durationUs"] = 0 } });
        JsonObject controller = manifest["exports"]![0]!.AsObject();
        controller["mediaQueries"] = References("authorBranch", "scale");
        JsonObject plain = controller.DeepClone().AsObject(); plain["id"] = "plain"; plain["mediaQueries"] = new JsonArray();
        JsonObject material = new()
        {
            ["id"] = "ink", ["kind"] = "material", ["name"] = "Ink", ["graph"] = "graphs/background.graph.json",
            ["parameters"] = controller["parameters"]!.DeepClone(), ["regions"] = new JsonArray("fill", "stroke"), ["coverageMode"] = "masked",
            ["fallback"] = new JsonObject { ["kind"] = "solid", ["color"] = "#3380CCA0" },
            ["outputs"] = new JsonArray(new JsonObject { ["id"] = "color", ["kind"] = "ImageGraph", ["target"] = "region",
                ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray(), ["bypass"] = new JsonObject { ["kind"] = "materialFallback", ["mapping"] = "identity" } }),
            ["mediaQueries"] = References("outputBypass", "color"),
        };
        manifest["exports"]!.AsArray().Add(plain); manifest["exports"]!.AsArray().Add(material);
        change?.Invoke(manifest);
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }

    private static JsonArray References(string kind, string output)
    {
        JsonArray result = [];
        foreach (string query in new[] { "source", "canvas" })
        {
            JsonObject State() => new() { ["kind"] = kind, ["outputs"] = new JsonArray(output) };
            result.Add(new JsonObject { ["query"] = query, ["fallback"] = new JsonObject { ["pending"] = State(), ["unavailable"] = State(), ["noMedia"] = State() } });
        }
        return result;
    }

    internal static Project Project(EffectPackageContents package, bool sourceOnly = false, string overrides = "{}")
    {
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink", EffectParameterOverrides.Parse(overrides));
        return MakoKaraGenerator.Core.Projects.Project.Create("Projection owner fixture") with
        {
            Composition = new() { CanvasWidth = 320, CanvasHeight = 240 },
            Effects = new([package], sourceOnly ? [] : new[]
            {
                new ProjectEffectInstance { Id = "owner-0", Name = "First", PackageId = package.PackageId, Revision = package.Revision, ExportId = "scale", Parameters = EffectParameterOverrides.Parse(overrides) },
                new ProjectEffectInstance { Id = "owner-1", Name = "Second", PackageId = package.PackageId, Revision = package.Revision, ExportId = "scale", Parameters = EffectParameterOverrides.Parse(overrides) },
                new ProjectEffectInstance { Id = "owner-plain", Name = "No query", PackageId = package.PackageId, Revision = package.Revision, ExportId = "plain", Parameters = EffectParameterOverrides.Parse(overrides) },
            }),
            LyricStyle = sourceOnly ? LyricStyle.Default with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material }, Stroke = new() { Material = material } } : LyricStyle.Default,
        };
    }

    internal static ResourceCatalog Resources(EffectPackageContents package) => new(package.PackageId, package.Revision, ["ink"], [], ["assets/image.png"]);

    // A formally read descriptor only: no compiler, image decoder or author callback is run; no pending capability is opened.
    internal static CompiledFrameEffect NoQueryCompiledDescriptor(string overrides = "{}")
    {
        var files = EffectPackageFixture.Files(); files["assets/image.png"] = [1, 2, 3];
        var manifest = EffectPackageFixture.NineParametersManifest(files);
        var contents = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        EffectPackageDefinition package = EffectPackageDefinition.Read(contents);
        ResourceCatalog resources = new(contents.PackageId, contents.Revision, [], [], ["assets/image.png"]);
        ParameterValues parameters = new(package.ResolveParameters("scale", EffectParameterOverrides.Parse(overrides).Encode())
            .Select(pair => KeyValuePair.Create(pair.Key, Value(pair.Value, resources))));
        return new(new("compiled-no-query"), null, null, parameters, new EffectBinding.Project(), EffectCapabilities.None)
        { PackageDefinition = package, PackageDeclaration = package.GetExport("scale"), Resources = resources };
    }

    // Only fixture construction mirrors the existing public SDK projection.
    // Expected authorities and full parameters always come from the real Core reader/resolver.
    internal static ParameterValues Parameters(EffectPackageMediaQueryCatalog catalog, string export = "scale", string overrides = "{}")
    {
        ResourceCatalog resources = Resources(catalog.Contents);
        return new(catalog.ResolveParameters(export, EffectParameterOverrides.Parse(overrides).Encode()).Select(pair =>
            KeyValuePair.Create(pair.Key, Value(pair.Value, resources))));
    }

    private static ParameterValue Value(EffectParameterValue value, ResourceCatalog resources) => value switch
    {
        EffectParameterValue.Number number => new ParameterValue.Number(number.Value),
        EffectParameterValue.Whole whole => new ParameterValue.Whole(whole.Value),
        EffectParameterValue.Toggle toggle => new ParameterValue.Toggle(toggle.Value),
        EffectParameterValue.Duration duration => new ParameterValue.Duration(duration.Value),
        EffectParameterValue.Choice choice => new ParameterValue.Choice(choice.Value),
        EffectParameterValue.Text text => new ParameterValue.Text(text.Value),
        EffectParameterValue.Color color => new ParameterValue.Color(new(color.R, color.G, color.B, color.A)),
        EffectParameterValue.Asset asset => new ParameterValue.Asset(resources.GetImage(asset.Path)),
        EffectParameterValue.Curve curve => new ParameterValue.Curve(curve.Value.Kind switch
        {
            "easing" => new CurveValue.Easing(curve.Value.Easing switch
                { "linear" => EasingKind.Linear, "easeOut" => EasingKind.EaseOut, "easeInOut" => EasingKind.EaseInOut, _ => throw new InvalidOperationException() }),
            "cubicBezier" => new CurveValue.CubicBezier(new(curve.Value.Points[0].X, curve.Value.Points[0].Y), new(curve.Value.Points[1].X, curve.Value.Points[1].Y)),
            "controlPoints" => new CurveValue.ControlPoints([.. curve.Value.Points.Select(point => new Point2(point.X, point.Y))]),
            _ => throw new InvalidOperationException(),
        }),
        _ => throw new InvalidOperationException(),
    };

    internal static MediaFeatureFileSource Source()
    {
        var writer = new MediaAbIdentityWriter();
        var rawColor = new MediaColorInterpretation(MediaTransfer.Srgb, MediaMatrix.Rgb, MediaRange.Full, MediaAlpha.Opaque,
            MediaChromaLocation.None, MediaNativePixelFormat.Rgb24, false, Hash('0'), default);
        var color = new MediaColorInterpretation(rawColor.Transfer, rawColor.Matrix, rawColor.Range, rawColor.Alpha,
            rawColor.ChromaLocation, rawColor.NativePixelFormat, false, rawColor.AuthorityDigest, writer.ComputeColorDigest(rawColor));
        var rawTool = new MediaToolIdentity(Hash('0'), Hash('1'), Hash('2'), default);
        var tool = new MediaToolIdentity(rawTool.Executables, rawTool.Dependencies, rawTool.NormalizedPipeline, writer.ComputeToolDigest(rawTool));
        var rawProfile = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline, MediaProfilesV1.Color,
            MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu, tool.Digest, color.Digest, default);
        var profile = new MediaProfileBundle(rawProfile.Presentation, rawProfile.DecoderPipeline, rawProfile.Color, rawProfile.Statistics,
            rawProfile.Quantization, rawProfile.Codec, rawProfile.Query, rawProfile.Gpu, tool.Digest, color.Digest, writer.ComputeProfileDigest(rawProfile));
        var rawDescriptor = new MediaFilePresentationDescriptor(0, 32, 18, 32, 18, null, 1, 1, MediaQuarterTurn.None, false, color, default);
        var descriptor = new MediaFilePresentationDescriptor(0, 32, 18, 32, 18, null, 1, 1, MediaQuarterTurn.None, false, color, writer.ComputeDescriptorDigest(rawDescriptor));
        var source = new MediaFeatureFileSource(default, MediaSourceKind.Still, new(Hash('3'), 12345), descriptor, tool, profile);
        return new(writer.ComputeSourceKey(source), source.Kind, source.Content, descriptor, tool, profile);
    }

    internal static MediaInputContext Context(ProjectMediaDependencyPlan plan, MediaProfileBundle profile, bool transparent = false)
        => MediaInputContext.Create(profile, MediaMapping.Create(320, 240, BackgroundFit.Contain, BackgroundPlaybackMode.Loop,
            new(0, 0, 0, 0), transparent ? MediaOutputMode.Transparent : MediaOutputMode.Normal), plan.QueryCatalogDigest, plan.FixedDependencyDigest);

    internal static MediaArtifactIdentity Artifact(MediaFeatureFileSource source) => new(Hash('4'),
        MediaTrackLayoutV1.GetArtifactBytes(source.Kind, 1), MediaProfilesV1.Codec, source.Key,
        source.Profile.Digest, source.Presentation.Digest, Hash('5'), 1, null);

    internal sealed class Sampler(MediaFeatureInput input, ProjectMediaDependencyPlan plan) : IProjectMediaFeatureSampler
    {
        public MediaFeatureInput Input { get; set; } = input;
        public ProjectMediaDependencyPlan Plan { get; set; } = plan;
        internal int Calls { get; private set; }
        internal ProjectMediaDependency? LastDependency { get; private set; }
        internal Func<ProjectMediaDependency, MediaFeatureQueryRect, long, CancellationToken, ProjectMediaFeatureSample>? OnSample { get; set; }
        public ProjectMediaFeatureSample Sample(ProjectMediaDependency dependency, MediaFeatureQueryRect rect, long projectTimeUs, CancellationToken token)
        {
            Calls++; LastDependency = dependency; token.ThrowIfCancellationRequested();
            if (OnSample is not null) return OnSample(dependency, rect, projectTimeUs, token);
            if (Input.State != MediaInputState.Available) return new(Input, dependency, rect, projectTimeUs);
            return dependency.Query.Space == EffectMediaCoordinateSpace.SourceNormalized
                ? new(Input, dependency, rect, projectTimeUs, new(new(.2, .2, .2, .5), .2, .08, .12, .25, .04))
                : new(Input, dependency, rect, projectTimeUs, canvasValues: new(new(.5, .5, .5, 1), .5, .3, .05));
        }
    }
}
