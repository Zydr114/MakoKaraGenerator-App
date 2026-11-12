using System.Collections.Immutable;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectStyleEditUseCaseTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 辉光四层局部叶覆盖关闭和恢复(int kind)
    {
        var initial = Workspace().Project;
        var parent = new GlowSpec { Color = RgbaColor.White, Opacity = .8f, BlurRadiusPx = 8, SpreadPx = 2 };
        var workspace = ProjectWorkspace.Adopt(initial with { LyricStyle = initial.LyricStyle with { Glow = parent } });
        var target = Target((StyleEditTargetKind)kind);
        var set = Commit(workspace, target, new StyleEditChange.GlowBlur(4));
        Assert.True(set.Accepted); Assert.Equal(4, Resolve(set.Project, target).Glow!.BlurRadiusPx);
        Assert.Equal(2, Resolve(set.Project, target).Glow!.SpreadPx);
        if (kind == 0) return;
        Commit(workspace, target, new StyleEditChange.Glow(null));
        Assert.Null(Resolve(workspace.Project, target).Glow);
        var noOp = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [StyleEditProperty.GlowBlurRadiusPx]);
        Assert.True(noOp.Result.Accepted); Assert.False(noOp.Result.Changed);
        var restored = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [StyleEditProperty.Glow]);
        Assert.True(restored.Result.Accepted); Assert.Equal(parent, Resolve(restored.Result.Project, target).Glow);
    }

    [Fact]
    public void 辉光无父级从透明种子合并且校验原子性()
    {
        var workspace = Workspace(); var target = Target(StyleEditTargetKind.Token);
        var result = Commit(workspace, target, new StyleEditChange.GlowOpacity(.5f));
        var glow = Resolve(result.Project, target).Glow!;
        Assert.Equal(RgbaColor.Transparent, glow.Color); Assert.Equal(.5f, glow.Opacity);
        Assert.Equal(0, glow.BlurRadiusPx); Assert.Equal(0, glow.SpreadPx);
        var bad = ProjectStyleEditUseCase.PreparePatch(workspace, [target], [new StyleEditChange.GlowSpread(-1)]);
        Assert.False(bad.Result.Accepted); Assert.Same(result.Project, workspace.Project);
        var conflict = ProjectStyleEditUseCase.PreparePatch(workspace, [target], [new StyleEditChange.Glow(null), new StyleEditChange.GlowColor(RgbaColor.White)]);
        Assert.False(conflict.Result.Accepted); Assert.Same(result.Project, workspace.Project);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 四层稳定目标纯准备后只提交一次并保正文时间和其他来源(int kind)
    {
        var workspace = Workspace(); var source = workspace.Project;
        var target = Target((StyleEditTargetKind)kind);
        int events = 0, observed = 0;
        workspace.History.Changed += (_, _) => events++;
        var prepared = ProjectStyleEditUseCase.PreparePatch(workspace, [target], [new StyleEditChange.Opacity(.35f)],
            (candidate, actualTarget) =>
            {
                Assert.Equal(target, actualTarget); Assert.Same(source, workspace.Project);
                Assert.Equal(.35f, Resolve(candidate, target).Opacity); observed++;
            });
        Assert.True(prepared.Result.Accepted); Assert.True(prepared.Result.Changed);
        Assert.Equal(1, prepared.Result.ChangedTargetCount); Assert.Equal(1, observed);
        Assert.Same(source, workspace.Project); Assert.Equal(0, events); Assert.False(workspace.IsDirty);
        Assert.Same(source.Lyrics.Branches[0], prepared.Result.Project.Lyrics.Branches[0]);
        Assert.Same(source.LyricPresentation, prepared.Result.Project.LyricPresentation);
        Assert.Same(source.Effects, prepared.Result.Project.Effects);
        Assert.Same(source.PresetApplications, prepared.Result.Project.PresetApplications);
        var result = ProjectStyleEditUseCase.CommitPrepared(workspace, prepared);
        Assert.Same(prepared.Result.Project, result.Project); Assert.Same(result.Project, workspace.Project);
        Assert.Equal("styles.patch", workspace.UndoName); Assert.Equal(1, events); Assert.True(workspace.IsDirty);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
        var repeated = ProjectStyleEditUseCase.CommitPrepared(workspace, prepared);
        Assert.False(repeated.Accepted); Assert.Equal(ProjectStyleEditErrorCode.TargetStale, repeated.Error!.Code);
        Assert.Equal(1, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 四层静态继承只写指定局部字段并保高级包材质(int kind)
    {
        MaterialSpec.PackageMaterial package = Package();
        var workspace = Workspace(); var source = workspace.Project;
        source = source with { LyricStyle = source.LyricStyle with { UnsungFill = new() { Material = package }, Stroke = new() { Material = package, WidthPx = 9, Join = StrokeJoin.Bevel } } };
        workspace = ProjectWorkspace.Adopt(source);
        var target = Target((StyleEditTargetKind)kind);
        var prepared = ProjectStyleEditUseCase.PreparePatch(workspace, [target], [new StyleEditChange.ShadowAngle(128)]);
        Assert.True(prepared.Result.Accepted);
        var resolved = Resolve(prepared.Result.Project, target);
        Assert.Same(package, resolved.UnsungFill.Material); Assert.Same(package, resolved.Stroke!.Material);
        Assert.Equal(9, resolved.Stroke.WidthPx); Assert.Equal(StrokeJoin.Bevel, resolved.Stroke.Join);
        Assert.Equal(128, resolved.Shadow!.AngleDegrees);
        if (kind != 0)
        {
            var own = Own(prepared.Result.Project, target)!;
            Assert.Null(own.Typography); Assert.False(own.UnsungFill.IsSet); Assert.False(own.Stroke.IsSet);
        }
    }

    [Fact]
    public void 字体null和效果关闭均是本层显式值且whole恢复才取消关闭()
    {
        var workspace = Workspace(); var source = workspace.Project;
        source = source with { LyricStyle = source.LyricStyle with { FontFamilyName = "Parent", Stroke = new() { WidthPx = 8 }, Shadow = new() { DistancePx = 9 } } };
        workspace = ProjectWorkspace.Adopt(source); var target = Target(StyleEditTargetKind.Line);
        var result = Commit(workspace, target, new StyleEditChange.FontFamilyName(null), new StyleEditChange.Stroke(null), new StyleEditChange.Shadow(null));
        Assert.True(result.Changed); var own = Own(result.Project, target)!;
        Assert.True(own.Typography!.FontFamilyName.IsSet); Assert.Null(own.Typography.FontFamilyName.Value);
        Assert.True(own.Stroke.IsSet); Assert.Null(own.Stroke.Value); Assert.True(own.Shadow.IsSet); Assert.Null(own.Shadow.Value);
        Assert.Null(Resolve(result.Project, target).FontFamilyName); Assert.Null(Resolve(result.Project, target).Stroke);
        Assert.Null(Resolve(result.Project, target).Shadow);
        var noChange = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [StyleEditProperty.StrokeWidthPx, StyleEditProperty.ShadowColor]);
        Assert.True(noChange.Result.Accepted); Assert.False(noChange.Result.Changed); Assert.Same(result.Project, noChange.Result.Project);
        var restore = ProjectStyleEditUseCase.PrepareRestore(workspace, [target], [StyleEditProperty.TypographyFontFamilyName, StyleEditProperty.Stroke, StyleEditProperty.Shadow]);
        Assert.True(restore.Result.Accepted); Assert.Null(Own(restore.Result.Project, target));
        Assert.Equal("Parent", Resolve(restore.Result.Project, target).FontFamilyName);
        Assert.Equal(8, Resolve(restore.Result.Project, target).Stroke!.WidthPx);
        Assert.Equal(9, Resolve(restore.Result.Project, target).Shadow!.DistancePx);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 关闭后设置叶转部分覆盖并从上层解析而非默认白色新效果(bool parentEnabled)
    {
        var workspace = Workspace(); var source = workspace.Project;
        MaterialSpec.PackageMaterial package = Package();
        source = source with { LyricStyle = source.LyricStyle with
        {
            Stroke = parentEnabled ? new() { Material = package, WidthPx = 7, Join = StrokeJoin.Bevel } : null,
            Shadow = parentEnabled ? new() { Color = RgbaColor.White, AngleDegrees = 76, DistancePx = 8, BlurRadiusPx = 6 } : null,
        } };
        workspace = ProjectWorkspace.Adopt(source); var target = Target(StyleEditTargetKind.Line);
        Commit(workspace, target, new StyleEditChange.Stroke(null), new StyleEditChange.Shadow(null));
        var result = Commit(workspace, target, new StyleEditChange.StrokeWidth(4), new StyleEditChange.ShadowOpacity(.4f));
        var own = Own(result.Project, target)!;
        Assert.Equal(4, own.Stroke.Value!.WidthPx);
        Assert.Null(own.Shadow.Value!.Color); Assert.Null(own.Shadow.Value.DistancePx);
        var resolved = Resolve(result.Project, target);
        Assert.Equal(4, resolved.Stroke!.WidthPx); Assert.Equal(.4f, resolved.Shadow!.Opacity);
        if (parentEnabled)
        {
            Assert.Same(package, resolved.Stroke.Material); Assert.Equal(StrokeJoin.Bevel, resolved.Stroke.Join);
            Assert.Equal(RgbaColor.White, resolved.Shadow.Color); Assert.Equal(76, resolved.Shadow.AngleDegrees);
            Assert.Equal(8, resolved.Shadow.DistancePx); Assert.Equal(6, resolved.Shadow.BlurRadiusPx);
        }
        else
        {
            Assert.Equal(new MaterialSpec.Solid(RgbaColor.Transparent), resolved.Stroke.Material);
            Assert.Equal(RgbaColor.Transparent, resolved.Shadow.Color); Assert.Equal(0, resolved.Shadow.DistancePx);
            Assert.Equal(0, resolved.Shadow.BlurRadiusPx);
        }
    }

    [Fact]
    public void 工程基线空效果设置叶沿Core透明零宽基准且零宽描边即无描边()
    {
        var workspace = Workspace(); var target = Target(StyleEditTargetKind.Project);
        var result = Commit(workspace, target, new StyleEditChange.StrokeJoin(StrokeJoin.Bevel), new StyleEditChange.ShadowAngle(20));
        Assert.True(result.Accepted); var style = result.Project.LyricStyle;
        Assert.Null(style.Stroke); Assert.Empty(style.Unsung.Strokes);
        Assert.Equal(RgbaColor.Transparent, style.Shadow!.Color); Assert.Equal(0, style.Shadow.DistancePx); Assert.Equal(0, style.Shadow.BlurRadiusPx);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 等值数组和渐变不产生历史并保redo和旧预设标识(int kind)
    {
        var workspace = Workspace(); var target = Target((StyleEditTargetKind)kind);
        Commit(workspace, target, new StyleEditChange.UnsungMaterial(new MaterialSpec.Gradient(Gradient())));
        if (kind != 3) Commit(workspace, target, new StyleEditChange.FallbackFamilyNames(["A", "B"]));
        var source = workspace.Project with { LyricStylePresetId = "old-preset" };
        workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("rename", p => p with { Name = "new" })); workspace.Undo();
        List<StyleEditChange> changes = [new StyleEditChange.UnsungMaterial(new MaterialSpec.Gradient(Gradient()))];
        if (kind != 3) changes.Add(new StyleEditChange.FallbackFamilyNames(["A", "B"]));
        int observed = 0;
        var prepared = ProjectStyleEditUseCase.PreparePatch(workspace, [target], changes, (_, _) => observed++);
        Assert.True(prepared.Result.Accepted); Assert.False(prepared.Result.Changed); Assert.Equal(0, observed);
        var result = ProjectStyleEditUseCase.CommitPrepared(workspace, prepared);
        Assert.Same(source, result.Project); Assert.Equal("old-preset", result.Project.LyricStylePresetId);
        Assert.Equal(1, workspace.History.RedoCount); Assert.Equal(0, workspace.History.UndoCount);
        if (kind == 0)
        {
            var changed = Commit(workspace, target, new StyleEditChange.FontSize(81));
            Assert.True(changed.Changed); Assert.Null(changed.Project.LyricStylePresetId);
        }
    }

    [Fact]
    public void 真实多目标budget拒绝保源引用dirty历史redo和通知且不会继续后目标()
    {
        var workspace = Workspace(); var source = workspace.Project;
        workspace.Execute(ProjectCommand.Create("rename", p => p with { Name = "new" })); workspace.Undo();
        int observed = 0, events = 0; workspace.History.Changed += (_, _) => events++;
        var targets = new[] { Target(StyleEditTargetKind.Line), new StyleEditTarget(StyleEditTargetKind.Line, "other", "other2"), new StyleEditTarget(StyleEditTargetKind.Line, "primary", "main") };
        var expected = new InvalidOperationException("budget");
        var error = Assert.Throws<InvalidOperationException>(() => ProjectStyleEditUseCase.PreparePatch(workspace, targets,
            [new StyleEditChange.FontSize(91)], (candidate, target) =>
            {
                Assert.Equal(91, Resolve(candidate, target).FontSizePx); Assert.Same(source, workspace.Project);
                if (++observed == 2) throw expected;
            }));
        Assert.Same(expected, error); Assert.Equal(2, observed); Assert.Same(source, workspace.Project);
        Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.History.UndoCount); Assert.Equal(1, workspace.History.RedoCount); Assert.Equal(0, events);
    }

    [Fact]
    public void 全部目标先验证否则之前有效目标不触发观察器()
    {
        var workspace = Workspace(); int observed = 0;
        var prepared = ProjectStyleEditUseCase.PreparePatch(workspace,
            [Target(StyleEditTargetKind.Line), new(StyleEditTargetKind.Line, "other", "missing")], [new StyleEditChange.FontSize(89)], (_, _) => observed++);
        Assert.False(prepared.Result.Accepted); Assert.Equal(1, prepared.Result.Error!.TargetIndex); Assert.Equal(0, observed);
        Assert.Same(workspace.Project, prepared.Result.Project); Assert.Equal(0, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void 捕获候选后工程或工作区变化拒绝(int change)
    {
        var workspace = Workspace(); var prepared = ProjectStyleEditUseCase.PreparePatch(workspace, [Target(StyleEditTargetKind.Line)], [new StyleEditChange.FontSize(89)]);
        ProjectWorkspace current = workspace;
        if (change == 0) workspace.Execute(ProjectCommand.Create("rename", p => p with { Name = "new" }));
        if (change == 1) workspace.StartNew(workspace.Project);
        if (change == 2) current = ProjectWorkspace.Adopt(workspace.Project);
        var source = current.Project; int undo = current.History.UndoCount;
        var result = ProjectStyleEditUseCase.CommitPrepared(current, prepared);
        Assert.False(result.Accepted); Assert.Same(source, current.Project); Assert.Equal(undo, current.History.UndoCount);
        Assert.Equal(change == 0 ? ProjectStyleEditErrorCode.TargetStale : ProjectStyleEditErrorCode.WorkspaceStale, result.Error!.Code);
    }

    [Fact]
    public void 同层批量只产生一次历史并按真实变化数反馈()
    {
        var workspace = Workspace(); var source = workspace.Project;
        Commit(workspace, Target(StyleEditTargetKind.Line), new StyleEditChange.Opacity(.7f));
        source = workspace.Project;
        var prepared = ProjectStyleEditUseCase.PreparePatch(workspace,
            [Target(StyleEditTargetKind.Line), new(StyleEditTargetKind.Line, "other", "other2"), new(StyleEditTargetKind.Line, "primary", "main")], [new StyleEditChange.Opacity(.7f)]);
        Assert.Equal(2, prepared.Result.ChangedTargetCount);
        Assert.True(prepared.Result.ChangeSet.FullRefreshRequired);
        ProjectStyleEditUseCase.CommitPrepared(workspace, prepared);
        Assert.Equal(2, workspace.History.UndoCount); Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 字体透明度和描边宽度叶更新保非空高精度包参数及未选材质身份()
    {
        MaterialSpec.PackageMaterial package = Package();
        byte[] originalParameters = package.Parameters.Encode().ToArray();
        var workspace = Workspace(); var source = workspace.Project;
        var branch = source.Lyrics.Branches[1]; var line = branch.Lines[0];
        line = line with { StyleOverride = new()
        {
            UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = package }),
            SungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = package }),
            Stroke = OptionalValue.Set<StrokeSpec?>(new StrokeSpec { Material = package, WidthPx = 8, Join = StrokeJoin.Bevel }),
        } };
        source = source with { Lyrics = source.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(0, line) }) };
        workspace = ProjectWorkspace.Adopt(source); var target = Target(StyleEditTargetKind.Line);
        var result = Commit(workspace, target, new StyleEditChange.FontSize(82), new StyleEditChange.Opacity(.4f), new StyleEditChange.StrokeWidth(3));
        Assert.True(result.Accepted); var own = Own(result.Project, target)!;
        Assert.Same(package, own.UnsungFill.Value!.Material.Value); Assert.Same(package, own.SungFill.Value!.Material.Value);
        var actual = Assert.IsType<MaterialSpec.PackageMaterial>(own.Stroke.Value!.Material);
        Assert.Same(package, actual); Assert.Same(package.Parameters, actual.Parameters);
        Assert.Equal(originalParameters, actual.Parameters.Encode().ToArray());
        Assert.Contains("0.12345678901234567890123456789", actual.Parameters.Json, StringComparison.Ordinal);
        Assert.Equal(StrokeJoin.Bevel, own.Stroke.Value.Join); Assert.Equal(3, own.Stroke.Value.WidthPx);
        var replaced = Commit(workspace, target, new StyleEditChange.SungMaterial(new MaterialSpec.Solid(RgbaColor.White)));
        Assert.True(replaced.Accepted); own = Own(replaced.Project, target)!;
        Assert.IsType<MaterialSpec.Solid>(own.SungFill.Value!.Material.Value);
        Assert.Same(package, own.UnsungFill.Value!.Material.Value); Assert.Same(package, own.Stroke.Value!.Material);
        Assert.Equal(originalParameters, package.Parameters.Encode().ToArray());
    }

    internal static ProjectWorkspace Workspace() => ProjectWorkspace.Adopt(new Project
    {
        Name = "styles",
        Lyrics = new LyricsDocument
        {
            PrimaryBranchId = "primary",
            Branches = [LyricBranch.Create("primary", "主", LyricBranchRole.Primary, [LyricLine.Create("main", "主")]),
                LyricBranch.Create("other", "译", LyricBranchRole.Translation,
                    [LyricLine.Create("other1", "译", tokens: [LyricToken.Create("token1", "译")]), LyricLine.Create("other2", "二", tokens: [LyricToken.Create("token2", "二")])])],
        },
    });

    internal static StyleEditTarget Target(StyleEditTargetKind kind) => kind switch
    {
        StyleEditTargetKind.Project => new(kind), StyleEditTargetKind.Branch => new(kind, "other"),
        StyleEditTargetKind.Line => new(kind, "other", "other1"), _ => new(kind, "other", "other1", "token1"),
    };

    internal static ProjectStyleEditResult Commit(ProjectWorkspace workspace, StyleEditTarget target, params StyleEditChange[] changes) =>
        ProjectStyleEditUseCase.CommitPrepared(workspace, ProjectStyleEditUseCase.PreparePatch(workspace, [target], changes));

    internal static LyricStyle Resolve(Project project, StyleEditTarget target)
    {
        var branch = target.BranchId is null ? null : project.Lyrics.Branches.Single(b => b.Id == target.BranchId);
        var line = target.LineId is null ? null : branch!.Lines.Single(l => l.Id == target.LineId);
        var token = target.TokenId is null ? null : line!.Tokens.Single(t => t.Id == target.TokenId);
        return LyricStyleResolver.Resolve(project.LyricStyle, branchOverride: branch?.StyleOverride, lineOverride: line?.StyleOverride, tokenOverride: token?.StyleOverride).Style;
    }

    internal static LyricStyleOverride? Own(Project project, StyleEditTarget target)
    {
        var branch = project.Lyrics.Branches.Single(b => b.Id == target.BranchId);
        if (target.Kind == StyleEditTargetKind.Branch) return branch.StyleOverride;
        var line = branch.Lines.Single(l => l.Id == target.LineId);
        return target.Kind == StyleEditTargetKind.Line ? line.StyleOverride : line.Tokens.Single(t => t.Id == target.TokenId).StyleOverride;
    }

    internal static MaterialSpec.PackageMaterial Package() => new("pkg", "sha256:" + new string('a', 64), "material",
        EffectParameterOverrides.Parse("""{"gain":0.12345678901234567890123456789,"count":9007199254740993,"nested":{"label":"保留","list":[1,2]}}"""));
    internal static GradientSpec Gradient() => new()
    {
        Kind = GradientKind.Radial, CenterX = .2, CenterY = .7, RadiusX = .3, RadiusY = .8, AngleDegrees = 26,
        CoordinateSpace = GradientCoordinateSpace.Glyph, ColorSpace = GradientColorSpace.Hsv, HuePath = GradientHuePath.Longer,
        Stops = [new(0, RgbaColor.White), new(.3, RgbaColor.Black), new(.3, RgbaColor.Transparent), new(1, RgbaColor.White)],
    };
}
