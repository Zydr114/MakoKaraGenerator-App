using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SceneTargetContractTests
{
    private static readonly TextMarkTarget Target = new(new("mark"), new("cue"), new("binding"));
    private static readonly MaterialValue White = MaterialValue.Solid(new(1, 1, 1, 1));
    private static readonly MaterialValue Blue = MaterialValue.Solid(new(0, 0, 1, 1));
    private static readonly StyleView Appearance = TestStyles.View(White, White, new(2, White), new(new(0, 0, 0, 1)), .4);
    private static readonly BaselineStyles Baseline = new([], [new(new("line"), Appearance)], [new(Target.NodeId, Appearance)],
        [new(new("group"), new(1))]);
    private static TextOutput Output(params string[] capabilities)
    {
        var frame = V2PackageTestHelper.CreateTextFrame(baseline: Baseline);
        return new(in frame, [.. capabilities.Length == 0
            ? [CapabilityNames.StyleFill, CapabilityNames.StyleStroke, CapabilityNames.StyleShadow, CapabilityNames.MarkAffine]
            : capabilities]);
    }
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
    public void 标记输出检查完整提示身份能力和阶段()
    {
        foreach (var invalid in new[] { Target with { CueId = new("other") }, Target with { BindingId = new("other") }, Target with { NodeId = new("other") } })
        {
            Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => Output().SetFill(new TextTarget.Mark(invalid), TextState.Sung, Blue)).Code);
            Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => Output().AppendTransform(new TextTarget.Mark(invalid), Affine2.Identity)).Code);
        }
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Output(CapabilityNames.MarkAffine).SetFill(new TextTarget.Mark(Target), TextState.Unsung, Blue)).Code);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Output(CapabilityNames.StyleFill).AppendTransform(new TextTarget.Mark(Target), Affine2.Identity)).Code);
        TextOutput output = Output();
        output.SetFill(new TextTarget.Mark(Target), TextState.Sung, Blue); output.Complete();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.SetShadow(new TextTarget.Mark(Target), TextState.Unsung, null)).Code);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Mark(Target), Affine2.Identity)).Code);
    }
    [Fact]
    public void 标记两态和装饰按顺序写入并拒绝直接标记透明度写入()
    {
        TextOutput output = Output();
        var mark = new TextTarget.Mark(Target);
        output.SetFill(mark, TextState.Sung, Blue); output.SetFill(mark, TextState.Sung, Baseline.Get(Target).Sung.Fill);
        output.SetFill(mark, TextState.Unsung, Blue); output.SetStrokes(mark, TextState.Unsung); output.SetShadow(mark, TextState.Unsung, null);
        Assert.Equal("PropertyTargetUnsupported", Assert.Throws<EffectOutputException>(() => output.MultiplyOpacity(mark, .5)).Code);
        var writes = output.Complete().Writes;
        Assert.Equal(5, writes.Length);
        Assert.Equal(White, Assert.IsType<TextWrite.Fill>(writes[1]).Material);
        Assert.Equal(Blue, Assert.IsType<TextWrite.Fill>(writes[2]).Material);
        Assert.Empty(Assert.IsType<TextWrite.Strokes>(writes[3]).Layers);
        Assert.Null(Assert.IsType<TextWrite.Shadow>(writes[4]).Value);
        Assert.Equal(.4, Baseline.Get(Target).Opacity);
    }
    [Fact]
    public void 标记仿射不接受零矩阵且按所属逻辑空间顺序写入()
    {
        TextOutput output = Output();
        var mark = new TextTarget.Mark(Target);
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => output.AppendTransform(mark, default)).Code);
        output.AppendTransform(mark, Affine2.Translation(10, 20));
        output.AppendTransform(mark, Affine2.ScaleAt(2, default));
        var writes = output.Complete().Writes;
        Assert.Equal(2, writes.Length);
        var combined = ((TextWrite.Transform)writes[1]).LocalMatrix.Compose(((TextWrite.Transform)writes[0]).LocalMatrix);
        Assert.Equal(new Point2(22, 44), combined.Map(new(1, 2)));
    }
    [Fact]
    public void 标记材质严格校验固定资源修订不当作已经执行shader()
    {
        var handle = new ResourceCatalog("package", "sha256:" + new string('a', 64), ["glass"], [], []).GetMaterial("glass");
        Assert.Equal("InvalidMaterialResource", Assert.Throws<EffectOutputException>(() => Output().SetFill(new TextTarget.Mark(Target), TextState.Unsung,
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
        Assert.Equal(MaterialValue.Solid(new(1, 0, 0, 1)), baseline.Get(new LineId(line.Id)).Unsung.Fill);
        Assert.Equal(MaterialValue.Solid(new(0, 0, 1, 1)), baseline.Get(new TokenId(token.Id)).Unsung.Fill);
        Assert.Equal(MaterialValue.Solid(new(0, 1, 0, 1)), baseline.Get(new LineId(line.Id)).Sung.Fill);
        Assert.Equal((double).7f, baseline.Get(new LineId("no-elements")).Opacity);
        Assert.Equal(MaterialValue.Solid(new(0, 1, 0, 1)), baseline.Get(new LineId("no-elements")).Sung.Fill);
        Assert.Equal(StaticStyleProjection.View(project.LyricStyle).Unsung.Fill, baseline.Get(new LineId("no-elements")).Unsung.Fill);
    }
}
