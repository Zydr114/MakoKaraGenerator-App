using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class MaterialUniformWriterTests
{
    private static readonly TokenFamilyTarget Target = new(new("token"), new("binding"));
    private static readonly ResourceCatalog Resources = new("package", "sha256:" + new string('a', 64), ["ink", "other"], [], []);
    private static readonly MaterialHandle Handle = Resources.GetMaterial("ink");
    private static readonly MaterialValue White = MaterialValue.Solid(new(1, 1, 1, 1));
    private static readonly StyleView Basis = TestStyles.View(White, White, new(2, White), null, .5);
    private static readonly MaterialValue Package = MaterialValue.PackageExport(Handle, ParameterValues.Empty);

    private static TextOutput Output(bool independent = true, params string[] capabilities)
    {
        var frame = V2PackageTestHelper.CreateTextFrame(independent: independent, resources: Resources,
            baseline: new([new(Target.TokenId, Basis)]));
        return new(in frame, [.. capabilities.Length == 0
            ? [CapabilityNames.StyleFill, CapabilityNames.StyleStroke, CapabilityNames.MaterialUniform]
            : capabilities]);
    }

    private sealed class AcceptShaders : IPackageShaderValidator
    { public void Validate(ImmutableDictionary<string, ShaderProgramDefinition> programs, CancellationToken cancellationToken = default) { } }
    private sealed class AcceptGraphs : IPackageImageGraphValidator
    { public void Validate(ImmutableDictionary<string, EffectPackageImageGraph> graphs, CancellationToken cancellationToken = default) { } }
    private sealed class AcceptMaterials : IPackageMaterialValidator
    { public void Validate(ImmutableArray<EffectMaterialDefinition> materials, CancellationToken cancellationToken = default) { } }

    private static async Task<PreparedRenderFrame> PrepareHostAsync(string evaluateBody, string? secondEvaluateBody = null)
    {
        string source = $$"""
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class FirstProbe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect prepared, TextOutput output)
                {
                    var handle = frame.Resources.GetMaterial("ink");
                    var pkg = MaterialValue.PackageExport(handle, ParameterValues.Empty);
                    var target = new TextTarget.Family(frame.Presentations.TokensInBinding[0].WithRuby);
                    {{evaluateBody}}
                }
            }
            public sealed class SecondProbe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect prepared, TextOutput output)
                {
                    var handle = frame.Resources.GetMaterial("ink");
                    var pkg = MaterialValue.PackageExport(handle, ParameterValues.Empty);
                    var target = new TextTarget.Family(frame.Presentations.TokensInBinding[0].WithRuby);
                    {{secondEvaluateBody ?? ""}}
                }
            }
            """;
        var program = new JsonObject
        {
            ["programVersion"] = 1,
            ["source"] = "shaders/tint.sksl",
            ["profile"] = EffectPackageProfiles.ShaderProgramProfile(EffectPackageProfiles.V2),
            ["uniforms"] = new JsonArray(new JsonObject { ["name"] = "amount", ["type"] = "float" }),
            ["children"] = new JsonArray(new JsonObject { ["name"] = "src", ["type"] = "color" }),
        };
        var graph = new JsonObject
        {
            ["graphVersion"] = ImageGraphDefinition.CurrentVersion,
            ["id"] = "mat",
            ["inputs"] = new JsonArray(new JsonObject
            {
                ["id"] = "original", ["source"] = "TargetColor", ["type"] = "color",
                ["availability"] = "required", ["backgroundRequirement"] = "none", ["space"] = "canvas",
                ["capture"] = new JsonObject
                {
                    ["includeRuby"] = true, ["includeStroke"] = true, ["includeShadow"] = true,
                    ["includeDescendantGroups"] = true, ["includeOwnedOverlay"] = false, ["region"] = "fillAndStroke",
                },
            }),
            ["parameters"] = new JsonArray(),
            ["nodes"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "a", ["op"] = "RuntimeShader", ["program"] = "shaders/tint.program.json", ["outputType"] = "color",
                    ["uniforms"] = new JsonObject { ["amount"] = 0.1 }, ["children"] = new JsonObject { ["src"] = "input:original" },
                    ["bounds"] = new JsonObject { ["expandPx"] = 0, ["maxExpandPx"] = 0, ["maxSampleOffsetPx"] = 0 },
                    ["edge"] = "transparent",
                },
                new JsonObject
                {
                    ["id"] = "b", ["op"] = "RuntimeShader", ["program"] = "shaders/tint.program.json", ["outputType"] = "color",
                    ["uniforms"] = new JsonObject { ["amount"] = 0.2 }, ["children"] = new JsonObject { ["src"] = "node:a" },
                    ["bounds"] = new JsonObject { ["expandPx"] = 0, ["maxExpandPx"] = 0, ["maxSampleOffsetPx"] = 0 },
                    ["edge"] = "transparent",
                }),
            ["output"] = "node:b",
            ["anchorMode"] = "ColorOnly",
            ["bounds"] = new JsonObject { ["space"] = "canvas", ["source"] = "targetRegion", ["maxExpandPx"] = 0 },
            ["composite"] = new JsonObject { ["mode"] = "sourceOver" },
        };
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["src/Probe.cs"] = System.Text.Encoding.UTF8.GetBytes(source),
            ["shaders/tint.sksl"] = "uniform float amount; uniform shader src; half4 main(float2 coord) { return src.eval(coord) * amount; }"u8.ToArray(),
            ["shaders/tint.program.json"] = V2PackageTestHelper.Bytes(program),
            ["graphs/mat.graph.json"] = V2PackageTestHelper.Bytes(graph),
        };
        var manifest = V2PackageTestHelper.Manifest(files,
            [CapabilityNames.StyleFill, CapabilityNames.StyleStroke, CapabilityNames.MaterialUniform, CapabilityNames.ImageGraph], "FirstProbe");
        foreach (var entry in manifest["files"]!.AsArray())
        {
            string path = entry!["path"]!.GetValue<string>();
            if (path.EndsWith(".sksl", StringComparison.Ordinal)) entry["role"] = "shader";
            else if (path.EndsWith(".program.json", StringComparison.Ordinal)) entry["role"] = "shaderProgram";
            else if (path.EndsWith(".graph.json", StringComparison.Ordinal)) entry["role"] = "graph";
        }
        manifest["parameters"] = new JsonArray();
        var export1 = manifest["exports"]![0]!.AsObject();
        export1["id"] = "first"; export1["name"] = "first"; export1["parameters"] = new JsonArray();
        export1["bindingKinds"] = new JsonArray("project");
        export1["outputs"]![0]!["kind"] = "StyleModulation";
        export1["outputs"]![0]!["target"] = "tokenFamily";
        var export2 = export1.DeepClone().AsObject();
        export2["id"] = "second"; export2["name"] = "second"; export2["entryType"] = "SecondProbe";
        static JsonObject MaterialExport(string id) => new()
        {
            ["id"] = id, ["kind"] = "material", ["name"] = id, ["graph"] = "graphs/mat.graph.json",
            ["parameters"] = new JsonArray(), ["regions"] = new JsonArray("fill", "stroke"), ["coverageMode"] = "masked",
            ["fallback"] = new JsonObject { ["kind"] = "solid", ["color"] = "#FFFFFFFF" },
            ["outputs"] = new JsonArray(new JsonObject
            {
                ["id"] = "color", ["kind"] = "ImageGraph", ["target"] = "region",
                ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray(),
                ["bypass"] = new JsonObject { ["kind"] = "materialFallback", ["mapping"] = "identity" },
            }),
        };
        manifest["exports"]!.AsArray().Add(export2);
        manifest["exports"]!.AsArray().Add(MaterialExport("ink"));
        manifest["exports"]!.AsArray().Add(MaterialExport("other"));
        var package = await CompiledEffectPackage.CompileAsync(
            EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(
                V2PackageTestHelper.Bytes(manifest), V2PackageTestHelper.Inputs(files))),
            compiler, null, new AcceptShaders(), new AcceptGraphs(), new AcceptMaterials());
        Assert.True(package.Artifact.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var snapshot = ProjectSnapshot.FromProject(Project.Create("uniforms", lyrics: LyricsDocument.Create([
            LyricLine.Create("line", "甲", new(0, 100_000), TimingStatus.Edited,
                [LyricToken.Create("token", "甲", new(0, 100_000), TimingStatus.Edited)])])))
            with { EffectInputsFingerprint = "uniforms" };
        var first = package.CreateEffect("first", new("first"), new EffectBinding.Project(), 0);
        var effects = secondEvaluateBody is null
            ? ImmutableArray.Create(first)
            : ImmutableArray.Create(first, package.CreateEffect("second", new("second"), new EffectBinding.Project(), 0));
        var factory = new PureFrameSourceFactory(effects, "uniforms", static () => new SimpleGeometry());
        await using var session = await factory.CreateAsync(snapshot);
        return await session.PrepareAsync(0, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
    }

    private sealed class SimpleGeometry : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        {
            var scene = LyricSceneSampler.SampleFrame(snapshot, timeUs);
            var g = new GeometrySample { AdvanceBounds = new(0, 0, 20, 20), Baseline = new(0, 15), BaseWorldMatrix = Affine2.Identity, CanTransformIndependently = true };
            return scene with
            {
                TokenGeometries = [.. FrameProjection.Tokens(scene).Select(token => new TokenGeometrySample
                {
                    BranchId = token.BranchId, LineId = token.LineId, WithRuby = token.WithRuby, BaseOnly = token.BaseOnly,
                    BaseGeometry = g, FamilyGeometry = g,
                })],
            };
        }
    }

    [Theory]
    [InlineData(MaterialSlot.UnsungFill)] [InlineData(MaterialSlot.SungFill)] [InlineData(MaterialSlot.UnsungStroke)] [InlineData(MaterialSlot.SungStroke)]
    public async Task 显式部位只修改自身且每个节点同名值分别保存(MaterialSlot slot)
    {
        string setLine = slot switch
        {
            MaterialSlot.UnsungFill => "output.SetFill(target, TextState.Unsung, pkg);",
            MaterialSlot.SungFill => "output.SetFill(target, TextState.Sung, pkg);",
            MaterialSlot.UnsungStroke => "output.SetStrokes(target, TextState.Unsung, new StrokeValue(4, pkg));",
            _ => "output.SetStrokes(target, TextState.Sung, new StrokeValue(4, pkg));",
        };
        var frame = await PrepareHostAsync($$"""
            {{setLine}}
            output.SetMaterialUniform(target, MaterialSlot.{{slot}}, handle, "a", "amount", UniformValue.Float(.25));
            output.SetMaterialUniform(target, MaterialSlot.{{slot}}, handle, "b", "amount", UniformValue.Float(.5));
            output.SetMaterialUniform(target, MaterialSlot.{{slot}}, handle, "a", "amount", UniformValue.Float(.75));
            """);
        var style = Assert.Single(frame.TokenStyles).Update.Apply(Basis);
        var material = Assert.IsType<MaterialValue.PackageMaterial>(style.Material(slot));
        Assert.Equal(.75, Assert.Single(material.Uniforms.Values[("a", "amount")].Numbers));
        Assert.Equal(.5, Assert.Single(material.Uniforms.Values[("b", "amount")].Numbers));
        Assert.Equal(2, material.Uniforms.Values.Count); Assert.Empty(Assert.IsType<MaterialValue.PackageMaterial>(Package).Uniforms.Values);
        Assert.Equal(.5, style.Opacity);
        Assert.Equal(slot == MaterialSlot.UnsungStroke ? 4 : 2, style.Unsung.InnermostStroke!.WidthPx);
        Assert.Equal(slot == MaterialSlot.SungStroke ? 4 : 2, style.Sung.InnermostStroke!.WidthPx);
        if (slot != MaterialSlot.UnsungFill) Assert.Same(White, style.Unsung.Fill);
        if (slot != MaterialSlot.SungFill) Assert.Same(White, style.Sung.Fill);
    }

    [Fact]
    public async Task 再次Set清除旧覆盖而后续uniform再次建立新覆盖()
    {
        var frame = await PrepareHostAsync("""
            output.SetFill(target, TextState.Sung, pkg);
            output.SetMaterialUniform(target, MaterialSlot.SungFill, handle, "a", "amount", UniformValue.Float(.2));
            output.SetFill(target, TextState.Sung, pkg);
            output.SetMaterialUniform(target, MaterialSlot.SungFill, handle, "b", "amount", UniformValue.Float(.7));
            """);
        var style = Assert.Single(frame.TokenStyles).Update.Apply(Basis);
        var material = Assert.IsType<MaterialValue.PackageMaterial>(style.Sung.Fill);
        Assert.Equal(("b", "amount"), Assert.Single(material.Uniforms.Values).Key);
    }

    [Fact]
    public async Task 无包材质时写uniform在合并阶段拒绝()
    {
        var missing = await Assert.ThrowsAsync<EffectFailureException>(() => PrepareHostAsync("""
            output.SetMaterialUniform(target, MaterialSlot.SungFill, handle, "a", "amount", UniformValue.Float(.2));
            """));
        Assert.Equal("MaterialUniformIdentityMismatch", missing.Code);

        var solid = await Assert.ThrowsAsync<EffectFailureException>(() => PrepareHostAsync("""
            output.SetFill(target, TextState.Sung, MaterialValue.Solid(new(1, 1, 1, 1)));
            output.SetMaterialUniform(target, MaterialSlot.SungFill, handle, "a", "amount", UniformValue.Float(.2));
            """));
        Assert.Equal("MaterialUniformIdentityMismatch", solid.Code);
    }

    [Fact]
    public async Task 同包另一个合法材质句柄不能冒充当前部位材质()
    {
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => PrepareHostAsync("""
            output.SetFill(target, TextState.Sung, pkg);
            output.SetMaterialUniform(target, MaterialSlot.SungFill, frame.Resources.GetMaterial("other"), "a", "amount", UniformValue.Float(.5));
            """));
        Assert.Equal("MaterialUniformIdentityMismatch", error.Code);
    }

    [Fact]
    public async Task 独立求值的相同操作和值按内容相等而顺序和值变化不相等()
    {
        async Task<StyleView> Build(double value)
        {
            var frame = await PrepareHostAsync($$"""
                output.SetFill(target, TextState.Sung, pkg);
                output.SetMaterialUniform(target, MaterialSlot.SungFill, handle, "a", "amount", UniformValue.Float({{value}}));
                """);
            return Assert.Single(frame.TokenStyles).Update.Apply(Basis);
        }
        var first = await Build(.5); var replay = await Build(.5); var changed = await Build(.7);
        Assert.Equal(first, replay); Assert.Equal(first.GetHashCode(), replay.GetHashCode()); Assert.NotEqual(first, changed);
    }

    [Fact]
    public async Task uniform沿调用实例顺序组合且不修改参数真值()
    {
        var frameA = await PrepareHostAsync("""
            output.SetFill(target, TextState.Sung, pkg);
            """, """
            output.SetMaterialUniform(target, MaterialSlot.SungFill, handle, "a", "amount", UniformValue.Float(.9));
            """);
        var frameB = await PrepareHostAsync("""
            output.SetFill(target, TextState.Sung, pkg);
            """, """
            output.SetMaterialUniform(target, MaterialSlot.SungFill, handle, "a", "amount", UniformValue.Float(.9));
            """);
        var a = Assert.IsType<MaterialValue.PackageMaterial>(Assert.Single(frameA.TokenStyles).Update.Apply(Basis).Sung.Fill);
        var b = Assert.IsType<MaterialValue.PackageMaterial>(Assert.Single(frameB.TokenStyles).Update.Apply(Basis).Sung.Fill);
        Assert.Equal(a, b); Assert.Equal(a.GetHashCode(), b.GetHashCode()); Assert.Same(ParameterValues.Empty, a.Parameters);
        Assert.Empty(Assert.IsType<MaterialValue.PackageMaterial>(Package).Uniforms.Values);
    }

    [Fact]
    public void 写入仍须真实目标固定资源写权字簇独立及有效阶段()
    {
        void Put(TextOutput target, MaterialHandle handle, TokenFamilyTarget token) => target.SetMaterialUniform(
            new TextTarget.Family(token), MaterialSlot.SungFill, handle, "a", "amount", UniformValue.Float(.2));
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Put(Output(capabilities: [CapabilityNames.TokenAffine]), Handle, Target)).Code);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => Put(Output(independent: false), Handle, Target)).Code);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => Put(Output(), Handle, Target with { BindingId = new("wrong") })).Code);
        var other = new ResourceCatalog("package", "sha256:" + new string('b', 64), ["ink"], [], []);
        Assert.Equal("InvalidMaterialResource", Assert.Throws<EffectOutputException>(() => Put(Output(), other.GetMaterial("ink"), Target)).Code);
        Assert.Equal("InvalidMaterialSlot", Assert.Throws<EffectOutputException>(() => Output().SetMaterialUniform(
            new TextTarget.Family(Target), (MaterialSlot)99, Handle, "a", "amount", UniformValue.Float(.2))).Code);
        var completed = Output(); completed.Complete();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => Put(completed, Handle, Target)).Code);
    }
}
