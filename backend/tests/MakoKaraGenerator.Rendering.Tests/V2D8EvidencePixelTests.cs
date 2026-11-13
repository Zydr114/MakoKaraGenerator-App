using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.TestSupport;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

// M13 A3 a：D8 核对时 C01 mark.affine、C02 SetShadow 与 group.opacity 缺 v2 渲染证据。
// 这里走真实 v3 包 → 工程实例 → 编译 → 纯求值准备帧 → 渲染，和静态编辑（或组透明度的整体减半）逐像素对照。
public sealed class V2D8EvidencePixelTests
{
    private const long TextTimeUs = 1_000_000, MarkTimeUs = 10_000_000;
    private const string Source = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record Probe(string Mode, string Node) : PreparedEffect;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => new Probe(source.Parameters.GetEnum("mode"), source.Parameters.GetText("node"));
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                var probe = (Probe)data;
                if (probe.Mode == "shadow")
                    foreach (var token in frame.Geometry)
                        output.SetShadow(token.WithRuby, TextState.Both, new ShadowValue(new ColorRgba(1, 0, 0, 1), .8, 60, 6, 4));
                else if (probe.Mode == "mark")
                    foreach (var mark in frame.MarkGeometry) output.AppendTransform(mark.Target, Affine2.Translation(8, -4));
                else
                    foreach (var group in frame.GroupGeometry)
                        if (group.Target.NodeId.Value == probe.Node) output.MultiplyOpacity(group.Target, .5);
            }
        }
        """;

    [Theory]
    [InlineData("shadow", false)] [InlineData("shadow", true)]
    [InlineData("mark", false)] [InlineData("mark", true)]
    [InlineData("group", false)] [InlineData("group", true)]
    public async Task 纯求值写入的阴影标记仿射与组透明度在CPU和Vulkan上等于静态对照(string mode, bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var source = mode == "mark" ? MarkProject() : TextProject();
        string root = source.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var package = Package();
        var instance = new ProjectEffectInstance
        {
            Id = "d8-" + mode, Name = mode, PackageId = package.PackageId, Revision = package.Revision, ExportId = "probe",
            Subscription = new("project"), Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(new { mode, node = root })),
        };
        var snapshot = ProjectSnapshot.FromProject(source with { Effects = new([package], [instance]) });
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")),
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(item => item.ControllerArtifact?.Diagnostics ?? [])));
        await using var session = await new PureFrameSourceFactory(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline())
            .CreateAsync(snapshot);
        long time = mode == "mark" ? MarkTimeUs : TextTimeUs;
        var prepared = await session.PrepareAsync(time, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(mode == "shadow" ? 1 : 0, prepared.TokenStyles.Length);
        Assert.Equal(mode == "mark" ? 3 : 0, prepared.MarkTransforms.Length);
        Assert.Equal(mode == "group" ? .5 : null, prepared.GroupOpacities.SingleOrDefault()?.Opacity.Factor);

        using var renderer = gpu ? new SkiaRenderPipeline(null, null, null, null, false, BaseMaterialRuntimeTests.Device(true), false) : new SkiaRenderPipeline();
        using var actual = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, TransparentBackground = true });
        using var plain = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(source), time) with { TransparentBackground = true });
        Assert.False(plain.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
        if (mode == "group")
        {
            // 全部歌词都在主分支根组里：隔离合成后乘 0.5，预乘像素整体减半（舍入差不超过 2）。
            byte[] before = plain.PixelMemory.ToArray(), after = actual.PixelMemory.ToArray(); int visible = 0;
            for (int i = 0; i < after.Length; i++)
            {
                Assert.True(Math.Abs(after[i] - before[i] * .5) <= 2, $"字节{i}: {before[i]} → {after[i]}");
                if (i % 4 == 3 && before[i] > 200) visible++;
            }
            Assert.True(visible > 100, "可见像素太少，无法比较。");
            return;
        }
        using var expected = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(StaticEdit(source, mode, prepared)), time) with { TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
    }

    private static Project StaticEdit(Project project, string mode, PreparedRenderFrame prepared)
    {
        if (mode == "shadow")
            return project with { LyricStyle = project.LyricStyle with { Shadow = ShadowSpec.Create(new(255, 0, 0, 255), .8f, 60, 6, 4) } };
        // 标记局部空间平移 (8,-4)：按标记自身旋转缩放映射到父组参考框里的位置比例（同 PreparedMarkFrameTests 仿射对照）。
        var tree = project.LyricPresentation; var group = tree.FindNode(tree.FindProgram("primary")!.LeadIns[0].IndicatorContainerId)!;
        var sequence = (CountdownSequenceContent)group.Content!;
        var reference = prepared.Scene.GroupGeometries.Single(item => item.Target.NodeId.Value == group.Id).ReferenceBounds;
        return project with { LyricPresentation = tree with { Nodes = [.. tree.Nodes.Select(node =>
        {
            if (!sequence.MarkNodeIds.Contains(node.Id)) return node;
            var offset = SceneMatrix.RotateScale(node.Placement.RotationDegrees, node.Placement.Scale).Map(new(8, -4));
            return node with { Placement = node.Placement with { Position = new(node.Placement.Position.X + offset.X / reference.Width,
                node.Placement.Position.Y + offset.Y / reference.Height) } };
        })] } };
    }

    private static Project TextProject() => Project.Create("D8 v2 证据", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 3_000_000),
        LyricsDocument.Create([LyricLine.Create("line", "AB", new(0, 3_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "AB", new(500_000, 2_000_000), TimingStatus.Edited)])]),
        lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 64, SungColor = RgbaColor.White,
            UnsungColor = new(120, 200, 255), Stroke = null, Shadow = null, Glow = null }) with { EnterAnimation = null };

    // 前奏标记带自身旋转与缩放，局部平移才能区分局部与父空间。
    private static Project MarkProject()
    {
        var project = LeadInFixture.Create(true, true); var tree = project.LyricPresentation;
        var sequence = (CountdownSequenceContent)tree.FindNode(tree.FindProgram("primary")!.LeadIns[0].IndicatorContainerId)!.Content!;
        return project with { LyricPresentation = tree with { Nodes = [.. tree.Nodes.Select(node => sequence.MarkNodeIds.Contains(node.Id)
            ? node with { Placement = node.Placement with { RotationDegrees = 7, Scale = .9 } } : node)] } };
    }

    private static EffectPackageContents Package()
    {
        var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(Source);
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion; manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile; manifest["packageId"] = "makokara.m13.d8-evidence";
        manifest["requires"] = new JsonArray("style.shadow", "mark.affine", "group.opacity");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        manifest["parameters"] = new JsonArray(
            new JsonObject { ["id"] = "mode", ["type"] = "enum", ["label"] = "模式", ["default"] = "shadow", ["values"] = new JsonArray(
                new JsonObject { ["value"] = "shadow", ["label"] = "阴影" }, new JsonObject { ["value"] = "mark", ["label"] = "标记" },
                new JsonObject { ["value"] = "group", ["label"] = "组" }) },
            new JsonObject { ["id"] = "node", ["type"] = "text", ["label"] = "目标组", ["default"] = "" });
        var export = manifest["exports"]![0]!; export["id"] = "probe"; export["parameters"] = new JsonArray("mode", "node");
        export["bindingKinds"] = new JsonArray("project");
        export["outputs"] = new JsonArray(Output("shadow", "StyleModulation", "tokenFamily"), Output("mark", "VisualTransform", "textMark"),
            Output("group", "StyleModulation", "group"));
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }
    private static JsonObject Output(string id, string kind, string target) => new()
    { ["id"] = id, ["kind"] = kind, ["target"] = target, ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray() };
}
