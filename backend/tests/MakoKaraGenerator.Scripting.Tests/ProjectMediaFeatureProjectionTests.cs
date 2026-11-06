using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;
using Xunit;
using CoreState = MakoKaraGenerator.Core.Media.MediaInputState;
using SdkState = MakoKaraGenerator.Scripting.V2.MediaInputState;
using F = MakoKaraGenerator.Scripting.Tests.ProjectMediaProjectionFixture;

namespace MakoKaraGenerator.Scripting.Tests;

/// <summary>Projection identity and detachment tests; the fake sampler is not a numerical oracle.</summary>
public sealed class ProjectMediaFeatureProjectionTests
{
    private static readonly string[] QueryIds = ["canvas", "source"];
    private static readonly string[] SourcePaths = ["lyricStyle.unsungFill", "lyricStyle.sungFill", "lyricStyle.stroke.material"];
    private static readonly string[] SourceRegions = ["fill", "fill", "stroke"];
    private sealed record Rig(EffectPackageContents Package, Project Project, EffectPackageMediaQueryCatalog Catalog,
        ProjectMediaDependencyPlan Plan, F.Sampler Sampler, ProjectMediaFeatureBinding Binding);

    private static Rig Create(string overrides = "{}", bool sources = false, bool available = false)
    {
        EffectPackageContents package = F.Package(); Project project = F.Project(package, sources, overrides);
        EffectPackageMediaQueryCatalog catalog = EffectPackageMediaQueries.Read(package);
        ProjectMediaDependencyPlan plan = ProjectMediaDependencyResolver.Resolve(project);
        MediaFeatureFileSource file = F.Source(); MediaInputContext context = F.Context(plan, file.Profile);
        MediaFeatureInput input = available
            ? MediaFeatureInput.CreateAvailableSolid(context, MediaFeatureSolidSource.Create(file.Profile, new(.2, .2, .2, .5)))
            : MediaFeatureInput.CreatePending(context, file, new(F.Hash('6')));
        var sampler = new F.Sampler(input, plan);
        return new(package, project, catalog, plan, sampler, new(sampler));
    }

    private static ProjectMediaFeatureProjection Instance(Rig rig, string id = "owner-0", string export = "scale", ParameterValues? parameters = null)
        => ProjectMediaFeatureProjection.ResolveInstance(rig.Binding, new(id), rig.Catalog, export,
            parameters ?? F.Parameters(rig.Catalog, export), Expected(rig, id, rig.Catalog, export));

    private static ImmutableDictionary<string, EffectParameterValue> Expected(Rig rig, string id,
        EffectPackageMediaQueryCatalog catalog, string export)
    {
        ProjectEffectInstance actual = rig.Project.Effects.Instances.Single(instance => instance.Id == id);
        return catalog.ResolveParameters(export, actual.Parameters.Encode());
    }

    private static ParameterValues Change(ParameterValues values, string id, ParameterValue replacement)
        => new(values.Entries.Select(pair => pair.Key == id ? KeyValuePair.Create(id, replacement) : pair));

    private static void Error(string code, Action action) => Assert.Equal(code, Assert.Throws<EffectOutputException>(action).Code);
    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    [Fact]
    public void RealCatalogAndResolverAdmitMediaCapability()
    {
        Rig rig = Create();
        Assert.True(EffectPackageProfiles.SupportedV2Capabilities.Contains("media.features"));
        Assert.NotNull(EffectPackageDefinition.Read(rig.Package));
        Assert.Equal(2, rig.Plan.Owners.Length); Assert.Equal(4, rig.Plan.Dependencies.Length);
        Assert.All(rig.Plan.Owners, owner => Assert.Equal(9, owner.Parameters.Count));
        ProjectMediaFeatureProjection projection = Instance(rig);
        Assert.Same(rig.Plan.Owners[0], projection.Owner);
        Assert.Equal(QueryIds, projection.Catalog.Profiles.Select(profile => profile.Id.Value));
        Assert.Equal(new long[] { 0, 120_000 }, projection.Catalog.Profiles.Select(profile => profile.WindowUs));
        Assert.Equal(MediaCoordinateSpace.CanvasPixels, projection.Catalog.Profiles[0].Space);
        Assert.Equal(MediaCoordinateSpace.SourceNormalized, projection.Catalog.Profiles[1].Space);
    }

    [Theory]
    [InlineData("{}", "{\"peak\":1.50,\"integer\":9007199254740993,\"duration\":120000,\"boolean\":true,\"text\":\"\",\"enum\":\"branch\",\"color\":{\"r\":0.123456789,\"g\":0.25,\"b\":0.75,\"a\":0.8},\"curve\":{\"kind\":\"easing\",\"easing\":\"easeOut\"},\"asset\":\"assets/image.png\"}")]
    [InlineData("{\"peak\":2,\"integer\":-7,\"duration\":1000000,\"boolean\":false,\"text\":\"精确文本\",\"enum\":\"project\",\"color\":{\"r\":0.3,\"g\":0.4,\"b\":0.5,\"a\":0.6},\"curve\":{\"kind\":\"cubicBezier\",\"first\":[0.1,0.2],\"second\":[0.8,0.9]}}", "{\"peak\":2.0,\"integer\":-7,\"duration\":1000000,\"boolean\":false,\"text\":\"精确文本\",\"enum\":\"project\",\"color\":{\"r\":0.3,\"g\":0.4,\"b\":0.5,\"a\":0.6},\"curve\":{\"kind\":\"cubicBezier\",\"first\":[0.1,0.2],\"second\":[0.8,0.9]}}")]
    [InlineData("{\"curve\":{\"kind\":\"controlPoints\",\"points\":[[0,0],[0.5,0.25],[1,1]]}}", "{\"curve\":{\"kind\":\"controlPoints\",\"points\":[[0,0],[0.5,0.25],[1,1]]}}")]
    public void CompleteTypedDefaultsAndEquivalentOverridesBind(string stored, string actual)
    {
        Rig rig = Create(stored); ParameterValues parameters = F.Parameters(rig.Catalog, overrides: actual);
        var projection = Instance(rig, parameters: parameters);
        Assert.Same(rig.Plan.Owners[0], projection.Owner);
        Assert.Equal(9, parameters.Ids.Length);
        Assert.Equal(((EffectParameterValue.Duration)rig.Plan.Owners[0].Parameters["duration"]).Value,
            projection.Catalog.Resolve(projection.Catalog.GetId("source")).WindowUs);
    }

    [Theory]
    [InlineData("{\"curve\":{\"kind\":\"easing\",\"easing\":\"linear\"}}")]
    [InlineData("{\"curve\":{\"kind\":\"easing\",\"easing\":\"easeInOut\"}}")]
    public void EveryDeclaredEasingUsesTheExistingTypedReader(string overrides)
    {
        Rig rig = Create(overrides);
        Assert.Same(rig.Plan.Owners[0], Instance(rig, parameters: F.Parameters(rig.Catalog, overrides: overrides)).Owner);
    }

    [Fact]
    public void CompleteParameterIdentityRejectsEveryWrongTypeValueMissingAndExtraKey()
    {
        Rig rig = Create(); ParameterValues original = F.Parameters(rig.Catalog);
        var foreignResource = new ResourceCatalog(rig.Package.PackageId, "sha256:" + new string('f', 64), [], [], ["assets/image.png"]);
        ParameterValues[] wrong = [
            Change(original, "peak", new ParameterValue.Number(Math.BitIncrement(1.5))),
            Change(original, "integer", new ParameterValue.Whole(9_007_199_254_740_992)),
            Change(original, "duration", new ParameterValue.Duration(120_001)),
            Change(original, "boolean", new ParameterValue.Toggle(false)),
            Change(original, "text", new ParameterValue.Text("changed non-window parameter")),
            Change(original, "enum", new ParameterValue.Choice("project")),
            Change(original, "color", new ParameterValue.Color(new(.123456789, .25, .75, Math.BitDecrement(.8)))),
            Change(original, "curve", new ParameterValue.Curve(new CurveValue.Easing(EasingKind.Linear))),
            Change(original, "asset", new ParameterValue.Asset(foreignResource.GetImage("assets/image.png"))),
            Change(original, "duration", new ParameterValue.Whole(120_000)),
            new(original.Entries.Where(pair => pair.Key != "text")),
            new(original.Entries.Append(KeyValuePair.Create<string, ParameterValue>("extra", new ParameterValue.Text("")))),
        ];
        foreach (ParameterValues parameters in wrong) Error("MediaFeatureBindingMismatch", () => Instance(rig, parameters: parameters));
        Assert.Equal(0, rig.Sampler.Calls);
    }

    [Fact]
    public void CurvesCompareEveryPointBitAndAssetChecksPackageRevisionAndPath()
    {
        const string overrides = "{\"curve\":{\"kind\":\"controlPoints\",\"points\":[[0,0],[0.5,0.25],[1,1]]}}";
        Rig rig = Create(overrides); ParameterValues values = F.Parameters(rig.Catalog, overrides: overrides);
        Error("MediaFeatureBindingMismatch", () => Instance(rig, parameters: Change(values, "curve",
            new ParameterValue.Curve(new CurveValue.ControlPoints([new(0, 0), new(.5, Math.BitIncrement(.25)), new(1, 1)])))));
        foreach (ResourceCatalog resources in new[] {
            new ResourceCatalog("another.package", rig.Package.Revision, [], [], ["assets/image.png"]),
            new ResourceCatalog(rig.Package.PackageId, rig.Package.Revision, [], [], ["assets/another.png"]) })
            Error("MediaFeatureBindingMismatch", () => Instance(rig, parameters: Change(values, "asset",
                new ParameterValue.Asset(resources.GetImage(resources.PackageId == "another.package" ? "assets/image.png" : "assets/another.png")))));
    }

    [Fact]
    public void InstanceKindIdPackageRevisionAndExportCannotBeSubstituted()
    {
        Rig rig = Create();
        Error("MediaFeatureBindingMismatch", () => ProjectMediaFeatureProjection.ResolveInstance(rig.Binding, new("missing"), rig.Catalog,
            "scale", F.Parameters(rig.Catalog), Expected(rig, "owner-0", rig.Catalog, "scale")));
        Error("MediaFeatureBindingMismatch", () => Instance(rig, export: "ink"));
        foreach (EffectPackageContents other in new[] { F.Package(m => m["packageId"] = "other.package"), F.Package(m => m["name"] = "Changed revision") })
        {
            var catalog = EffectPackageMediaQueries.Read(other);
            Error("MediaFeatureBindingMismatch", () => ProjectMediaFeatureProjection.ResolveInstance(rig.Binding, new("owner-0"), catalog,
                "scale", F.Parameters(catalog), Expected(rig, "owner-0", catalog, "scale")));
        }
        var standalone = new CompiledFrameEffect(new("owner-0"), null, null, F.Parameters(rig.Catalog), new EffectBinding.Project(), EffectCapabilities.None);
        Error("MediaFeatureBindingMismatch", () => ProjectMediaFeatureProjection.ResolveInstance(rig.Binding, standalone));
    }

    [Fact]
    public void SameQueryNamesAcrossOwnersAndSessionsHaveFreshScopes()
    {
        Rig rig = Create(); var first = Instance(rig); var second = Instance(rig, "owner-1"); var repeat = Instance(rig);
        Assert.NotSame(first.Catalog, second.Catalog); Assert.NotSame(first.Catalog, repeat.Catalog);
        Assert.NotSame(first.Catalog.Input, second.Catalog.Input);
        MediaFeatures firstStage = first.CreateStage(), secondStage = second.CreateStage(), repeatStage = repeat.CreateStage();
        Error("UndeclaredMediaQuery", () => secondStage.Sample(first.Catalog.GetId("source"), new(0, 0, 1, 1), 0));
        Error("UndeclaredMediaQuery", () => repeatStage.Sample(first.Catalog.GetId("source"), new(0, 0, 1, 1), 0));
        Assert.Equal(0, rig.Sampler.Calls);
        secondStage.Sample(second.Catalog.GetId("source"), new(0, 0, 1, 1), 0);
        Assert.Same(rig.Plan.Dependencies[3], rig.Sampler.LastDependency);
        firstStage.Invalidate(); secondStage.Invalidate(); repeatStage.Invalidate();
    }

    [Fact]
    public void NoQueryExportKeepsFixedInputWithFreshEmptyDirectory()
    {
        Rig rig = Create(); var projection = Instance(rig, "owner-plain", "plain"); var other = Instance(rig, "owner-plain", "plain");
        Assert.Null(projection.Owner); Assert.NotNull(projection.Catalog.Input); Assert.Empty(projection.Catalog.Profiles);
        Assert.NotSame(projection.Catalog, other.Catalog); Assert.NotSame(projection.Catalog.Input, other.Catalog.Input);
        Assert.Equal(rig.Sampler.Input.InputRevision.Digest.LowerHex, projection.Catalog.Input.InputRevision);
        MediaFeatures stage = projection.CreateStage();
        Error("UndeclaredMediaQuery", () => stage.Sample(Instance(rig).Catalog.GetId("source"), new(0, 0, 1, 1), 0));
        Assert.Equal(0, rig.Sampler.Calls); stage.Invalidate();
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("type")]
    [InlineData("nonWindowValue")]
    [InlineData("foreignAsset")]
    public void NoQueryMetadataRejectsWrongCompleteParameterShapeAndValue(string fault)
    {
        Rig rig = Create(); ParameterValues original = F.Parameters(rig.Catalog, "plain");
        var foreign = new ResourceCatalog(rig.Package.PackageId, "sha256:" + new string('f', 64), [], [], ["assets/image.png"]);
        ParameterValues wrong = fault switch
        {
            "empty" => ParameterValues.Empty,
            "missing" => new(original.Entries.Where(pair => pair.Key != "text")),
            "extra" => new(original.Entries.Append(KeyValuePair.Create<string, ParameterValue>("extra", new ParameterValue.Text("")))),
            "type" => Change(original, "duration", new ParameterValue.Whole(120_000)),
            "nonWindowValue" => Change(original, "text", new ParameterValue.Text("different fixed value")),
            "foreignAsset" => Change(original, "asset", new ParameterValue.Asset(foreign.GetImage("assets/image.png"))),
            _ => throw new InvalidOperationException(),
        };
        Error("MediaFeatureBindingMismatch", () => Instance(rig, "owner-plain", "plain", wrong));
        Assert.Equal(0, rig.Sampler.Calls);
    }

    [Fact]
    public void NoQueryMetadataKeepsActualOverridesInsteadOfDefaults()
    {
        const string overrides = "{\"peak\":2,\"text\":\"actual fixed override\",\"curve\":{\"kind\":\"controlPoints\",\"points\":[[0,0],[0.5,0.25],[1,1]]}}";
        Rig rig = Create(overrides); ParameterValues values = F.Parameters(rig.Catalog, "plain", overrides);
        var projection = Instance(rig, "owner-plain", "plain", values);
        Assert.Null(projection.Owner); Assert.NotNull(projection.Catalog.Input); Assert.Empty(projection.Catalog.Profiles);
        Error("MediaFeatureBindingMismatch", () => Instance(rig, "owner-plain", "plain", F.Parameters(rig.Catalog, "plain")));
        Assert.Equal(0, rig.Sampler.Calls);
    }

    [Fact]
    public void QueryMetadataExpectedValuesCannotReplaceFixedPlanParameters()
    {
        Rig rig = Create(); const string changed = "{\"text\":\"different from fixed plan\"}";
        ParameterValues parameters = F.Parameters(rig.Catalog, overrides: changed);
        var expected = rig.Catalog.ResolveParameters("scale", EffectParameterOverrides.Parse(changed).Encode());
        Error("MediaFeatureBindingMismatch", () => ProjectMediaFeatureProjection.ResolveInstance(rig.Binding, new("owner-0"),
            rig.Catalog, "scale", parameters, expected));
        Assert.Equal(0, rig.Sampler.Calls);
    }

    [Fact]
    public void CompiledNoQuerySchemaAcceptsCompleteTypedOverridesWithoutExecutingPackage()
    {
        Rig rig = Create();
        CompiledFrameEffect definition = F.NoQueryCompiledDescriptor("{\"peak\":2,\"text\":\"actual compiled override\"}");
        var projection = ProjectMediaFeatureProjection.ResolveInstance(rig.Binding, definition);
        Assert.Null(projection.Owner); Assert.NotNull(projection.Catalog.Input); Assert.Empty(projection.Catalog.Profiles);
        Assert.Equal(0, rig.Sampler.Calls);
    }

    [Theory]
    [InlineData("empty", "PackageParameterSetMismatch")]
    [InlineData("missing", "PackageParameterSetMismatch")]
    [InlineData("extra", "PackageParameterSetMismatch")]
    [InlineData("type", "PackageParameterTypeMismatch")]
    [InlineData("range", "PackageParameterOutOfRange")]
    [InlineData("choice", "PackageParameterOutOfRange")]
    [InlineData("foreignAsset", "InvalidParameterResource")]
    [InlineData("actualResources", "InvalidParameterResource")]
    public void CompiledNoQuerySchemaRejectsInvalidParametersAndActualResourceSubstitution(string fault, string code)
    {
        Rig rig = Create(); CompiledFrameEffect definition = F.NoQueryCompiledDescriptor(); ParameterValues original = definition.Parameters;
        var foreign = new ResourceCatalog(definition.PackageIdentity!.PackageId, "sha256:" + new string('f', 64), [], [], ["assets/image.png"]);
        ParameterValues wrong = fault switch
        {
            "empty" => ParameterValues.Empty,
            "missing" => new(original.Entries.Where(pair => pair.Key != "text")),
            "extra" => new(original.Entries.Append(KeyValuePair.Create<string, ParameterValue>("extra", new ParameterValue.Text("")))),
            "type" => Change(original, "duration", new ParameterValue.Whole(120_000)),
            "range" => Change(original, "peak", new ParameterValue.Number(5)),
            "choice" => Change(original, "enum", new ParameterValue.Choice("not-declared")),
            "foreignAsset" => Change(original, "asset", new ParameterValue.Asset(foreign.GetImage("assets/image.png"))),
            "actualResources" => original,
            _ => throw new InvalidOperationException(),
        };
        definition = definition with { Parameters = wrong, Resources = fault == "actualResources" ? ResourceCatalog.Empty : definition.Resources };
        Error(code, () => ProjectMediaFeatureProjection.ResolveInstance(rig.Binding, definition));
        Assert.Equal(0, rig.Sampler.Calls);
    }

    [Fact]
    public void SharedSourceMaterialRetainsThreeActualPathsAndRegions()
    {
        Rig rig = Create(sources: true); var references = SourceMaterialCatalog.Collect(rig.Project);
        Assert.Equal(3, references.Length); Assert.Equal(3, rig.Plan.Owners.Length);
        Assert.Same(references[0].Material, references[1].Material); Assert.Same(references[0].Material, references[2].Material);
        var value = (MaterialValue.PackageMaterial)MaterialValue.PackageExport(F.Resources(rig.Package).GetMaterial("ink"), F.Parameters(rig.Catalog, "ink"));
        var projections = references.Select(reference => ProjectMediaFeatureProjection.ResolveSource(rig.Binding,
            new(reference, reference.Region == "stroke" ? PreparedMaterialSlot.Stroke : PreparedMaterialSlot.UnsungFill), rig.Catalog, value)).ToArray();
        Assert.Equal(SourcePaths, projections.Select(item => item.Owner!.Path));
        Assert.Equal(SourceRegions, projections.Select(item => item.Owner!.Region));
        for (int index = 0; index < projections.Length; index++) Assert.Same(rig.Plan.Owners[index], projections[index].Owner);
        MediaFeatures stage = projections[1].CreateStage();
        Error("UndeclaredMediaQuery", () => stage.Sample(projections[0].Catalog.GetId("source"), new(0, 0, 1, 1), 0));
        stage.Sample(projections[1].Catalog.GetId("source"), new(0, 0, 1, 1), 2);
        Assert.Same(rig.Plan.Dependencies[3], rig.Sampler.LastDependency); stage.Invalidate();
        foreach (SourceMaterialReference wrong in new[] { references[0] with { Path = "lyricStyle.missing" }, references[0] with { Region = "stroke" } })
            Error("MediaFeatureBindingMismatch", () => ProjectMediaFeatureProjection.ResolveSource(rig.Binding,
                new(wrong, PreparedMaterialSlot.UnsungFill), rig.Catalog, value));
    }

    [Fact]
    public void SourceReparsesCompleteTypedParametersAndRejectsRuntimeOrStoredMismatch()
    {
        Rig rig = Create(sources: true); var reference = SourceMaterialCatalog.Collect(rig.Project)[0];
        ParameterValues values = F.Parameters(rig.Catalog, "ink");
        MaterialValue.PackageMaterial Value(ParameterValues parameters) => (MaterialValue.PackageMaterial)MaterialValue.PackageExport(F.Resources(rig.Package).GetMaterial("ink"), parameters);
        var equivalent = reference with { Material = new(rig.Package.PackageId, rig.Package.Revision, "ink", EffectParameterOverrides.Parse("{\"peak\":1.50}")) };
        Assert.Same(rig.Plan.Owners[0], ProjectMediaFeatureProjection.ResolveSource(rig.Binding, new(equivalent, PreparedMaterialSlot.UnsungFill), rig.Catalog, Value(values)).Owner);
        var changed = reference with { Material = new(rig.Package.PackageId, rig.Package.Revision, "ink", EffectParameterOverrides.Parse("{\"text\":\"different\"}")) };
        Error("MediaFeatureBindingMismatch", () => ProjectMediaFeatureProjection.ResolveSource(rig.Binding, new(changed, PreparedMaterialSlot.UnsungFill), rig.Catalog, Value(values)));
        Error("MediaFeatureBindingMismatch", () => ProjectMediaFeatureProjection.ResolveSource(rig.Binding, new(reference, PreparedMaterialSlot.UnsungFill), rig.Catalog,
            Value(Change(values, "text", new ParameterValue.Text("different")))));
        var foreign = new ResourceCatalog(rig.Package.PackageId, "sha256:" + new string('f', 64), ["ink"], [], []);
        Error("MediaFeatureBindingMismatch", () => ProjectMediaFeatureProjection.ResolveSource(rig.Binding, new(reference, PreparedMaterialSlot.UnsungFill), rig.Catalog,
            (MaterialValue.PackageMaterial)MaterialValue.PackageExport(foreign.GetMaterial("ink"), values)));
    }

    [Theory]
    [InlineData("pending", SdkState.Pending)]
    [InlineData("file", SdkState.Available)]
    [InlineData("solid", SdkState.Available)]
    [InlineData("unavailable", SdkState.Unavailable)]
    [InlineData("transparent", SdkState.NoMedia)]
    [InlineData("absent", SdkState.NoMedia)]
    public void EveryClosedInputBranchProjectsOnlySafeFixedMetadata(string branch, SdkState expected)
    {
        Rig rig = Create(); var source = F.Source(); var context = F.Context(rig.Plan, source.Profile, branch == "transparent");
        MediaFeatureInput input = branch switch
        {
            "pending" => MediaFeatureInput.CreatePending(context, source, new(F.Hash('6'))),
            "file" => MediaFeatureInput.CreateAvailableFile(context, source, F.Artifact(source)),
            "solid" => MediaFeatureInput.CreateAvailableSolid(context, MediaFeatureSolidSource.Create(source.Profile, new(.2, .2, .2, .5))),
            "unavailable" => MediaFeatureInput.CreateUnavailable(context, source, new(F.Hash('6')), MediaFailure.Create(new(F.Hash('7')), "MediaDecodeFailed", "Private safe detail withheld")),
            "transparent" => MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.TransparentOutput, F.Hash('8')),
            _ => MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.NoBackgroundContribution, F.Hash('8')),
        };
        var sampler = new F.Sampler(input, rig.Plan); var projection = ProjectMediaFeatureProjection.ResolveInstance(new(sampler), new("owner-0"), rig.Catalog,
            "scale", F.Parameters(rig.Catalog), Expected(rig, "owner-0", rig.Catalog, "scale"));
        var info = projection.Catalog.Input!;
        Assert.Equal(expected, info.State); Assert.Equal(input.InputRevision.Digest.LowerHex, info.InputRevision);
        Assert.Equal(context.Mapping.Key.Digest.LowerHex, info.MappingKey); Assert.Equal(source.Profile.Digest.LowerHex, info.ProfileDigest);
        Assert.Equal(branch == "file" ? F.Hash('4').LowerHex : null, info.ArtifactHash);
        Assert.Equal(branch == "unavailable" ? "MediaDecodeFailed" : null, info.DiagnosticCode);
        Assert.Equal(branch == "transparent" ? "TransparentOutput" : branch == "absent" ? "NoBackgroundContribution" : null, info.NoMediaReason);
        Assert.Equal(input is MediaFeatureInput.NoMedia ? null : input is MediaFeatureInput.AvailableSolid solid ? solid.Source.Key.Digest.LowerHex : source.Key.Digest.LowerHex, info.SourceKey);
        Assert.DoesNotContain(typeof(MediaFeatureInputInfo).GetProperties(), property => property.Name is "SafeDetail" or "Source" or "Reader" or "Request" or "Failure");
        MediaFeatures stage = projection.CreateStage(); var sample = stage.Sample(projection.Catalog.GetId("source"), new(0, 0, 1, 1), 17);
        Assert.Same(info, sample.Input); Assert.Equal(input.State == CoreState.Available, sample.SourceValues is not null);
        Assert.Null(sample.CanvasValues); stage.Invalidate();
    }

    [Fact]
    public void FreshStagesExpireIndependentlyWhileDetachedValuesAndCatalogRemainUsable()
    {
        Rig rig = Create(available: true); var projection = Instance(rig); MediaFeatures first = projection.CreateStage(), second = projection.CreateStage();
        var id = projection.Catalog.GetId("source"); MediaFeatureSample sample = first.Sample(id, new(0, 0, 1, 1), 0);
        first.Invalidate(); Error("InvalidMediaFeatureStage", () => first.Sample(id, new(0, 0, 1, 1), 0));
        Error("InvalidMediaFeatureStage", () => { _ = first.Catalog; });
        MediaFeatureSample later = second.Sample(id, new(0, 0, 1, 1), 1);
        Assert.NotSame(sample, later); Assert.NotSame(sample.SourceValues, later.SourceValues);
        Assert.Same(projection.Catalog.Input, sample.Input); Assert.Same(projection.Catalog.Resolve(id), sample.Query);
        Assert.Equal(.2, sample.SourceValues!.Mean.R); Assert.Equal(.04, sample.SourceValues.PremulYVariance);
        Assert.Equal(120_000, projection.Catalog.Resolve(projection.Catalog.GetId("source")).WindowUs); second.Invalidate();
        Assert.Equal(.2, sample.SourceValues.Mean.R); Assert.Equal("source", projection.Catalog.GetId("source").Value);
    }

    [Fact]
    public void CallbackReceivesExactDependencyBinary64RectAndExplicitTime()
    {
        Rig rig = Create(available: true); var projection = Instance(rig); MediaFeatures stage = projection.CreateStage();
        Rect2 rect = new(.12345678912345678, .23456789123456789, Math.BitIncrement(.75), Math.BitDecrement(.9));
        long time = long.MaxValue;
        rig.Sampler.OnSample = (dependency, actual, actualTime, _) =>
        {
            Assert.Same(rig.Plan.Dependencies[1], dependency); Assert.Equal(time, actualTime);
            Assert.Equal(Bits(rect.Left), Bits(actual.Left)); Assert.Equal(Bits(rect.Top), Bits(actual.Top));
            Assert.Equal(Bits(rect.Right), Bits(actual.Right)); Assert.Equal(Bits(rect.Bottom), Bits(actual.Bottom));
            return new(rig.Sampler.Input, dependency, actual, actualTime, new(new(.2, .2, .2, .5), .2, .08, .12, .25, .04));
        };
        var result = stage.Sample(projection.Catalog.GetId("source"), rect, time);
        Assert.Equal(Bits(rect.Right), Bits(result.Rect.Right)); Assert.Equal(time, result.ProjectTimeUs);
        stage.Invalidate();
    }

    [Theory]
    [InlineData("input")]
    [InlineData("ownerDependency")]
    [InlineData("newPlanDependency")]
    [InlineData("left")]
    [InlineData("top")]
    [InlineData("right")]
    [InlineData("bottom")]
    [InlineData("time")]
    public void CallbackCannotSubstituteEqualOrForeignIdentityOrAnyRequestField(string fault)
    {
        Rig rig = Create(); var projection = Instance(rig); MediaFeatures stage = projection.CreateStage();
        rig.Sampler.OnSample = (dependency, rect, time, _) =>
        {
            var input = fault == "input" ? MediaFeatureInput.CreatePending(rig.Sampler.Input.Context, F.Source(), new(F.Hash('6'))) : rig.Sampler.Input;
            var other = fault == "ownerDependency" ? rig.Plan.Dependencies[3]
                : fault == "newPlanDependency" ? ProjectMediaDependencyResolver.Resolve(rig.Project, CancellationToken.None).Dependencies[1] : dependency;
            var changed = new MediaFeatureQueryRect(fault == "left" ? Math.BitIncrement(rect.Left) : rect.Left,
                fault == "top" ? Math.BitIncrement(rect.Top) : rect.Top,
                fault == "right" ? Math.BitIncrement(rect.Right) : rect.Right,
                fault == "bottom" ? Math.BitIncrement(rect.Bottom) : rect.Bottom);
            return new(input, other, changed, fault == "time" ? time + 1 : time);
        };
        Error("MediaFeatureResultMismatch", () => stage.Sample(projection.Catalog.GetId("source"), new(.1, .2, .8, .9), 19));
        stage.Invalidate();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.1)]
    [InlineData(1.1)]
    public void DetachedCopyRejectsMalformedTrustedNumericValues(double fault)
    {
        Rig rig = Create(available: true); var projection = Instance(rig); MediaFeatures stage = projection.CreateStage();
        rig.Sampler.OnSample = (dependency, rect, time, _) => new(rig.Sampler.Input, dependency, rect, time,
            new(new(.2, .2, .2, .5), fault, .08, .12, .25, .04));
        Error("MediaFeatureResultMismatch", () => stage.Sample(projection.Catalog.GetId("source"), new(0, 0, 1, 1), 0)); stage.Invalidate();
    }

    [Fact]
    public void DetachedCopyRejectsNonPremultipliedMeanAndCopiesEverySourceMoment()
    {
        Rig rig = Create(available: true); var projection = Instance(rig); MediaFeatures stage = projection.CreateStage();
        ProjectSourceMediaFeatureValues? trustedValues = null;
        rig.Sampler.OnSample = (dependency, rect, time, _) => new(rig.Sampler.Input, dependency, rect, time,
            trustedValues = new(new(.2, .1, .05, .5), .2, .08, .12, .25, .04));
        var result = stage.Sample(projection.Catalog.GetId("source"), new(0, 0, 1, 1), 0);
        var values = Assert.IsType<SourceMediaFeatureValues>(result.SourceValues); Assert.NotSame(trustedValues, values);
        Assert.Equal(.2, values.Mean.R); Assert.Equal(.1, values.Mean.G); Assert.Equal(.05, values.Mean.B); Assert.Equal(.5, values.Mean.Alpha);
        Assert.Equal(.2, values.MeanPremulY); Assert.Equal(.08, values.MeanSquaredPremulY);
        Assert.Equal(.12, values.MeanAlphaTimesPremulY); Assert.Equal(.25, values.MeanSquaredAlpha); Assert.Equal(.04, values.PremulYVariance);
        rig.Sampler.OnSample = (dependency, rect, time, _) => new(rig.Sampler.Input, dependency, rect, time,
            new(new(.6, .1, .05, .5), .2, .08, .12, .25, .04));
        Error("MediaFeatureResultMismatch", () => stage.Sample(projection.Catalog.GetId("source"), new(0, 0, 1, 1), 0)); stage.Invalidate();
        Assert.Equal(.12, values.MeanAlphaTimesPremulY);
    }

    [Fact]
    public void CanvasSampleCopiesEveryPassiveValueWithoutProviderBacking()
    {
        Rig rig = Create(available: true); var projection = Instance(rig); MediaFeatures stage = projection.CreateStage();
        var result = stage.Sample(projection.Catalog.GetId("canvas"), new(.1, .2, 319.9, 239.8), 20);
        Assert.Null(result.SourceValues); var values = Assert.IsType<CanvasMediaFeatureValues>(result.CanvasValues);
        Assert.Equal(.5, values.Mean.R); Assert.Equal(.5, values.Mean.G); Assert.Equal(.5, values.Mean.B); Assert.Equal(1d, values.Mean.Alpha);
        Assert.Equal(.5, values.MeanPremulY); Assert.Equal(.3, values.MeanSquaredPremulY); Assert.Equal(.05, values.PremulYVariance); Assert.True(values.IsOpaque);
        stage.Invalidate(); Assert.Equal(.3, values.MeanSquaredPremulY);
    }

    [Fact]
    public void CancellationBeforeAndAfterCallbackAndSamplerFaultsPropagate()
    {
        Rig rig = Create(); var projection = Instance(rig); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => projection.CreateStage(cancellation.Token)); Assert.Equal(0, rig.Sampler.Calls);
        using var after = new CancellationTokenSource(); MediaFeatures stage = projection.CreateStage(after.Token);
        rig.Sampler.OnSample = (dependency, rect, time, _) => { after.Cancel(); return new(rig.Sampler.Input, dependency, rect, time); };
        Assert.Throws<OperationCanceledException>(() => stage.Sample(projection.Catalog.GetId("source"), new(0, 0, 1, 1), 0)); stage.Invalidate();
        var failure = new InvalidOperationException("controlled sampler fault"); rig.Sampler.OnSample = (_, _, _, _) => throw failure;
        MediaFeatures failing = projection.CreateStage();
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => failing.Sample(projection.Catalog.GetId("source"), new(0, 0, 1, 1), 0))); failing.Invalidate();
    }

    [Fact]
    public void CancelledResolutionAndInvalidQueriesNeverReachSampler()
    {
        Rig rig = Create(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectMediaFeatureProjection.ResolveInstance(rig.Binding, new("owner-0"), rig.Catalog,
            "scale", F.Parameters(rig.Catalog), Expected(rig, "owner-0", rig.Catalog, "scale"), cancelled.Token));
        var projection = Instance(rig); MediaFeatures stage = projection.CreateStage(); MediaQueryId id = projection.Catalog.GetId("source");
        Error("UndeclaredMediaQuery", () => stage.Sample(default, new(0, 0, 1, 1), 0));
        Error("InvalidMediaQuery", () => stage.Sample(id, new(0, 0, 1, 1), -1));
        Error("InvalidMediaQuery", () => stage.Sample(id, new(0, 0, 0, 1), 0));
        Error("InvalidMediaQuery", () => stage.Sample(id, new(0, 0, 1.01, 1), 0));
        Assert.Equal(0, rig.Sampler.Calls); stage.Invalidate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SamplerCannotReplaceFixedInputOrPlanAtStageOrCallback(bool replacePlan)
    {
        Rig rig = Create(); var projection = Instance(rig); MediaFeatures stage = projection.CreateStage();
        void Replace()
        {
            if (replacePlan) rig.Sampler.Plan = ProjectMediaDependencyResolver.Resolve(rig.Project);
            else rig.Sampler.Input = MediaFeatureInput.CreatePending(rig.Sampler.Input.Context, F.Source(), new(F.Hash('6')));
        }
        rig.Sampler.OnSample = (dependency, rect, time, _) => { var result = new ProjectMediaFeatureSample(rig.Sampler.Input, dependency, rect, time); Replace(); return result; };
        Error("MediaFeatureBindingMismatch", () => stage.Sample(projection.Catalog.GetId("source"), new(0, 0, 1, 1), 0));
        Error("MediaFeatureBindingMismatch", () => projection.CreateStage()); stage.Invalidate();
    }
}
