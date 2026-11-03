using System.Collections.Immutable;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;
using static MakoKaraGenerator.Application.Tests.ProjectStyleEditUseCaseTests;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectStyleEditValidationTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)]
    [InlineData(18)] [InlineData(19)] [InlineData(20)] [InlineData(21)] [InlineData(22)] [InlineData(23)]
    [InlineData(24)] [InlineData(25)] [InlineData(26)] [InlineData(27)] [InlineData(28)]
    public void 规范成员独立写入且恢复不会留下未选覆盖(int property)
    {
        var workspace = Workspace(); var target = Target(StyleEditTargetKind.Branch);
        var change = Change(property);
        Assert.Equal((StyleEditProperty)property, change.Property);
        var result = Commit(workspace, target, change);
        Assert.True(result.Accepted); Assert.True(result.Changed);
        Assert.NotNull(Own(result.Project, target)); Assert.False(Own(result.Project, target)!.IsEmpty);
        var restored = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [change.Property]);
        Assert.True(restored.Result.Accepted); Assert.True(restored.Result.Changed);
        Assert.Null(Own(restored.Result.Project, target)); Assert.Same(result.Project, workspace.Project);
        ProjectStyleEditUseCase.CommitPrepared(workspace, restored);
        Assert.Equal("styles.restore", workspace.UndoName); Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(result.Project, workspace.Undo());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(20)] [InlineData(21)] [InlineData(23)]
    public void 元素不允许字体布局和缩放且拒绝不改工程(int property)
    {
        var workspace = Workspace(); var source = workspace.Project; var target = Target(StyleEditTargetKind.Token);
        var patch = ProjectStyleEditUseCase.PreparePatch(workspace, [target], [Change(property)]);
        var restore = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [(StyleEditProperty)property]);
        foreach (var prepared in new[] { patch, restore })
        {
            Assert.False(prepared.Result.Accepted); Assert.Equal(ProjectStyleEditErrorCode.UnsupportedProperty, prepared.Result.Error!.Code);
            var result = ProjectStyleEditUseCase.CommitPrepared(workspace, prepared);
            Assert.Same(source, result.Project); Assert.Same(source, workspace.Project); Assert.Equal(0, workspace.History.UndoCount);
        }
    }

    [Theory]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)]
    [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(22)]
    [InlineData(24)] [InlineData(25)] [InlineData(26)] [InlineData(27)] [InlineData(28)]
    public void 元素仅接受原领域支持的绘制属性(int property)
    {
        var workspace = Workspace(); var result = Commit(workspace, Target(StyleEditTargetKind.Token), Change(property));
        Assert.True(result.Accepted); Assert.True(result.Changed);
        var own = Own(result.Project, Target(StyleEditTargetKind.Token));
        Assert.Empty(TokenStyleSupport.UnsupportedSet(own));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void 非法规范值整批拒绝且观察器不会看到部分候选(int invalid)
    {
        var workspace = Workspace(); var source = workspace.Project;
        StyleEditChange change = invalid switch
        {
            0 => new StyleEditChange.FontSize(float.NaN),
            1 => new StyleEditChange.FontWeight(99),
            2 => new StyleEditChange.FallbackFamilyNames(["A", "A"]),
            3 => new StyleEditChange.FallbackFamilyNames(default),
            4 => new StyleEditChange.UnsungMaterial(Package()),
            5 => new StyleEditChange.Stroke(new() { Material = Package() }),
            6 => new StyleEditChange.StrokeWidth(-1),
            7 => new StyleEditChange.ShadowDistance(-1),
            8 => new StyleEditChange.Opacity(float.PositiveInfinity),
            9 => new StyleEditChange.UnsungMaterial(new MaterialSpec.Gradient(Gradient() with { Stops = [new(0, RgbaColor.White), new(.9, RgbaColor.Black), new(.1, RgbaColor.White), new(1, RgbaColor.Black)] })),
            _ => new StyleEditChange.Opacity(.5f) with { Property = StyleEditProperty.Scale },
        };
        int observed = 0;
        var prepared = ProjectStyleEditUseCase.PreparePatch(workspace, [Target(StyleEditTargetKind.Line)], [new StyleEditChange.IsItalic(true), change], (_, _) => observed++);
        Assert.False(prepared.Result.Accepted); Assert.Equal(ProjectStyleEditErrorCode.InvalidValue, prepared.Result.Error!.Code);
        Assert.Equal(0, observed); Assert.Same(source, workspace.Project); Assert.Same(source, prepared.Result.Project);
        Assert.Equal(0, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void 非法空混层重复或不匹配目标整批拒绝(int shape)
    {
        var workspace = Workspace(); var source = workspace.Project; var target = Target(StyleEditTargetKind.Line);
        StyleEditTarget[] targets = shape switch
        {
            0 => [], 1 => [target, target], 2 => [target, Target(StyleEditTargetKind.Token)],
            3 => [new(StyleEditTargetKind.Project, "other")],
            4 => [new(StyleEditTargetKind.Branch, "other", "other1")],
            5 => [new(StyleEditTargetKind.Line, "primary", "other1")],
            6 => [new(StyleEditTargetKind.Token, "other", "other1", "token2")],
            7 => Enumerable.Range(0, 1001).Select(i => new StyleEditTarget(StyleEditTargetKind.Line, "other", i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray(),
            _ => [new((StyleEditTargetKind)100)],
        };
        var prepared = ProjectStyleEditUseCase.PreparePatch(workspace, targets, [new StyleEditChange.Opacity(.2f)]);
        Assert.False(prepared.Result.Accepted); Assert.Equal(ProjectStyleEditErrorCode.InvalidTarget, prepared.Result.Error!.Code);
        Assert.Same(source, workspace.Project); Assert.False(workspace.IsDirty);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void 空重复未知或父子冲突属性拒绝且工程不能恢复上层(int shape)
    {
        var workspace = Workspace(); var target = Target(StyleEditTargetKind.Line);
        var prepared = shape switch
        {
            0 => ProjectStyleEditUseCase.PreparePatch(workspace, [target], []),
            1 => ProjectStyleEditUseCase.PreparePatch(workspace, [target], [new StyleEditChange.Opacity(.4f), new StyleEditChange.Opacity(.5f)]),
            2 => ProjectStyleEditUseCase.PreparePatch(workspace, [target], [new StyleEditChange.Stroke(null), new StyleEditChange.StrokeWidth(2)]),
            3 => ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [StyleEditProperty.Shadow, StyleEditProperty.ShadowOpacity]),
            _ => ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [(StyleEditProperty)100]),
        };
        Assert.False(prepared.Result.Accepted); Assert.Equal(ProjectStyleEditErrorCode.InvalidProperty, prepared.Result.Error!.Code);
        var project = ProjectStyleEditUseCase.PrepareRestore(workspace, [Target(StyleEditTargetKind.Project)], [StyleEditProperty.Opacity]);
        Assert.False(project.Result.Accepted); Assert.Equal(ProjectStyleEditErrorCode.UnsupportedProperty, project.Result.Error!.Code);
        Assert.Equal(0, workspace.History.UndoCount);
    }

    [Fact]
    public void 完整渐变保持所有几何和色彩选项与重复色标顺序()
    {
        var workspace = Workspace(); var gradient = Gradient(); var target = Target(StyleEditTargetKind.Line);
        var result = Commit(workspace, target, new StyleEditChange.SungMaterial(new MaterialSpec.Gradient(gradient)));
        Assert.True(result.Accepted);
        var actual = Assert.IsType<MaterialSpec.Gradient>(Own(result.Project, target)!.SungFill.Value!.Material.Value);
        Assert.Same(gradient, actual.Value); Assert.Equal(gradient, Resolve(result.Project, target).SungFill.Gradient);
        Assert.Equal(.3, actual.Value.Stops[1].Offset); Assert.Equal(.3, actual.Value.Stops[2].Offset);
        Assert.Equal(RgbaColor.Black, actual.Value.Stops[1].Color); Assert.Equal(RgbaColor.Transparent, actual.Value.Stops[2].Color);
    }

    [Fact]
    public void 有效scene对象完整保留且非法旧引用不借样式修改自动清理()
    {
        var workspace = Workspace(); var source = workspace.Project;
        var tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics) with
        {
            LineOverrides = [new() { BranchId = "other", LineId = "other1", LocalTransform = new() { OffsetXRatio = .2, RotationDegrees = 28 } }],
        };
        Assert.False(PresentationValidator.Validate(source with { LyricPresentation = tree }).HasErrors);
        workspace = ProjectWorkspace.Adopt(source with { LyricPresentation = tree });
        var result = Commit(workspace, Target(StyleEditTargetKind.Line), new StyleEditChange.FontSize(80));
        Assert.True(result.Accepted); Assert.Same(tree, result.Project.LyricPresentation);
        var invalid = tree with { LineOverrides = tree.LineOverrides.Add(new() { BranchId = "other", LineId = "deleted" }) };
        source = workspace.Project with { LyricPresentation = invalid }; workspace = ProjectWorkspace.Adopt(source);
        var prepared = ProjectStyleEditUseCase.PreparePatch(workspace, [Target(StyleEditTargetKind.Line)], [new StyleEditChange.FontSize(81)]);
        Assert.False(prepared.Result.Accepted); Assert.Equal(ProjectStyleEditErrorCode.DomainValidationFailed, prepared.Result.Error!.Code);
        Assert.Same(invalid, workspace.Project.LyricPresentation); Assert.Same(source, prepared.Result.Project);
        Assert.Equal(2, workspace.Project.LyricPresentation.LineOverrides.Length); Assert.Equal(0, workspace.History.UndoCount);
    }

    [Fact]
    public void 旧大数组无变化或恢复只移除本层引用不把旧限制施加给共享调用()
    {
        var workspace = Workspace(); var target = Target(StyleEditTargetKind.Branch);
        var names = Enumerable.Range(0, 4096).Select(i => "font-" + i).ToImmutableArray();
        Commit(workspace, target, new StyleEditChange.FallbackFamilyNames(names));
        var source = workspace.Project;
        var noop = ProjectStyleEditUseCase.PreparePatch(workspace, [target], [new StyleEditChange.FallbackFamilyNames(names.ToArray().ToImmutableArray())]);
        Assert.True(noop.Result.Accepted); Assert.False(noop.Result.Changed); Assert.Same(source, noop.Result.Project);
        var restore = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [StyleEditProperty.TypographyFallbackFamilyNames]);
        Assert.True(restore.Result.Accepted); Assert.Null(Own(restore.Result.Project, target));
        Assert.Same(source.Lyrics.Branches[0], restore.Result.Project.Lyrics.Branches[0]);
    }

    [Fact]
    public void 一千真实稳定目标接受且每个候选独立观察最后一次历史提交()
    {
        var lines = Enumerable.Range(0, 1000).Select(i => LyricLine.Create("line-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "词")).ToImmutableArray();
        var workspace = ProjectWorkspace.Adopt(new Project { Name = "max-targets", Lyrics = new LyricsDocument
        { PrimaryBranchId = "primary", Branches = [LyricBranch.Create("primary", "主", LyricBranchRole.Primary, lines)] } });
        var source = workspace.Project; int observed = 0;
        var targets = lines.Select(line => new StyleEditTarget(StyleEditTargetKind.Line, "primary", line.Id)).ToArray();
        var prepared = ProjectStyleEditUseCase.PreparePatch(workspace, targets, [new StyleEditChange.Opacity(.25f)], (candidate, target) =>
        {
            Assert.Same(source, workspace.Project); Assert.Equal(.25f, Resolve(candidate, target).Opacity); observed++;
        });
        Assert.True(prepared.Result.Accepted); Assert.Equal(1000, prepared.Result.ChangedTargetCount); Assert.Equal(1000, observed);
        Assert.Equal(0, workspace.History.UndoCount); Assert.Same(source, workspace.Project);
        ProjectStyleEditUseCase.CommitPrepared(workspace, prepared);
        Assert.Equal(1, workspace.History.UndoCount); Assert.Same(source, workspace.Undo());
    }

    private static StyleEditChange Change(int property) => property switch
    {
        0 => new StyleEditChange.FontFamilyName(null), 1 => new StyleEditChange.FallbackFamilyNames(["A", "B"]),
        2 => new StyleEditChange.FontSize(87), 3 => new StyleEditChange.FontWeight(437),
        4 => new StyleEditChange.IsBold(true), 5 => new StyleEditChange.IsItalic(true),
        6 => new StyleEditChange.LetterSpacing(3), 7 => new StyleEditChange.UnsungMaterial(new MaterialSpec.Gradient(Gradient())),
        8 => new StyleEditChange.SungMaterial(new MaterialSpec.Solid(RgbaColor.White)),
        9 => new StyleEditChange.Stroke(null), 10 => new StyleEditChange.StrokeMaterial(new MaterialSpec.Gradient(Gradient())),
        11 => new StyleEditChange.StrokeWidth(9), 12 => new StyleEditChange.StrokeJoin(StrokeJoin.Bevel),
        13 => new StyleEditChange.StrokePosition(StrokePosition.Outside), 14 => new StyleEditChange.Shadow(null),
        15 => new StyleEditChange.ShadowColor(RgbaColor.Black), 16 => new StyleEditChange.ShadowOpacity(.3f),
        17 => new StyleEditChange.ShadowAngle(27), 18 => new StyleEditChange.ShadowDistance(3),
        19 => new StyleEditChange.ShadowBlur(6), 20 => new StyleEditChange.VerticalCenter(.3),
        21 => new StyleEditChange.HorizontalAlignment(LyricHorizontalAlignment.Left),
        22 => new StyleEditChange.Opacity(.7f), 23 => new StyleEditChange.Scale(2),
        24 => new StyleEditChange.Glow(null), 25 => new StyleEditChange.GlowColor(RgbaColor.White),
        26 => new StyleEditChange.GlowOpacity(.3f), 27 => new StyleEditChange.GlowBlur(6),
        28 => new StyleEditChange.GlowSpread(2), _ => throw new ArgumentOutOfRangeException(nameof(property)),
    };
}
