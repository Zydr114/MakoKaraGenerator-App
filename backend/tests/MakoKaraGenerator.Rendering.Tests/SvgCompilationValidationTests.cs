using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SvgCompilationValidationTests
{
    [Fact]
    public void 没有控制器引用的全部SVG仍必须有验证器且原始失败不发布部分结果()
    {
        var contents = Package(); var resources = EffectPackageSvgResources.Read(contents);
        Assert.Equal("PackageSvgValidatorMissing", Assert.Throws<EffectPackageException>(() =>
            PackageSvgValidation.Validate(contents, resources, null)).Code);
        var original = new EffectPackageException("PackageSvgSyntaxInvalid", "assets/atlas.svg", "unused resource failed");
        var validator = new Validator((actualContents, actualResources, _) =>
        {
            Assert.Same(contents, actualContents); Assert.Same(resources, actualResources);
            Assert.Collection(actualResources.ById.Keys.Order(StringComparer.Ordinal),
                id => Assert.Equal("atlas", id), id => Assert.Equal("vector", id));
            throw original;
        });
        Assert.Same(original, Assert.Throws<EffectPackageException>(() =>
            PackageSvgValidation.Validate(contents, resources, validator)));
        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public void 完整验证结果按身份覆盖全部资源和未使用图片且不依赖数组顺序()
    {
        var contents = Package(); var resources = EffectPackageSvgResources.Read(contents); var inspection = Inspect(contents, resources);
        inspection = inspection with { Resources = [.. inspection.Resources.Reverse().Select(resource => resource with
        { Dependencies = [.. resource.Dependencies.Reverse()], AtlasPlans = [.. resource.AtlasPlans.Reverse()] })] };
        var validator = new Validator((actualContents, actualResources, _) =>
        { Assert.Same(contents, actualContents); Assert.Same(resources, actualResources); return inspection; });
        PackageSvgValidation.Validate(contents, resources, validator);
        Assert.Equal(1, validator.Calls);
        Assert.Contains(inspection.Resources.Single(resource => resource.Id == "vector").Dependencies,
            dependency => dependency.Path == "assets/unused.png" && dependency.Role == "image");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("package")]
    [InlineData("revision")]
    [InlineData("profile")]
    [InlineData("rules")]
    [InlineData("prepare")]
    [InlineData("default")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("null-resource")]
    public void 验证器不能返回外来身份或部分资源目录(string change)
    {
        var contents = Package(); var resources = EffectPackageSvgResources.Read(contents); var valid = Inspect(contents, resources);
        var altered = change switch
        {
            "null" => null!, "package" => valid with { PackageId = "another.package" },
            "revision" => valid with { Revision = new string('0', 64) }, "profile" => valid with { Profile = "another-profile" },
            "rules" => valid with { RulesVersion = " " }, "prepare" => valid with { EstimatedPrepareBytes = -1 },
            "default" => valid with { Resources = default }, "missing" => valid with { Resources = [valid.Resources[0]] },
            "extra" => valid with { Resources = [.. valid.Resources, valid.Resources[0] with { Id = "extra" }] },
            "duplicate" => valid with { Resources = [valid.Resources[0], valid.Resources[0]] },
            "null-resource" => valid with { Resources = [valid.Resources[0], null!] },
            _ => throw new InvalidOperationException(),
        };
        AssertInvalid(contents, resources, altered);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("viewport")]
    [InlineData("source")]
    [InlineData("source-hash")]
    [InlineData("dependencies-default")]
    [InlineData("dependency-missing")]
    [InlineData("dependency-extra")]
    [InlineData("dependency-duplicate")]
    [InlineData("dependency-hash")]
    [InlineData("dependency-role")]
    [InlineData("dependency-null")]
    [InlineData("vector-atlas")]
    public void 验证事实必须匹配真实descriptor和精确原字节依赖(string change)
    {
        var contents = Package(); var resources = EffectPackageSvgResources.Read(contents); var valid = Inspect(contents, resources);
        var vector = valid.Resources.Single(resource => resource.Id == "vector"); var dependency = vector.Dependencies[0];
        var altered = change switch
        {
            "mode" => vector with { Mode = SvgResourceMode.Atlas },
            "viewport" => vector with { Viewport = valid.Resources.Single(resource => resource.Id == "atlas").Viewport },
            "source" => vector with { SourcePath = "assets/atlas.svg" }, "source-hash" => vector with { SourceSha256 = new string('0', 64) },
            "dependencies-default" => vector with { Dependencies = default },
            "dependency-missing" => vector with { Dependencies = [.. vector.Dependencies.Skip(1)] },
            "dependency-extra" => vector with { Dependencies = [.. vector.Dependencies, dependency with { Path = "assets/atlas.svg" }] },
            "dependency-duplicate" => vector with { Dependencies = vector.Dependencies.SetItem(1, dependency) },
            "dependency-hash" => vector with { Dependencies = vector.Dependencies.SetItem(0, dependency with { Sha256 = new string('0', 64) }) },
            "dependency-role" => vector with { Dependencies = vector.Dependencies.SetItem(0, dependency with { Role = "asset" }) },
            "dependency-null" => vector with { Dependencies = vector.Dependencies.SetItem(0, null!) },
            "vector-atlas" => vector with { AtlasPlans = [valid.Resources.Single(resource => resource.Id == "atlas").AtlasPlans[0]] },
            _ => throw new InvalidOperationException(),
        };
        AssertInvalid(contents, resources, valid with { Resources = [.. valid.Resources.Select(resource => resource.Id == "vector" ? altered : resource)] });
    }

    [Theory]
    [InlineData("default")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("variant")]
    [InlineData("levels")]
    [InlineData("base-size")]
    [InlineData("null")]
    public void atlas验证结果精确覆盖所有固定档位且基础页包含描述padding(string change)
    {
        var contents = Package(); var resources = EffectPackageSvgResources.Read(contents); var valid = Inspect(contents, resources);
        var atlas = valid.Resources.Single(resource => resource.Id == "atlas"); var plan = atlas.AtlasPlans[0];
        var altered = change switch
        {
            "default" => atlas with { AtlasPlans = default }, "missing" => atlas with { AtlasPlans = [plan] },
            "extra" => atlas with { AtlasPlans = [.. atlas.AtlasPlans, plan with { VariantId = "extra" }] },
            "duplicate" => atlas with { AtlasPlans = [plan, plan] },
            "variant" => atlas with { AtlasPlans = atlas.AtlasPlans.SetItem(0, plan with { VariantId = "unknown" }) },
            "levels" => atlas with { AtlasPlans = atlas.AtlasPlans.SetItem(0, plan with { Levels = [] }) },
            "base-size" => atlas with { AtlasPlans = atlas.AtlasPlans.SetItem(0, plan with
                { Levels = plan.Levels.SetItem(0, plan.Levels[0] with { Width = plan.Levels[0].Width - 1 }) }) },
            "null" => atlas with { AtlasPlans = atlas.AtlasPlans.SetItem(0, null!) },
            _ => throw new InvalidOperationException(),
        };
        AssertInvalid(contents, resources, valid with { Resources = [.. valid.Resources.Select(resource => resource.Id == "atlas" ? altered : resource)] });
    }

    [Fact]
    public void 固定描述不能与另一个内容修订混用且取消前后都保留取消语义()
    {
        var contents = Package(); var resources = EffectPackageSvgResources.Read(contents); var changed = Package("<rect width='2' height='1'/>");
        var validator = new Validator((_, _, _) => Inspect(contents, resources));
        Assert.Equal("PackageSvgResourceOwnershipMismatch", Assert.Throws<EffectPackageException>(() =>
            PackageSvgValidation.Validate(changed, resources, validator)).Code);
        Assert.Equal(0, validator.Calls);
        using var before = new CancellationTokenSource(); before.Cancel();
        var early = Assert.Throws<OperationCanceledException>(() =>
            PackageSvgValidation.Validate(contents, resources, validator, before.Token));
        Assert.Equal(before.Token, early.CancellationToken); Assert.Equal(0, validator.Calls);
        using var after = new CancellationTokenSource();
        var canceling = new Validator((_, _, token) =>
        { Assert.Equal(after.Token, token); after.Cancel(); return Inspect(contents, resources); });
        var late = Assert.Throws<OperationCanceledException>(() =>
            PackageSvgValidation.Validate(contents, resources, canceling, after.Token));
        Assert.Equal(after.Token, late.CancellationToken); Assert.Equal(1, canceling.Calls);
    }

    [Fact]
    public async Task 没有SVG的原有公开重载与新增项目入口保持兼容且不调用SVG验证器()
    {
        var definition = EffectPackageFixture.WordEmphasis();
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var validator = new Validator((_, _, _) => throw new InvalidOperationException("No SVG validation expected."));
        var previous = await CompiledEffectPackage.CompileAsync(definition, compiler, null, null, null, null);
        var current = await CompiledEffectPackage.CompileAsync(definition, compiler, null, null, null, null, validator);
        Assert.True(previous.Succeeded); Assert.True(current.Succeeded);
        Assert.Equal(previous.Resources.PackageId, current.Resources.PackageId); Assert.Equal(previous.Resources.Revision, current.Resources.Revision);
        var project = Project.Create("No SVG", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])) with
        {
            Effects = new([definition.Contents], [new()
            { Id = "compatible", Name = "Existing package", PackageId = definition.Contents.PackageId,
                Revision = definition.Contents.Revision, ExportId = "word-emphasis" }]),
        };
        var fromProject = await CompiledProjectEffects.CompileAsync(project, compiler, null, null, null, null, validator);
        var fromSnapshot = await CompiledProjectEffects.CompileAsync(ProjectSnapshot.FromProject(project), compiler, null, null, null, null, validator);
        var fromEffects = await CompiledProjectEffects.CompileAsync(project.Effects, compiler, null, null, null, null, validator);
        Assert.True(fromProject.Succeeded); Assert.True(fromSnapshot.Succeeded); Assert.True(fromEffects.Succeeded);
        Assert.Single(fromProject.Packages); Assert.Single(fromSnapshot.Packages); Assert.Single(fromEffects.Packages);
        Assert.Equal(0, validator.Calls);
    }

    private static void AssertInvalid(EffectPackageContents contents, EffectPackageSvgResources resources, PackageSvgInspection inspection)
    {
        var validator = new Validator((_, _, _) => inspection);
        Assert.Equal("PackageSvgInspectionInvalid", Assert.Throws<EffectPackageException>(() =>
            PackageSvgValidation.Validate(contents, resources, validator)).Code);
        Assert.Equal(1, validator.Calls);
    }

    // These tests exercise the compilation boundary's inspection checks. Actual XML and native image validation belongs to Rendering.Tests.
    private static PackageSvgInspection Inspect(EffectPackageContents contents, EffectPackageSvgResources resources)
        => new(contents.PackageId, contents.Revision, EffectPackageSvgResources.StaticProfile, "test-rules/1",
            [.. resources.ById.Values.OrderBy(resource => resource.Id, StringComparer.Ordinal).Select(resource => new SvgResourceInspection(
                resource.Id, resource.Mode, resource.Viewport, resource.SourcePath, contents.Files.Single(file => file.Path == resource.SourcePath).Sha256,
                [.. resource.Images.Append(resource.SourcePath).Append(resource.DescriptorPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                    .Select(path => contents.Files.Single(file => file.Path == path)).Select(file => new SvgDependencyInspection(file.Path, file.Role, file.Sha256))],
                1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, [.. resource.Variants.Select(variant => Plan(variant, resource.PaddingPx))]))], 1);

    private static SvgAtlasPlan Plan(SvgAtlasVariant variant, int padding)
    {
        var levels = ImmutableArray.CreateBuilder<SvgMipLevelPlan>(); long total = 0;
        int width = variant.Width + 2 * padding, height = variant.Height + 2 * padding;
        while (true)
        {
            long bytes = (long)width * height * 4; total += bytes; levels.Add(new(width, height, bytes));
            if (width == 1 && height == 1) break;
            width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
        }
        return new(variant.Id, levels.ToImmutable(), total);
    }

    private static EffectPackageContents Package(string body = "<rect width='1' height='1'/>")
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["assets/vector.svg"] = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" + body + "</svg>"),
            ["assets/atlas.svg"] = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100'>" + body + "</svg>"),
            ["assets/unused.png"] = [1, 2],
        };
        foreach (bool atlas in new[] { false, true })
        {
            string id = atlas ? "atlas" : "vector";
            JsonObject descriptor = new()
            {
                ["svgResourceVersion"] = 1, ["id"] = id, ["source"] = "assets/" + id + ".svg",
                ["profile"] = EffectPackageSvgResources.StaticProfile, ["mode"] = id,
                ["viewport"] = new JsonObject { ["width"] = atlas ? 200 : 100, ["height"] = 100 },
                ["fonts"] = new JsonArray(), ["images"] = atlas ? new JsonArray() : new JsonArray("assets/unused.png"),
            };
            if (atlas) descriptor["atlas"] = new JsonObject
            {
                ["variants"] = new JsonArray(new JsonObject { ["id"] = "small", ["width"] = 15, ["height"] = 7 },
                    new JsonObject { ["id"] = "large", ["width"] = 30, ["height"] = 14 }),
                ["paddingPx"] = 2, ["mipmaps"] = true,
            };
            files.Add("resources/" + id + ".json", Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
        }
        JsonArray entries = [];
        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal)) entries.Add(new JsonObject
        {
            ["path"] = pair.Key, ["role"] = pair.Key.EndsWith(".svg", StringComparison.Ordinal) ? "svg"
                : pair.Key.EndsWith(".json", StringComparison.Ordinal) ? "svgResource" : "image",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
        });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile, ["packageId"] = "svg.compilation.tests", ["name"] = "SVG compilation", ["version"] = "1.0.0",
            ["requires"] = new JsonArray(EffectPackageSvgResources.VectorCapability, EffectPackageSvgResources.AtlasCapability),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 }, ["emitters"] = new JsonArray(),
            ["files"] = entries, ["parameters"] = new JsonArray(), ["exports"] = new JsonArray(),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
    }

    private sealed class Validator(Func<EffectPackageContents, EffectPackageSvgResources, CancellationToken, PackageSvgInspection> validate) : IPackageSvgValidator
    {
        internal int Calls { get; private set; }
        public PackageSvgInspection Validate(EffectPackageContents contents, EffectPackageSvgResources resources, CancellationToken cancellationToken = default)
        { Calls++; return validate(contents, resources, cancellationToken); }
    }
}
