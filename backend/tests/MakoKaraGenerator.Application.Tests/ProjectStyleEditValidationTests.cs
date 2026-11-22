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
    public static TheoryData<LyricStyleProperty> AllProperties => [.. LyricStylePropertyExtensions.All];
    public static TheoryData<LyricStyleProperty> TokenUnsupported => [.. TokenStyleSupport.UnsupportedAtTokenLevel];
    public static TheoryData<LyricStyleProperty> TokenSupported => [.. TokenStyleSupport.SupportedAtTokenLevel];

    [Theory]
    [MemberData(nameof(AllProperties))]
    public void 规范成员独立写入且恢复不会留下未选覆盖(LyricStyleProperty property)
    {
        var workspace = Workspace(); var target = Target(StyleEditTargetKind.Branch);
        var change = Change(property);
        Assert.Equal(property, change.Property);
        var result = Commit(workspace, target, change);
        Assert.True(result.Accepted); Assert.True(result.Changed);
        Assert.NotNull(Own(result.Project, target)); Assert.False(Own(result.Project, target)!.IsEmpty);
        Assert.Contains(property, TokenStyleSupport.SetProperties(Own(result.Project, target)));
        var restored = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [change.Property]);
        Assert.True(restored.Result.Accepted); Assert.True(restored.Result.Changed);
        Assert.Null(Own(restored.Result.Project, target)); Assert.Same(result.Project, workspace.Project);
        ProjectStyleEditUseCase.CommitPrepared(workspace, restored);
        Assert.Equal("styles.restore", workspace.UndoName); Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(result.Project, workspace.Undo());
    }

    [Fact]
    public void 描边整表写入保层序且重放无变化()
    {
        var workspace = Workspace(); var target = Target(StyleEditTargetKind.Branch);
        var layers = TwoLayers();
        var first = Commit(workspace, target, new StyleEditChange.UnsungStrokes(layers));
        Assert.True(first.Changed); Assert.Equal(layers, Own(first.Project, target)!.UnsungStrokes);
        Assert.Equal(StrokeJoin.Bevel, Resolve(first.Project, target).Unsung.Strokes[1].Join);
        Assert.False(Commit(workspace, target, new StyleEditChange.UnsungStrokes(TwoLayers())).Changed);
        var off = Commit(workspace, target, new StyleEditChange.UnsungStrokes(StrokeLayers.Empty));
        Assert.True(off.Changed); Assert.Empty(Resolve(off.Project, target).Unsung.Strokes);
    }

    [Fact]
    public void 已唱跟随与显式取值互转且跟随项分量写成只含该分量的显式部分值()
    {
        var fresh = Workspace().Project;
        var parent = new ShadowSpec { Color = RgbaColor.White, Opacity = .6f, AngleDegrees = 30, DistancePx = 5, BlurRadiusPx = 2 };
        var workspace = ProjectWorkspace.Adopt(fresh with { LyricStyle = fresh.LyricStyle with { Shadow = parent } });
        var target = Target(StyleEditTargetKind.Line);
        Assert.Equal(parent, Resolve(workspace.Project, target).Sung.Shadow.Resolve(parent));
        var leaf = Commit(workspace, target, new StyleEditChange.EffectNumber(LyricStyleProperty.SungShadowOpacity, .2));
        Assert.True(leaf.Changed);
        var own = Own(leaf.Project, target)!.Sung!.Shadow.Value;
        Assert.False(own.FollowsUnsung); Assert.Equal(.2f, own.Value!.Opacity); Assert.Null(own.Value.Color);
        var resolved = Resolve(leaf.Project, target).Sung.Shadow;
        Assert.False(resolved.FollowsUnsung); Assert.Equal(parent with { Opacity = .2f }, resolved.Value);
        Assert.Equal(parent, Resolve(leaf.Project, target).Unsung.Shadow);
        var follow = Commit(workspace, target, new StyleEditChange.SungShadow(Followable.Follow<ShadowSpec?>()));
        Assert.True(follow.Changed); Assert.True(Resolve(follow.Project, target).Sung.Shadow.FollowsUnsung);
        var strokes = Commit(workspace, target, new StyleEditChange.SungStrokes(Followable.Set(TwoLayers())));
        Assert.Equal(TwoLayers(), Resolve(strokes.Project, target).Sung.Strokes.Value);
        Assert.Empty(Resolve(strokes.Project, target).Unsung.Strokes);
    }

    [Fact]
    public void 扫字线高亮叶从透明基准合并且整体关闭后恢复回到上层()
    {
        var workspace = Workspace(); var target = Target(StyleEditTargetKind.Line);
        var leaf = Commit(workspace, target, new StyleEditChange.EffectNumber(LyricStyleProperty.WipeHighlightWidthRatio, .25),
            new StyleEditChange.HighlightBlend(WipeHighlightBlend.Add), new StyleEditChange.WipeFeather(.2));
        Assert.True(leaf.Changed);
        var wipe = Resolve(leaf.Project, target).Wipe;
        Assert.Equal(.2, wipe.FeatherRatio); Assert.Equal(.25, wipe.Highlight!.WidthRatio); Assert.Equal(WipeHighlightBlend.Add, wipe.Highlight.Blend);
        var off = Commit(workspace, target, new StyleEditChange.WipeHighlight(null));
        Assert.Null(Resolve(off.Project, target).Wipe.Highlight);
        var leafRestore = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [LyricStyleProperty.WipeHighlightOpacity]);
        Assert.True(leafRestore.Result.Accepted); Assert.False(leafRestore.Result.Changed); Assert.Same(off.Project, leafRestore.Result.Project);
        var restored = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [LyricStyleProperty.WipeHighlight, LyricStyleProperty.WipeFeatherRatio]);
        Assert.True(restored.Result.Changed); Assert.Null(Own(restored.Result.Project, target));
    }

    [Theory]
    [MemberData(nameof(TokenUnsupported))]
    public void 元素不允许字体布局和缩放且拒绝不改工程(LyricStyleProperty property)
    {
        var workspace = Workspace(); var source = workspace.Project; var target = Target(StyleEditTargetKind.Token);
        var patch = ProjectStyleEditUseCase.PreparePatch(workspace, [target], [Change(property)]);
        var restore = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [property]);
        foreach (var prepared in new[] { patch, restore })
        {
            Assert.False(prepared.Result.Accepted); Assert.Equal(ProjectStyleEditErrorCode.UnsupportedProperty, prepared.Result.Error!.Code);
            var result = ProjectStyleEditUseCase.CommitPrepared(workspace, prepared);
            Assert.Same(source, result.Project); Assert.Same(source, workspace.Project); Assert.Equal(0, workspace.History.UndoCount);
        }
    }

    [Theory]
    [MemberData(nameof(TokenSupported))]
    public void 元素仅接受原领域支持的绘制属性(LyricStyleProperty property)
    {
        var workspace = Workspace(); var result = Commit(workspace, Target(StyleEditTargetKind.Token), Change(property));
        Assert.True(result.Accepted); Assert.True(result.Changed);
        var own = Own(result.Project, Target(StyleEditTargetKind.Token));
        Assert.Empty(TokenStyleSupport.UnsupportedSet(own));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    public void 非法规范值整批拒绝且观察器不会看到部分候选(int invalid)
    {
        var workspace = Workspace(); var source = workspace.Project;
        StyleEditChange change = invalid switch
        {
            0 => new StyleEditChange.FontSize(float.NaN),
            1 => new StyleEditChange.FontWeight(99),
            2 => new StyleEditChange.FallbackFamilyNames(["A", "A"]),
            3 => new StyleEditChange.FallbackFamilyNames(default),
            4 => new StyleEditChange.Fill(false, Package()),
            5 => new StyleEditChange.UnsungStrokes(StrokeLayers.FromStroke(new() { Material = Package(), WidthPx = 2 })),
            6 => new StyleEditChange.SungStrokes(Followable.Set(StrokeLayers.FromStroke(new() { WidthPx = -1 }))),
            7 => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungShadowDistancePx, -1),
            8 => new StyleEditChange.Opacity(float.PositiveInfinity),
            9 => new StyleEditChange.Fill(false, new MaterialSpec.Gradient(Gradient() with { Stops = [new(0, RgbaColor.White), new(.9, RgbaColor.Black), new(.1, RgbaColor.White), new(1, RgbaColor.Black)] })),
            10 => new StyleEditChange.Opacity(.5f) with { Property = LyricStyleProperty.Scale },
            11 => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungShadowColor, 1),
            12 => new StyleEditChange.EffectNumber(LyricStyleProperty.WipeHighlightWidthRatio, double.NaN),
            _ => new StyleEditChange.WipeFeather(-.1),
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
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void 空重复未知或父子冲突属性拒绝且工程不能恢复上层(int shape)
    {
        var workspace = Workspace(); var target = Target(StyleEditTargetKind.Line);
        var prepared = shape switch
        {
            0 => ProjectStyleEditUseCase.PreparePatch(workspace, [target], []),
            1 => ProjectStyleEditUseCase.PreparePatch(workspace, [target], [new StyleEditChange.Opacity(.4f), new StyleEditChange.Opacity(.5f)]),
            2 => ProjectStyleEditUseCase.PreparePatch(workspace, [target], [new StyleEditChange.SungGlow(Followable.Follow<GlowSpec?>()), new StyleEditChange.EffectNumber(LyricStyleProperty.SungGlowSpreadPx, 2)]),
            3 => ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [LyricStyleProperty.UnsungShadow, LyricStyleProperty.UnsungShadowOpacity]),
            4 => ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [LyricStyleProperty.WipeHighlight, LyricStyleProperty.WipeHighlightBlend]),
            _ => ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [(LyricStyleProperty)100]),
        };
        Assert.False(prepared.Result.Accepted); Assert.Equal(ProjectStyleEditErrorCode.InvalidProperty, prepared.Result.Error!.Code);
        var project = ProjectStyleEditUseCase.PrepareRestore(workspace, [Target(StyleEditTargetKind.Project)], [LyricStyleProperty.Opacity]);
        Assert.False(project.Result.Accepted); Assert.Equal(ProjectStyleEditErrorCode.UnsupportedProperty, project.Result.Error!.Code);
        Assert.Equal(0, workspace.History.UndoCount);
    }

    [Fact]
    public void 完整渐变保持所有几何和色彩选项与重复色标顺序()
    {
        var workspace = Workspace(); var gradient = Gradient(); var target = Target(StyleEditTargetKind.Line);
        var result = Commit(workspace, target, new StyleEditChange.Fill(true, new MaterialSpec.Gradient(gradient)));
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
        var restore = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [LyricStyleProperty.FallbackFamilyNames]);
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

    private static StrokeLayers TwoLayers() => StrokeLayers.Create(
        [StrokeSpec.Create(RgbaColor.Black, 3), new StrokeSpec { Material = new MaterialSpec.Gradient(Gradient()), WidthPx = 2, Join = StrokeJoin.Bevel }]);

    private static StyleEditChange Change(LyricStyleProperty property) => property switch
    {
        LyricStyleProperty.FontFamilyName => new StyleEditChange.FontFamilyName(null),
        LyricStyleProperty.FallbackFamilyNames => new StyleEditChange.FallbackFamilyNames(["A", "B"]),
        LyricStyleProperty.FontSizePx => new StyleEditChange.FontSize(87),
        LyricStyleProperty.FontWeight => new StyleEditChange.FontWeight(437),
        LyricStyleProperty.IsBold => new StyleEditChange.IsBold(true),
        LyricStyleProperty.IsItalic => new StyleEditChange.IsItalic(true),
        LyricStyleProperty.LetterSpacingPx => new StyleEditChange.LetterSpacing(3),
        LyricStyleProperty.RubyRelativeScale => new StyleEditChange.RubyRelativeScale(.7),
        LyricStyleProperty.RubyGapPx => new StyleEditChange.RubyGap(4),
        LyricStyleProperty.UnsungFill => new StyleEditChange.Fill(false, new MaterialSpec.Gradient(Gradient())),
        LyricStyleProperty.SungFill => new StyleEditChange.Fill(true, new MaterialSpec.Solid(RgbaColor.White)),
        LyricStyleProperty.UnsungStrokes => new StyleEditChange.UnsungStrokes(TwoLayers()),
        LyricStyleProperty.SungStrokes => new StyleEditChange.SungStrokes(Followable.Set(StrokeLayers.Empty)),
        LyricStyleProperty.UnsungShadow => new StyleEditChange.UnsungShadow(null),
        LyricStyleProperty.SungShadow => new StyleEditChange.SungShadow(Followable.Set<ShadowSpec?>(null)),
        LyricStyleProperty.UnsungGlow => new StyleEditChange.UnsungGlow(null),
        LyricStyleProperty.SungGlow => new StyleEditChange.SungGlow(Followable.Set<GlowSpec?>(new() { Color = RgbaColor.White, Opacity = .5f, BlurRadiusPx = 4 })),
        LyricStyleProperty.WipeFeatherRatio => new StyleEditChange.WipeFeather(.2),
        LyricStyleProperty.WipeHighlight => new StyleEditChange.WipeHighlight(new() { Color = RgbaColor.White, WidthRatio = .25, Opacity = .8f, Blend = WipeHighlightBlend.Screen }),
        LyricStyleProperty.WipeHighlightBlend => new StyleEditChange.HighlightBlend(WipeHighlightBlend.Add),
        LyricStyleProperty.VerticalCenterRatio => new StyleEditChange.VerticalCenter(.3),
        LyricStyleProperty.HorizontalAlignment => new StyleEditChange.HorizontalAlignment(LyricHorizontalAlignment.Left),
        LyricStyleProperty.Opacity => new StyleEditChange.Opacity(.7f),
        LyricStyleProperty.Scale => new StyleEditChange.Scale(2),
        _ => LyricStylePropertyExtensions.Component(property) switch
        {
            LyricStyleEffectComponent.Color => new StyleEditChange.EffectColor(property, RgbaColor.Black),
            LyricStyleEffectComponent.Opacity => new StyleEditChange.EffectNumber(property, .3),
            LyricStyleEffectComponent.AngleDegrees => new StyleEditChange.EffectNumber(property, 27),
            LyricStyleEffectComponent.WidthRatio => new StyleEditChange.EffectNumber(property, .3),
            LyricStyleEffectComponent.None => throw new ArgumentOutOfRangeException(nameof(property)),
            _ => new StyleEditChange.EffectNumber(property, 3),
        },
    };
}
