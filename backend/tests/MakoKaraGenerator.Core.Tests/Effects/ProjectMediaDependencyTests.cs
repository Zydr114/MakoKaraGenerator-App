using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using Xunit;

namespace MakoKaraGenerator.Core.Tests.Effects;

public sealed class ProjectMediaDependencyTests
{
    private static readonly string[] CompleteOutputOrder = ["other", "scale"];
    private static readonly string[] Utf8QueryOrder = ["\uE000", "\U00010000"];
    private static ProjectMediaDependencyPlan Plan(EffectPackageContents package, string parameters = "{}") =>
        ProjectMediaDependencyResolver.Resolve(MediaQueryPackageFixture.Project([package], [MediaQueryPackageFixture.Instance(package, parameters: parameters)]));
    private static JsonNode Declaration(JsonObject manifest) => manifest["mediaQueries"]![0]!;
    private static JsonNode Export(JsonObject manifest) => manifest["exports"]![0]!;
    private static JsonNode Reference(JsonObject manifest) => Export(manifest)["mediaQueries"]![0]!;
    private static EffectPackageMediaQueryCatalog Catalog(Action<JsonObject> change) =>
        EffectPackageMediaQueries.Read(MediaQueryPackageFixture.Controller(change));
    private static void Reject(Action<JsonObject> change) => Assert.Throws<EffectPackageException>(() => Catalog(change));
    private static void Oracle(ProjectMediaDependencyPlan plan)
    {
        Assert.Equal(MediaDependencyOracle.Catalog(plan), plan.QueryCatalogDigest.LowerHex);
        Assert.Equal(MediaDependencyOracle.Plan(plan), plan.FixedDependencyDigest.LowerHex);
        Assert.All(plan.Owners, owner => Assert.Equal(MediaDependencyOracle.Parameters(owner), owner.ParametersDigest.LowerHex));
    }

    [Fact]
    public void MediaCapabilityHasStrictCatalogAndExecutableDefinition()
    {
        var package = MediaQueryPackageFixture.Controller(); var catalog = EffectPackageMediaQueries.Read(package);
        var query = Assert.Single(catalog.Queries); Assert.IsType<EffectMediaWindow.Parameter>(query.Window);
        Assert.Equal(EffectMediaCoordinateSpace.CanvasPixels, query.Space);
        Assert.Single(catalog.GetExport("scale").MediaQueries);
        Assert.True(EffectPackageProfiles.SupportedV2Capabilities.Contains("media.features"));
        Assert.False(EffectPackageProfiles.SupportedV2Capabilities.Contains("media.features.cpu"));
        Assert.NotNull(EffectPackageDefinition.Read(package));
        Assert.Throws<EffectPackageException>(() => MediaQueryPackageFixture.Controller(m => m["requires"]!.AsArray().Add("media.features.cpu")));
        Assert.DoesNotContain(typeof(EffectPackageMediaQueryCatalog).GetMethods(), method => method.ReturnType == typeof(EffectPackageDefinition));
        Oracle(Plan(package));
    }

    [Fact]
    public void OldV1AndV2PackagesKeepEmptyOptionalQueryFieldsAndExecutionBehavior()
    {
        foreach (var package in new[] { EffectPackageFixture.Contents(), SdkV2PackageFixture.Contents() })
        {
            var catalog = EffectPackageMediaQueries.Read(package); Assert.Empty(catalog.Queries);
            Assert.All(catalog.Exports, export => Assert.Empty(export.MediaQueries));
            Assert.NotNull(EffectPackageDefinition.Read(package));
            Assert.Equal(EffectPackageDefinition.Read(package).ResolveParameters("scale")["peak"], catalog.ResolveParameters("scale")["peak"]);
        }
        var old = EffectPackageFixture.Contents(); var root = JsonNode.Parse(old.Manifest.AsSpan())!.AsObject(); root["mediaQueries"] = new JsonArray();
        Assert.Equal("PackageManifestUnknownField", Assert.Throws<EffectPackageException>(() => MediaQueryPackageFixture.Recreate(old, root)).Code);
        root.Remove("mediaQueries"); root["exports"]![0]!["mediaQueries"] = new JsonArray();
        var invalid = MediaQueryPackageFixture.Recreate(old, root);
        Assert.Equal("PackageSemanticUnknownField", Assert.Throws<EffectPackageException>(() => EffectPackageMediaQueries.Read(invalid)).Code);
    }

    [Fact]
    public void QueryAndReferenceObjectsRejectUnknownKeysAtEveryNewLevel()
    {
        foreach (string level in new[] { "query", "window", "reference", "fallback", "state" })
            Reject(m =>
            {
                var node = level switch
                {
                    "query" => Declaration(m), "window" => Declaration(m)["window"]!, "reference" => Reference(m),
                    "fallback" => Reference(m)["fallback"]!, _ => Reference(m)["fallback"]!["pending"]!,
                };
                node["typo"] = true;
            });
        foreach (string field in new[] { "id", "space", "window" }) Reject(m => Declaration(m).AsObject().Remove(field));
        foreach (string field in new[] { "query", "fallback" }) Reject(m => Reference(m).AsObject().Remove(field));
        foreach (string state in new[] { "pending", "unavailable", "noMedia" }) Reject(m => Reference(m)["fallback"]!.AsObject().Remove(state));
        Reject(m => m["mediaQueries"] = new JsonObject());
        Reject(m => Export(m)["mediaQueries"] = "query");
    }

    [Fact]
    public void DuplicateJsonKeysAndDuplicateQueryIdentitiesNeverOverwriteEarlierValues()
    {
        var package = MediaQueryPackageFixture.Controller(); string manifest = Encoding.UTF8.GetString(package.Manifest.AsSpan());
        foreach (var (search, replace) in new[] { ("\"space\":\"canvasPixels\"", "\"space\":\"canvasPixels\",\"space\":\"sourceNormalized\""),
            ("\"query\":\"query\"", "\"query\":\"query\",\"query\":\"other\""),
            ("\"kind\":\"parameter\"", "\"kind\":\"parameter\",\"kind\":\"literal\"") })
        {
            string duplicate = manifest.Replace(search, replace, StringComparison.Ordinal); Assert.NotEqual(manifest, duplicate);
            Assert.Equal("PackageJsonDuplicateKey", Assert.Throws<EffectPackageException>(() => EffectPackageContents.CreateSupported(
                Encoding.UTF8.GetBytes(duplicate), package.Files.Select(f => KeyValuePair.Create(f.Path, (ReadOnlyMemory<byte>)f.Content.ToArray())))).Code);
        }
        Reject(m => m["mediaQueries"]!.AsArray().Add(Declaration(m).DeepClone()));
        Reject(m => Export(m)["mediaQueries"]!.AsArray().Add(Reference(m).DeepClone()));
        Reject(m => Reference(m)["query"] = "unlisted");
        foreach (string id in new[] { "", " ", "bad\nquery", "bad\0query", "\uFEFFquery" }) Reject(m => Declaration(m)["id"] = id);
    }

    [Fact]
    public void LiteralWindowsAreExactBoundedIntegerMicrosecondsWithKnownSpaceAndKind()
    {
        foreach (long boundary in new[] { 0L, 2_000_000L })
        {
            var package = MediaQueryPackageFixture.Controller(m => m["mediaQueries"] = new JsonArray(MediaQueryPackageFixture.Query(literal: boundary)));
            Assert.Equal(boundary, Assert.Single(Plan(package).DistinctWindowsUs));
        }
        foreach (JsonNode? value in new JsonNode?[] { JsonValue.Create(-1), JsonValue.Create(2_000_001), JsonValue.Create(.5), JsonValue.Create("100"), null })
            Reject(m => Declaration(m)["window"] = new JsonObject { ["kind"] = "literal", ["durationUs"] = value?.DeepClone() });
        Reject(m => Declaration(m)["space"] = "canvas"); Reject(m => Declaration(m)["window"]!["kind"] = "expression");
        Reject(m => Declaration(m)["window"] = new JsonObject { ["kind"] = "literal" });
    }

    [Fact]
    public void WindowParameterMustBeDurationVisibleToExportAndEntireRangeMustBeLegal()
    {
        Reject(m => Declaration(m)["window"]!["parameter"] = "missing");
        Reject(m => Declaration(m)["window"]!["parameter"] = "peak");
        Reject(m => Export(m)["parameters"] = new JsonArray("peak"));
        Reject(m => m["parameters"]![1]!["max"] = 2_000_001L);
        Reject(m => m["parameters"]![1]!["min"] = -1L);
        Reject(m => m["parameters"]![1]!["type"] = "integer");
        Assert.Throws<ProjectMediaDependencyException>(() => Plan(MediaQueryPackageFixture.Controller(), "{\"window\":1.5}"));
        Assert.Throws<ProjectMediaDependencyException>(() => Plan(MediaQueryPackageFixture.Controller(), "{\"window\":\"1\"}"));
        Assert.Throws<ProjectMediaDependencyException>(() => Plan(MediaQueryPackageFixture.Controller(), "{\"window\":2000001}"));
    }

    [Fact]
    public void MediaRequiresDeclarationsAndReferencesAndOutputRequiresStillUsePackageGate()
    {
        Reject(m => m["requires"] = new JsonArray("token.affine", "image.graph"));
        Reject(m => m["mediaQueries"] = new JsonArray());
        Reject(m => Export(m).AsObject().Remove("mediaQueries"));
        Reject(m => Export(m)["outputs"]![0]!["requires"] = new JsonArray("style.fill"));
        Reject(m => Export(m)["outputs"]![0]!["requires"] = new JsonArray("future.unknown"));
        var mediaOutput = MediaQueryPackageFixture.Controller(m => Export(m)["outputs"]![0]!["requires"] = new JsonArray("media.features"));
        Assert.NotNull(EffectPackageMediaQueries.Read(mediaOutput));
        Assert.NotNull(EffectPackageDefinition.Read(mediaOutput));
        var old = SdkV2PackageFixture.Contents(m => m["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray("style.fill"));
        Assert.Equal("PackageCapabilityMissing", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(old)).Code);
    }

    [Fact]
    public void ThreeFallbackStatesMustEachCoverExactlyCurrentExportOutputs()
    {
        foreach (string state in new[] { "pending", "unavailable", "noMedia" })
        {
            Reject(m => Reference(m)["fallback"]![state]!["kind"] = "outputBypass");
            Reject(m => Reference(m)["fallback"]![state]!["outputs"] = new JsonArray());
            Reject(m => Reference(m)["fallback"]![state]!["outputs"] = new JsonArray("another-export-output"));
            Reject(m => Reference(m)["fallback"]![state]!["outputs"] = new JsonArray("scale", "scale"));
            Reject(m => Reference(m)["fallback"]![state]!.AsObject().Remove("outputs"));
        }
        Reject(m =>
        {
            var other = Export(m)["outputs"]![0]!.DeepClone(); other["id"] = "other"; Export(m)["outputs"]!.AsArray().Add(other);
        });
        var complete = MediaQueryPackageFixture.Controller(m =>
        {
            var other = Export(m)["outputs"]![0]!.DeepClone(); other["id"] = "other"; Export(m)["outputs"]!.AsArray().Add(other);
            Export(m)["mediaQueries"] = new JsonArray(MediaQueryPackageFixture.Reference("query", "authorBranch", "scale", "other"));
        });
        Assert.Equal(CompleteOutputOrder, EffectPackageMediaQueries.Read(complete).GetExport("scale").MediaQueries[0].Pending.OutputIds);
    }

    [Fact]
    public void PureSourceMaterialWithoutAnyControllerHasRealOwnerAndExistingFallback()
    {
        var package = MediaQueryPackageFixture.PureMaterial(); var catalog = EffectPackageMediaQueries.Read(package);
        Assert.DoesNotContain(catalog.Exports, export => export is EffectControllerDefinition);
        var material = Assert.IsType<EffectMaterialDefinition>(catalog.GetExport("ink"));
        Assert.IsType<EffectMaterialFallback.Solid>(material.Fallback);
        Assert.IsType<EffectOutputBypass.MaterialFallback>(Assert.Single(material.Outputs).Bypass);
        var project = MediaQueryPackageFixture.Project([package], source: package);
        var plan = ProjectMediaDependencyResolver.Resolve(project); var owner = Assert.Single(plan.Owners);
        Assert.Equal(ProjectMediaDependencyOwnerKind.SourceMaterial, owner.Kind); Assert.Equal("lyricStyle.unsung.fill", owner.Path);
        Assert.Equal("fill", owner.Region); Assert.Empty(project.Effects.Instances);
        Assert.IsType<EffectMediaFallbackReference.OutputBypass>(Assert.Single(plan.Dependencies).Reference.NoMedia);
        Oracle(plan);
    }

    [Fact]
    public void GraphFallbackUsesVerifiedOriginalInputAndCannotPointBackToMediaOrNode()
    {
        var package = MediaQueryPackageFixture.PureMaterial();
        var graph = Assert.IsType<EffectGraphDefinition>(EffectPackageMediaQueries.Read(package).GetExport("tint"));
        Assert.Equal("original", Assert.IsType<EffectOutputBypass.GraphInput>(Assert.Single(graph.Outputs).Bypass).InputId);
        var project = MediaQueryPackageFixture.Project([package], [MediaQueryPackageFixture.Instance(package, export: "tint")]);
        Oracle(ProjectMediaDependencyResolver.Resolve(project));
        foreach (string input in new[] { "input:background", "node:probe", "input:missing" })
            Assert.Throws<EffectPackageException>(() => EffectPackageMediaQueries.Read(MediaQueryPackageFixture.PureMaterial(m => m["exports"]![1]!["outputs"]![0]!["bypass"]!["input"] = input)));
        Assert.Throws<EffectPackageException>(() => EffectPackageMediaQueries.Read(MediaQueryPackageFixture.PureMaterial(m => m["exports"]![1]!["outputs"]![0]!.AsObject().Remove("bypass"))));
        var invalidRoot = Assert.Throws<EffectPackageException>(() => EffectPackageMediaQueries.Read(MediaQueryPackageFixture.PureMaterial(graphChange: g => g["bypassInput"] = "input:background")));
        Assert.Equal("InvalidGraphBypass", invalidRoot.Code);
        Assert.IsType<ImageGraphException>(invalidRoot.InnerException);
        Assert.Throws<EffectPackageException>(() => EffectPackageMediaQueries.Read(MediaQueryPackageFixture.PureMaterial(m => m["exports"]![0]!["mediaQueries"]![0]!["fallback"]!["pending"]!["kind"] = "authorBranch")));
    }

    [Fact]
    public void DisabledInstanceAndUnusedRegisteredPackageDoNotManufactureDependencies()
    {
        var used = MediaQueryPackageFixture.Controller();
        var unused = MediaQueryPackageFixture.Controller(m => m["packageId"] = "unused.package");
        var disabled = MediaQueryPackageFixture.Instance(unused, "disabled") with { Enabled = false, ExportId = "missing-export" };
        var project = MediaQueryPackageFixture.Project([used], [MediaQueryPackageFixture.Instance(used), disabled]);
        var plan = ProjectMediaDependencyResolver.Resolve(project); Assert.Single(plan.Owners); Assert.Single(plan.Dependencies);
        var empty = ProjectMediaDependencyResolver.Resolve(MediaQueryPackageFixture.Project([unused], [disabled]));
        Assert.Empty(empty.Owners); Assert.Empty(empty.Dependencies); Assert.Empty(empty.DistinctWindowsUs); Oracle(empty);
    }

    [Fact]
    public void ExactRevisionNeverSelectsLatestSamePackageIdOrAnotherExport()
    {
        var first = MediaQueryPackageFixture.Controller(m => m["mediaQueries"] = new JsonArray(MediaQueryPackageFixture.Query(literal: 100)));
        var second = MediaQueryPackageFixture.Controller(m => m["mediaQueries"] = new JsonArray(MediaQueryPackageFixture.Query(literal: 200)));
        Assert.Equal(first.PackageId, second.PackageId); Assert.NotEqual(first.Revision, second.Revision);
        var project = MediaQueryPackageFixture.Project([second, first], [MediaQueryPackageFixture.Instance(first)]);
        Assert.Equal(100, Assert.Single(ProjectMediaDependencyResolver.Resolve(project).DistinctWindowsUs));
        var missing = Assert.Throws<ProjectMediaDependencyException>(() => ProjectMediaDependencyResolver.Resolve(project with { Effects = new([second], project.Effects.Instances) }));
        Assert.Equal(first.Revision, missing.Owner.Revision); Assert.Equal("EffectPackageReferenceMissing", missing.Code);
        var badExport = project with { Effects = new([first], [MediaQueryPackageFixture.Instance(first) with { ExportId = "missing" }]) };
        Assert.Equal("PackageExportMissing", Assert.Throws<ProjectMediaDependencyException>(() => ProjectMediaDependencyResolver.Resolve(badExport)).Code);
    }

    [Fact]
    public void TypedDefaultEquivalentOverridesHaveEqualIdentityButActualParametersInvalidateClosure()
    {
        var package = MediaQueryPackageFixture.Controller(); var defaults = Plan(package);
        var explicitDefaults = Plan(package, "{\"window\":200000,\"peak\":1.50}");
        Assert.Equal(defaults.QueryCatalogDigest, explicitDefaults.QueryCatalogDigest); Assert.Equal(defaults.FixedDependencyDigest, explicitDefaults.FixedDependencyDigest);
        var changedWindow = Plan(package, "{\"window\":200001}");
        Assert.NotEqual(defaults.QueryCatalogDigest, changedWindow.QueryCatalogDigest); Assert.NotEqual(defaults.FixedDependencyDigest, changedWindow.FixedDependencyDigest);
        var changedOther = Plan(package, "{\"peak\":1.75}");
        Assert.Equal(defaults.QueryCatalogDigest, changedOther.QueryCatalogDigest); Assert.NotEqual(defaults.FixedDependencyDigest, changedOther.FixedDependencyDigest);
        Assert.Equal(200000, Assert.Single(defaults.Dependencies).WindowUs);
        Assert.Equal("4b351d94e936083d92d863b17ba20252e5fbe5a730d2cba767b96b242ecb3860", Assert.Single(defaults.Owners).ParametersDigest.LowerHex); Oracle(defaults); Oracle(explicitDefaults); Oracle(changedWindow); Oracle(changedOther);
    }

    [Fact]
    public void FourWindowsAreAProjectUnionAcrossInstancesSpacesAndRealMaterialOwner()
    {
        var controller = MediaQueryPackageFixture.Controller(m =>
        {
            m["mediaQueries"]!.AsArray().Add(MediaQueryPackageFixture.Query("source", space: "sourceNormalized"));
            Export(m)["mediaQueries"]!.AsArray().Add(MediaQueryPackageFixture.Reference("source"));
        });
        var material = MediaQueryPackageFixture.PureMaterial();
        var instances = Enumerable.Range(0, 4).Select(i => MediaQueryPackageFixture.Instance(controller, "instance-" + i, "{\"window\":" + i + "}")).ToArray();
        var project = MediaQueryPackageFixture.Project([controller, material], instances, material, "{\"window\":0}");
        var plan = ProjectMediaDependencyResolver.Resolve(project); Assert.Equal(new long[] { 0, 1, 2, 3 }, plan.DistinctWindowsUs);
        Assert.Equal(9, plan.Dependencies.Length); Assert.Equal(5, plan.Owners.Length); Oracle(plan);
        var failed = Assert.Throws<ProjectMediaDependencyException>(() => ProjectMediaDependencyResolver.Resolve(MediaQueryPackageFixture.Project([controller, material], instances, material, "{\"window\":4}")));
        Assert.Equal("ProjectMediaWindowCapacityExceeded", failed.Code); Assert.Equal("lyricStyle.unsung.fill", failed.Owner.Path);
        Assert.Equal(ProjectMediaDependencyOwnerKind.SourceMaterial, failed.Owner.Kind);
    }

    [Fact]
    public void BatchQueryCapacityIsNotReusedAsDirectoryOrOwnerCountLimit()
    {
        var package = MediaQueryPackageFixture.Controller();
        var project = MediaQueryPackageFixture.Project([package], Enumerable.Range(0, 65).Select(i => MediaQueryPackageFixture.Instance(package, "i" + i)).ToArray());
        Assert.Equal(65, ProjectMediaDependencyResolver.Resolve(project).Owners.Length);
        var directory = MediaQueryPackageFixture.Controller(m =>
        {
            m["mediaQueries"] = new JsonArray(); Export(m)["mediaQueries"] = new JsonArray();
            for (int i = 0; i < 65; i++) { string id = "q" + i; m["mediaQueries"]!.AsArray().Add(MediaQueryPackageFixture.Query(id, 0)); Export(m)["mediaQueries"]!.AsArray().Add(MediaQueryPackageFixture.Reference(id)); }
        });
        Assert.Equal(65, Plan(directory).Dependencies.Length); Assert.Single(Plan(directory).DistinctWindowsUs);
    }

    [Fact]
    public void FullSourceCatalogIncludesHiddenBranchLineAndTokenOverrides()
    {
        var package = MediaQueryPackageFixture.PureMaterial();
        var overlay = new LyricStyleOverride { UnsungFill = new FillSpecOverride { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } };
        var lyrics = LyricsDocument.Create([LyricLine.Create("line", "字", tokens: [LyricToken.Create("token", "字") with { StyleOverride = overlay }])]);
        var branch = lyrics.PrimaryBranch!; var line = branch.Lines[0];
        lyrics = lyrics with { Branches = lyrics.Branches.Replace(branch, branch with { StyleOverride = overlay, Lines = branch.Lines.Replace(line, line with { StyleOverride = overlay }) }) };
        var project = MediaQueryPackageFixture.Project([package]) with { Lyrics = lyrics };
        var expected = SourceMaterialCatalog.Collect(project); Assert.Equal(3, expected.Length);
        var plan = ProjectMediaDependencyResolver.Resolve(project); Assert.Equal(expected.Select(s => s.Path), plan.Owners.Select(o => o.Path));
        Assert.Equal(3, plan.Dependencies.Length); Assert.Single(plan.DistinctWindowsUs); Oracle(plan);
    }

    [Fact]
    public void FailuresPreserveActualSourceOwnerRevisionAndOriginalParameterOrRegionCode()
    {
        var material = MediaQueryPackageFixture.PureMaterial();
        var badParameter = MediaQueryPackageFixture.Project([material], source: material, sourceParameters: "{\"window\":\"bad\"}");
        var error = Assert.Throws<ProjectMediaDependencyException>(() => ProjectMediaDependencyResolver.Resolve(badParameter));
        Assert.Equal("lyricStyle.unsung.fill", error.Owner.Path); Assert.Equal("fill", error.Owner.Region); Assert.Equal(material.Revision, error.Owner.Revision);
        Assert.Equal("PackageParameterTypeMismatch", error.Code); Assert.Equal("parameters.window", error.Path);
        var restricted = MediaQueryPackageFixture.PureMaterial(m => m["exports"]![0]!["regions"] = new JsonArray("stroke"));
        error = Assert.Throws<ProjectMediaDependencyException>(() => ProjectMediaDependencyResolver.Resolve(MediaQueryPackageFixture.Project([restricted], source: restricted)));
        Assert.Equal("SourceMaterialRegionUnsupported", error.Code);
        var broken = MediaQueryPackageFixture.Controller(m => Reference(m)["query"] = "missing");
        error = Assert.Throws<ProjectMediaDependencyException>(() => Plan(broken));
        Assert.Equal("instance", error.Owner.Path); Assert.Equal("PackageMediaQueryMissing", error.Code);
    }

    [Fact]
    public void CancellationIsNotWrappedAsFailureAndEmptyPlanHasIndependentStableIdentity()
    {
        var project = MakoKaraGenerator.Core.Projects.Project.Create("空闭包");
        var plan = ProjectMediaDependencyResolver.Resolve(project); Assert.Empty(plan.Dependencies); Oracle(plan);
        Assert.Equal("00a7f33139b2be591771e0dd17b4ac2ee421855b4e3865cfabb50e9003706865", plan.QueryCatalogDigest.LowerHex);
        Assert.Equal("79d5990bba0e38fee443d2fcc3a942aa54039075a5e4c0c56bff076b0bffe253", plan.FixedDependencyDigest.LowerHex);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectMediaDependencyResolver.Resolve(project, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => EffectPackageMediaQueries.Read(MediaQueryPackageFixture.Controller(), cancellation.Token));
    }

    [Fact]
    public void AllNineParameterKindsUseOneParserAndIndependentTypedIdentityOracle()
    {
        var files = EffectPackageFixture.Files(); files["assets/image.png"] = [1, 2, 3];
        var manifest = EffectPackageFixture.NineParametersManifest(files); MediaQueryPackageFixture.AddQueries(manifest);
        var package = EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
        var plan = Plan(package); var owner = Assert.Single(plan.Owners); Assert.Equal(10, owner.Parameters.Count);
        Assert.Equal(9_007_199_254_740_993L, Assert.IsType<EffectParameterValue.Whole>(owner.Parameters["integer"]).Value); Oracle(plan);
        var changed = Plan(package, "{\"integer\":9007199254740994}"); Assert.NotEqual(plan.FixedDependencyDigest, changed.FixedDependencyDigest);
        Assert.Equal(plan.QueryCatalogDigest, changed.QueryCatalogDigest); Oracle(changed);
    }


    [Fact]
    public void OwnerOrderAndExactSourceIdentityParticipateWithoutInputOrMappingCycles()
    {
        var package = MediaQueryPackageFixture.Controller();
        var first = MediaQueryPackageFixture.Instance(package, "first"); var second = MediaQueryPackageFixture.Instance(package, "second");
        var a = ProjectMediaDependencyResolver.Resolve(MediaQueryPackageFixture.Project([package], [first, second]));
        var b = ProjectMediaDependencyResolver.Resolve(MediaQueryPackageFixture.Project([package], [second, first]));
        Assert.NotEqual(a.QueryCatalogDigest, b.QueryCatalogDigest); Assert.NotEqual(a.FixedDependencyDigest, b.FixedDependencyDigest);
        Assert.Equal(a.DistinctWindowsUs.ToArray(), b.DistinctWindowsUs.ToArray()); Oracle(a); Oracle(b);
        var project = MediaQueryPackageFixture.Project([package], [first]);
        var original = ProjectMediaDependencyResolver.Resolve(project);
        var nameChanged = ProjectMediaDependencyResolver.Resolve(project with { Name = "未进入媒体闭包的工程名称" });
        Assert.Equal(original.QueryCatalogDigest, nameChanged.QueryCatalogDigest); Assert.Equal(original.FixedDependencyDigest, nameChanged.FixedDependencyDigest);
    }

    [Fact]
    public void QueryReferencesUseStrictUtf8OrderAndDoNotUseUtf16SurrogateOrdering()
    {
        var package = MediaQueryPackageFixture.Controller(m =>
        {
            m["mediaQueries"] = new JsonArray(MediaQueryPackageFixture.Query("\U00010000", 0), MediaQueryPackageFixture.Query("\uE000", 0));
            Export(m)["mediaQueries"] = new JsonArray(MediaQueryPackageFixture.Reference("\U00010000"), MediaQueryPackageFixture.Reference("\uE000"));
        });
        var plan = Plan(package); Assert.Equal(Utf8QueryOrder, plan.Dependencies.Select(dependency => dependency.Query.Id)); Oracle(plan);
    }

    [Fact]
    public void PublicCatalogAndPlanAreImmutableAndContainNoExecutableDefinitionOrRuntimeBinding()
    {
        Type[] values = [typeof(EffectPackageMediaQueryCatalog), typeof(EffectMediaQueryDefinition), typeof(EffectMediaQueryReference),
            typeof(EffectMediaWindow.Literal), typeof(EffectMediaWindow.Parameter), typeof(EffectMediaFallbackReference.AuthorBranch),
            typeof(EffectMediaFallbackReference.OutputBypass), typeof(ProjectMediaDependencyOwner), typeof(ProjectMediaDependency), typeof(ProjectMediaDependencyPlan)];
        foreach (Type type in values)
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.DoesNotContain(type.GetProperties(), property => property.SetMethod?.IsPublic == true);
            Assert.DoesNotContain(type.GetProperties(), property => property.PropertyType == typeof(EffectPackageDefinition));
        }
    }
}
