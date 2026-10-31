using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SceneTargetContractTests
{
    private static readonly TextMarkTarget Target = new(new("mark"), new("cue"), new("binding"));
    private static readonly MaterialValue White = MaterialValue.Solid(new(1, 1, 1, 1));
    private static readonly MaterialValue Blue = MaterialValue.Solid(new(0, 0, 1, 1));
    private static readonly StyleView Appearance = new(White, White, new(2, White), new(new(0, 0, 0, 1)), .4);
    private static readonly BaselineStyles Baseline = new([], [new(new("line"), Appearance)], [new(Target.NodeId, Appearance)],
        [new(new("group"), new(1))]);
    private static FrameWriter Writer(EffectCapabilities capabilities = EffectCapabilities.Style | EffectCapabilities.Transforms)
        => new([], capabilities, Baseline, markGeometry: [new(Target, new() { AdvanceBounds = new(10, 20, 50, 60),
            Baseline = new(10, 55), BaseWorldMatrix = Affine2.Identity, InkBounds = new(12, 23, 48, 58), CanTransformIndependently = true })]);
    [Fact]
    public void 组没有假文字材质且行标记基线按身份独立读取()
    {
        Assert.Equal(Appearance, Baseline.Get(new LineId("line")));
        Assert.Equal(Appearance, Baseline.Get(new LineTarget(new("line"), new("binding"))));
        Assert.Equal(Appearance, Baseline.Get(Target));
        Assert.Equal(1, Baseline.Get(new GroupTarget(new("group"))).Opacity);
        Assert.Single(typeof(GroupStyleView).GetProperties());
        Assert.Throws<EffectOutputException>(() => Baseline.Get(new GroupTarget(Target.NodeId)));
        Assert.Throws<EffectOutputException>(() => Baseline.Get(new TextMarkTarget(new("group"), Target.CueId, Target.BindingId)));
        Assert.Throws<ArgumentException>(() => Baseline.Get(new LineTarget(new("line"), default)));
        Assert.Throws<ArgumentException>(() => Baseline.Get(Target with { CueId = default }));
    }
    [Fact]
    public void 标记writer检查完整提示身份能力和阶段()
    {
        FrameWriter writer = Writer();
        foreach (var invalid in new[] { Target with { CueId = new("other") }, Target with { BindingId = new("other") }, Target with { NodeId = new("other") } })
        {
            Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.Style.SetSungMaterial(invalid, Blue)).Code);
            Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(invalid, Affine2.Identity)).Code);
        }
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Writer(EffectCapabilities.None).Style.SetUnsungMaterial(Target, Blue)).Code);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Writer(EffectCapabilities.Style).Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
        writer.Style.SetSungMaterial(Target, Blue); writer.Complete();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Style.SetShadow(Target, null)).Code);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
    }
    [Fact]
    public void 标记两态和装饰按顺序覆盖且透明度最后合并()
    {
        FrameWriter writer = Writer();
        writer.Style.SetSungMaterial(Target, Blue); writer.Style.SetSungMaterial(Target, Baseline.Get(Target).SungMaterial);
        writer.Style.SetUnsungMaterial(Target, Blue); writer.Style.SetStroke(Target, null); writer.Style.SetShadow(Target, null);
        writer.Style.MultiplyOpacity(Target, 3); writer.Style.MultiplyOpacity(Target, .5);
        var result = Assert.Single(writer.Complete().MarkStyles).Update.Apply(Baseline.Get(Target));
        Assert.Equal(White, result.SungMaterial); Assert.Equal(Blue, result.UnsungMaterial);
        Assert.Null(result.Stroke); Assert.Null(result.Shadow); Assert.Equal(.6, result.Opacity, 12);
        Assert.Equal(.4, Baseline.Get(Target).Opacity);
    }
    [Fact]
    public void 标记仿射不接受零矩阵且按所属逻辑空间顺序组合()
    {
        FrameWriter writer = Writer();
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, default)).Code);
        writer.Transforms.ApplyAffine(Target, Affine2.Translation(10, 20));
        writer.Transforms.ApplyAffine(Target, Affine2.ScaleAt(2, default));
        Assert.Equal(new Point2(12, 24), Assert.Single(writer.Complete().MarkTransforms).LocalMatrix.Map(new(1, 2)));
        FrameWriter neutral = Writer(); neutral.Transforms.ApplyAffine(Target, Affine2.Identity);
        neutral.Transforms.ApplyAffine(Target, Affine2.Translation(10, 0)); neutral.Transforms.ApplyAffine(Target, Affine2.Translation(-10, 0));
        Assert.Empty(neutral.Complete().MarkTransforms);
    }
    [Fact]
    public void 标记材质严格校验固定资源修订不当作已经执行shader()
    {
        var handle = new ResourceCatalog("package", "sha256:" + new string('a', 64), ["glass"], [], []).GetMaterial("glass");
        Assert.Equal("InvalidMaterialResource", Assert.Throws<EffectOutputException>(() => Writer().Style.SetUnsungMaterial(Target,
            MaterialValue.PackageExport(handle, ParameterValues.Empty))).Code);
    }
    [Fact]
    public void 完整基线包含不可见行和禁用提示但源数据保持原样()
    {
        var project = LeadInFixture.Create(true, true); var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        project = project with { LyricPresentation = tree with { Programs = tree.Programs.Replace(program, program with
            { LeadIns = [.. program.LeadIns.Select(cue => cue with { Enabled = false })] }) } };
        var snapshot = ProjectSnapshot.FromProject(project); var baseline = StaticStyleProjection.CreateBaseline(snapshot);
        Assert.Equal(StaticStyleProjection.View(StaticStyleProjection.LineStyles(snapshot)[new("chorus")]), baseline.Get(new LineId("chorus")));
        var cue = program.LeadIns[0]; var node = tree.FindNode(cue.IndicatorContainerId)!;
        var marks = ((CountdownSequenceContent)node.Content!).MarkNodeIds;
        Assert.All(marks, mark => Assert.Equal(StaticStyleProjection.View(StaticStyleProjection.MarkStyles(snapshot)[new(mark)]),
            baseline.Get(new TextMarkTarget(new(mark), new(cue.Id), new("not-a-current-binding")))));
        Assert.Empty(FrameProjection.Marks(LyricSceneSampler.SampleFrame(snapshot, 9_500_000), SceneProjection.Create(snapshot, ContentProjection.Create(snapshot))));
        Assert.Same(project.Lyrics, snapshot.Lyrics); Assert.Same(project.LyricPresentation, snapshot.LyricPresentation);
    }
    [Fact]
    public void 行基线继承工程分支行但不读取元素覆盖且无元素行也可读取()
    {
        var project = LeadInFixture.Create(); var branch = project.Lyrics.Branches[0]; var line = branch.Lines[0];
        var token = line.Tokens[0] with { StyleOverride = new() { UnsungColor = new(0, 0, 255) } };
        line = line with { StyleOverride = new() { UnsungColor = new(255, 0, 0) }, Tokens = line.Tokens.SetItem(0, token) };
        branch = branch with { StyleOverride = new() { SungColor = new(0, 255, 0), Opacity = .7f },
            Lines = branch.Lines.SetItem(0, line).Add(LyricLine.Create("no-elements", "空行")) };
        var snapshot = ProjectSnapshot.FromProject(project with { Lyrics = project.Lyrics.ReplaceBranch(branch) });
        var baseline = StaticStyleProjection.CreateBaseline(snapshot);
        Assert.Equal(MaterialValue.Solid(new(1, 0, 0, 1)), baseline.Get(new LineId(line.Id)).UnsungMaterial);
        Assert.Equal(MaterialValue.Solid(new(0, 0, 1, 1)), baseline.Get(new TokenId(token.Id)).UnsungMaterial);
        Assert.Equal(MaterialValue.Solid(new(0, 1, 0, 1)), baseline.Get(new LineId(line.Id)).SungMaterial);
        Assert.Equal((double).7f, baseline.Get(new LineId("no-elements")).Opacity);
        Assert.Equal(MaterialValue.Solid(new(0, 1, 0, 1)), baseline.Get(new LineId("no-elements")).SungMaterial);
        Assert.Equal(StaticStyleProjection.View(project.LyricStyle).UnsungMaterial, baseline.Get(new LineId("no-elements")).UnsungMaterial);
    }
}
