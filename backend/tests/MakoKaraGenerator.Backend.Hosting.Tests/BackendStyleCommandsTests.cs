using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendStyleCommandsTests
{
    [Fact]
    public async Task 辉光读回关闭恢复及无操作保留重做()
    {
        using var f = await Fixture.Create();
        f.Replace(f.Workspace.Project with { LyricStyle = f.Workspace.Project.LyricStyle with { Glow = new() { BlurRadiusPx = 9, SpreadPx = 2 } } });
        Accepted(await f.Command("0", Patch(Line, new StyleUnsungGlowChangeDto(null))));
        var read = await f.Read(Line, "1");
        Assert.Null(read.StaticHierarchy.Unsung.Glow); Assert.Contains(read.Local, x => x is StyleUnsungGlowChangeDto { Value: null });
        Assert.False(Accepted(await f.Command("1", new RestoreStylesCommandDto([Line], ["unsung.glow.blurRadiusPx"]))).Changed);
        Accepted(await f.Command("1", Patch(Line, new StyleUnsungGlowOpacityChangeDto(.4f))));
        read = await f.Read(Line, "2"); Assert.Equal(9, read.StaticHierarchy.Unsung.Glow!.BlurRadiusPx); Assert.Equal(.4f, read.StaticHierarchy.Unsung.Glow.Opacity);
        Accepted(await f.Command("2", new RestoreStylesCommandDto([Line], ["unsung.glow"])));
        Assert.Equal(2, (await f.Read(Line, "3")).StaticHierarchy.Unsung.Glow!.SpreadPx);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static readonly StyleTargetDto Line = new LineStyleTargetDto("secondary", "other");
    private static PatchStylesCommandDto Patch(StyleTargetDto target, params StyleChangeDto[] changes) => new([target], changes);
    private static ProjectCommandResponse Accepted(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, $"{result.Error?.Code}/{result.Error?.FailureCode}: {result.Error?.Message}"); return result.Response!; }

    [Fact]
    public async Task 注音大小间距支持局部覆盖显式空值恢复和撤销()
    {
        using var f = await Fixture.Create();
        var token = new TokenStyleTargetDto("secondary", "other", "other-token");
        Accepted(await f.Command("0", Patch(Line, new StyleRubyRelativeScaleChangeDto(.6), new StyleRubyGapChangeDto(4))));
        var inherited = await f.Read(token, "1");
        Assert.Equal(.6, inherited.StaticHierarchy.Ruby.RelativeScale);
        Assert.Equal(4, inherited.StaticHierarchy.Ruby.GapPx);
        Accepted(await f.Command("1", Patch(token, new StyleRubyRelativeScaleChangeDto(null), new StyleRubyGapChangeDto(0))));
        var read = await f.Read(token, "2");
        Assert.Null(read.StaticHierarchy.Ruby.RelativeScale);
        Assert.Equal(0, read.StaticHierarchy.Ruby.GapPx);
        Assert.Contains(read.Local, item => item is StyleRubyRelativeScaleChangeDto { Value: null });
        Assert.Contains("\"property\":\"ruby.relativeScale\",\"value\":null", JsonSerializer.Serialize(read, Json), StringComparison.Ordinal);
        Assert.False(Accepted(await f.Command("2", Patch(token, new StyleRubyGapChangeDto(0)))).Changed);
        Accepted(await f.Command("2", new RestoreStylesCommandDto([token], ["ruby.relativeScale"])));
        Assert.Equal(.6, (await f.Read(token, "3")).StaticHierarchy.Ruby.RelativeScale);
        Accepted(await f.Command("3", new("history.undo")));
        Assert.Null((await f.Read(token, "4")).StaticHierarchy.Ruby.RelativeScale);
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Command("4", Patch(token, new StyleRubyGapChangeDto(-1)))).Error!.Code);
    }

    [Fact]
    public async Task 四层非主来源规范投影局部修改撤销重做与空操作历史一致()
    {
        using var f = await Fixture.Create();
        StyleTargetDto[] targets = [new ProjectStyleTargetDto(), new BranchStyleTargetDto("secondary"), Line, new TokenStyleTargetDto("secondary", "other", "other-token")];
        for (int index = 0; index < targets.Length; index++)
        {
            var changed = Accepted(await f.Command(index.ToString(CultureInfo.InvariantCulture), Patch(targets[index], new StyleOpacityChangeDto(.8f - index * .1f))));
            Assert.True(changed.Changed); Assert.Equal(1, changed.StyleResult!.ChangedTargetCount); Assert.Equal("styles.patch", changed.UndoName);
            Assert.Equal(["styles", "renderInputs"], changed.ChangeSet.Areas); Assert.True(changed.ChangeSet.RequiresFullRefresh);
            var read = await f.Read(targets[index], (index + 1).ToString(CultureInfo.InvariantCulture));
            Assert.Equal(.8f - index * .1f, read.StaticHierarchy.Opacity); Assert.Equal("staticHierarchy", read.Resolution);
            Assert.Equal("primary", f.Workspace.Project.Lyrics.PrimaryBranchId);
        }
        var undo = Accepted(await f.Command("4", new("history.undo")));
        Assert.Null(undo.StyleResult); Assert.True(undo.ChangeSet.RequiresFullRefresh); Assert.Equal(["project"], undo.ChangeSet.Areas);
        var beforeNoop = f.Workspace.Project;
        var noop = Accepted(await f.Command("5", new RestoreStylesCommandDto([targets[3]], ["opacity"])));
        Assert.False(noop.Changed); Assert.Equal(0, noop.StyleResult!.ChangedTargetCount); Assert.True(noop.CanRedo);
        Assert.Same(beforeNoop, f.Workspace.Project); Assert.Equal("5", noop.ProjectRevision);
        var redo = Accepted(await f.Command("5", new("history.redo"))); Assert.True(redo.ChangeSet.RequiresFullRefresh);
        Assert.Equal(["project"], redo.ChangeSet.Areas); Assert.Equal(4, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 三态和关闭后叶设置继承真实上层阴影且未选包材质描边保持原引用()
    {
        using var f = await Fixture.Create();
        var package = new MaterialSpec.PackageMaterial("test.package", "sha256:" + new string('a', 64), "material", EffectParameterOverrides.Parse("{\"strength\":0.1234567890123456789,\"source\":\"preserve\"}"));
        var baseline = f.Workspace.Project.LyricStyle with { Stroke = new() { Material = package, WidthPx = 7, Join = StrokeJoin.Bevel }, Shadow = new() { DistancePx = 5, BlurRadiusPx = 2 } };
        f.Replace(f.Workspace.Project with { LyricStyle = baseline });
        Accepted(await f.Command("0", Patch(Line, new StyleFontFamilyChangeDto(null), new StyleUnsungStrokesChangeDto([]), new StyleUnsungShadowChangeDto(null))));
        var read = await f.Read(Line, "1");
        string wire = JsonSerializer.Serialize(read, Json);
        Assert.Contains("\"property\":\"typography.fontFamilyName\",\"value\":null", wire, StringComparison.Ordinal);
        Assert.Contains("\"property\":\"unsung.strokes\",\"value\":[]", wire, StringComparison.Ordinal);
        Assert.Contains("\"property\":\"unsung.shadow\",\"value\":null", wire, StringComparison.Ordinal);
        Assert.Empty(read.StaticHierarchy.Unsung.Strokes); Assert.Null(read.StaticHierarchy.Unsung.Shadow); Assert.Null(read.StaticHierarchy.Typography.FontFamilyName);
        Assert.Equal(StyleFollowKind.Follow, read.StaticHierarchy.Sung.Strokes.Kind); Assert.Equal(StyleFollowKind.Follow, read.StaticHierarchy.Sung.Shadow.Kind);
        Assert.IsType<PackageStyleMaterialDto>(read.Baseline.Unsung.Strokes[0].Material);
        Assert.DoesNotContain("parameters", wire, StringComparison.Ordinal);
        var noop = Accepted(await f.Command("1", new RestoreStylesCommandDto([Line], ["unsung.shadow.opacity", "sung.shadow.opacity"]))); Assert.False(noop.Changed);
        Accepted(await f.Command("1", Patch(Line, new StyleUnsungShadowOpacityChangeDto(.3f))));
        read = await f.Read(Line, "2"); Assert.Equal(.3f, read.StaticHierarchy.Unsung.Shadow!.Opacity); Assert.Equal(5, read.StaticHierarchy.Unsung.Shadow.DistancePx);
        Assert.Contains(read.Local, value => value is StyleUnsungShadowOpacityChangeDto { Value: .3f });
        Assert.DoesNotContain(read.Local, value => value.Property == "unsung.shadow");
        Assert.Same(package, f.Workspace.Project.LyricStyle.Stroke!.Material);
        Accepted(await f.Command("2", new RestoreStylesCommandDto([Line], ["unsung.strokes", "typography.fontFamilyName"])));
        read = await f.Read(Line, "3"); Assert.Equal(7, read.StaticHierarchy.Unsung.Strokes[0].WidthPx); Assert.Equal(StyleStrokeJoin.Bevel, read.StaticHierarchy.Unsung.Strokes[0].Join);
        Assert.IsType<PackageStyleMaterialDto>(read.StaticHierarchy.Unsung.Strokes[0].Material);
        Assert.DoesNotContain(read.Local, value => value.Property is "unsung.strokes" or "typography.fontFamilyName");
    }

    [Fact]
    public async Task 已唱跟随与显式值读写往返且跟随项分量写成显式部分值()
    {
        using var f = await Fixture.Create();
        f.Replace(f.Workspace.Project with { LyricStyle = f.Workspace.Project.LyricStyle with { Glow = new() { Color = new(9, 8, 7, 255), Opacity = .6f, BlurRadiusPx = 4 } } });
        StyleStrokeDto[] layers = [new(new SolidStyleMaterialDto(new(1, 2, 3, 255)), 3, StyleStrokeJoin.Round), new(new SolidStyleMaterialDto(new(4, 5, 6, 255)), 2, StyleStrokeJoin.Bevel)];
        Accepted(await f.Command("0", Patch(Line, new StyleSungStrokesChangeDto(new(StyleFollowKind.Set, layers)), new StyleSungGlowSpreadChangeDto(2))));
        var read = await f.Read(Line, "1");
        Assert.Equal(StyleFollowKind.Set, read.StaticHierarchy.Sung.Strokes.Kind); Assert.Equal(layers, read.StaticHierarchy.Sung.Strokes.Value);
        Assert.Empty(read.StaticHierarchy.Unsung.Strokes);
        var glow = read.StaticHierarchy.Sung.Glow; Assert.Equal(StyleFollowKind.Set, glow.Kind);
        Assert.Equal(2, glow.Value!.SpreadPx); Assert.Equal(4, glow.Value.BlurRadiusPx); Assert.Equal(.6f, glow.Value.Opacity);
        Assert.Contains(read.Local, value => value is StyleSungGlowSpreadChangeDto { Value: 2 });
        string wire = JsonSerializer.Serialize(read.StaticHierarchy, Json);
        Assert.Contains("\"shadow\":{\"kind\":\"follow\"}", wire, StringComparison.Ordinal);
        var follow = Accepted(await f.Command("1", Patch(Line, new StyleSungStrokesChangeDto(new(StyleFollowKind.Follow)))));
        Assert.True(follow.Changed);
        read = await f.Read(Line, "2"); Assert.Equal(StyleFollowKind.Follow, read.StaticHierarchy.Sung.Strokes.Kind);
        Assert.Contains(read.Local, value => value is StyleSungStrokesChangeDto { Value.Kind: StyleFollowKind.Follow });
        Accepted(await f.Command("2", Patch(Line, new StyleWipeFeatherChangeDto(.2), new StyleWipeHighlightBlendChangeDto(StyleWipeHighlightBlend.Add))));
        read = await f.Read(Line, "3"); Assert.Equal(.2, read.StaticHierarchy.Wipe.FeatherRatio);
        Assert.Equal(StyleWipeHighlightBlend.Add, read.StaticHierarchy.Wipe.Highlight!.Blend); Assert.Equal(.25, read.StaticHierarchy.Wipe.Highlight.WidthRatio);
        Assert.Contains("\"blend\":\"add\"", JsonSerializer.Serialize(read.StaticHierarchy, Json), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 字体族空串空白均保持Core合法语义(string value)
    {
        using var f = await Fixture.Create();
        Accepted(await f.Command("0", Patch(Line, new StyleFontFamilyChangeDto(value))));
        Assert.Equal(value, (await f.Read(Line, "1")).StaticHierarchy.Typography.FontFamilyName);
    }

    [Fact]
    public async Task 完整渐变几何等位置色标与数组内容相等不制造历史()
    {
        using var f = await Fixture.Create();
        var gradient = new StyleGradientDto(StyleGradientKind.Linear, 123, -3, 4, -5, -6, StyleGradientCoordinateSpace.Glyph,
            StyleGradientColorSpace.Oklch, StyleGradientHuePath.Decreasing,
            [new(0, new(1, 2, 3, 4)), new(.5, new(5, 6, 7, 8)), new(.5, new(9, 10, 11, 12)), new(1, new(13, 14, 15, 16))]);
        var command = Patch(Line, new StyleUnsungFillChangeDto(new GradientStyleMaterialDto(gradient)), new StyleFallbackFamiliesChangeDto(["A", "B"]));
        Accepted(await f.Command("0", command)); var before = f.Workspace.Project;
        var copied = (PatchStylesCommandDto)JsonSerializer.Deserialize<ProjectCommandDto>(JsonSerializer.Serialize<ProjectCommandDto>(command, Json), Json)!;
        var result = Accepted(await f.Command("1", copied)); Assert.False(result.Changed); Assert.Same(before, f.Workspace.Project);
        var read = await f.Read(Line, "1"); var actual = Assert.IsType<GradientStyleMaterialDto>(read.StaticHierarchy.Unsung.Fill).Gradient;
        Assert.Equal(gradient, actual with { Stops = gradient.Stops }); Assert.Equal(gradient.Stops, actual.Stops);
        Assert.Equal(1, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 超128及前后空格稳定标识四层读写保持精确不trim()
    {
        using var f = await Fixture.Create(); string branch = " " + new string('b', 129) + " "; string line = " " + new string('l', 129) + " "; string token = " " + new string('t', 129) + " ";
        var secondary = LyricBranch.Create(branch, "long", lines: [LyricLine.Create(line, "字", tokens: [LyricToken.Create(token, "字")])]);
        f.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics with { Branches = [f.Workspace.Project.Lyrics.Branches[0], secondary] } });
        StyleTargetDto[] targets = [new BranchStyleTargetDto(branch), new LineStyleTargetDto(branch, line), new TokenStyleTargetDto(branch, line, token)];
        for (int i = 0; i < targets.Length; i++)
        {
            Accepted(await f.Command(i.ToString(CultureInfo.InvariantCulture), Patch(targets[i], new StyleOpacityChangeDto(.5f))));
            Assert.Equal(targets[i], (await f.Read(targets[i], (i + 1).ToString(CultureInfo.InvariantCulture))).Target);
        }
        var missing = await f.Registry.ReadStyleAsync("client", f.Session.ProjectSessionId, "3", new BranchStyleTargetDto(branch.Trim()));
        Assert.Equal("TargetStale", missing.Error!.FailureCode); Assert.Equal("primary", f.Workspace.Project.Lyrics.PrimaryBranchId);
    }

    [Theory]
    [InlineData("typography.fontSizePx")]
    [InlineData("verticalCenterRatio")]
    [InlineData("horizontalAlignment")]
    [InlineData("scale")]
    public async Task 元素不支持的属性领域拒绝且整批历史不变(string property)
    {
        using var f = await Fixture.Create();
        StyleChangeDto change = property switch { "typography.fontSizePx" => new StyleFontSizeChangeDto(80), "verticalCenterRatio" => new StyleVerticalCenterChangeDto(.7), "horizontalAlignment" => new StyleAlignmentChangeDto(StyleHorizontalAlignment.Left), _ => new StyleScaleChangeDto(2) };
        var source = f.Workspace.Project; var rejected = await f.Command("0", Patch(new TokenStyleTargetDto("secondary", "other", "other-token"), change));
        Assert.Equal(BackendErrorCode.CommandRejected, rejected.Error!.Code); Assert.Equal("UnsupportedProperty", rejected.Error.FailureCode);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 只读跨client关闭及修订冲突不修改样式()
    {
        using var f = await Fixture.Create(); var source = f.Workspace.Project;
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadStyleAsync("other", f.Session.ProjectSessionId, "0", Line)).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadStyleAsync("client", f.Session.ProjectSessionId, "1", Line)).Error!.Code);
        Set(f.Session, "IsReadOnly", true);
        Assert.Equal(BackendErrorCode.ReadOnlySession, (await f.Command("0", Patch(Line, new StyleOpacityChangeDto(.5f)))).Error!.Code);
        Assert.True((await f.Registry.ReadStyleAsync("client", f.Session.ProjectSessionId, "0", Line)).Accepted);
        Set(f.Session, "IsReadOnly", false); Set(f.Session, "IsClosing", true);
        Assert.Equal(BackendErrorCode.SessionClosing, (await f.Command("0", Patch(Line, new StyleOpacityChangeDto(.5f)))).Error!.Code);
        Set(f.Session, "IsClosed", true);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadStyleAsync("client", f.Session.ProjectSessionId, "0", Line)).Error!.Code);
        Assert.Same(source, f.Workspace.Project);
    }

    [Fact]
    public async Task 并发同修订仅一次提交且同回执重试结果一致()
    {
        using var f = await Fixture.Create(); var request = f.Request("0", Patch(Line, new StyleOpacityChangeDto(.4f)));
        var results = await Task.WhenAll(f.Execute(request), f.Execute(request), f.Command("0", Patch(Line, new StyleOpacityChangeDto(.3f))));
        Assert.Equal(2, results.Count(result => result.Accepted)); Assert.Equal(JsonSerializer.Serialize(results[0].Response, Json), JsonSerializer.Serialize(results[1].Response, Json));
        Assert.Equal(BackendErrorCode.RevisionConflict, results[2].Error!.Code); Assert.Equal(1, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 排队前递归固定数组并保留非法继承字段的回执身份()
    {
        using var f = await Fixture.Create(); var writer = (SemaphoreSlim)Get(f.Session, "Writer")!; await writer.WaitAsync();
        var fallback = new[] { "before" }; var changes = new StyleChangeDto[] { new StyleFallbackFamiliesChangeDto(fallback) }; var targets = new StyleTargetDto[] { Line };
        var request = f.Request("0", new PatchStylesCommandDto(targets, changes)); Task<ProjectCommandExecutionResult> running = f.Execute(request);
        fallback[0] = "after"; changes[0] = new StyleFontSizeChangeDto(100); targets[0] = new ProjectStyleTargetDto(); writer.Release();
        Accepted(await running); Assert.Equal(["before"], (await f.Read(Line, "1")).StaticHierarchy.Typography.FallbackFamilyNames);
        var sameId = request with { Command = Patch(Line, new StyleOpacityChangeDto(.5f)) };
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(sameId)).Error!.Code);
        var malformed = f.Request("1", Patch(Line, new StyleOpacityChangeDto(.5f)) with { StartUs = "1" });
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Execute(malformed)).Error!.Code);
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(malformed with { Command = Patch(Line, new StyleOpacityChangeDto(.5f)) })).Error!.Code);
    }

    [Fact]
    public async Task 旧超大值可等值空操作及恢复修复但实际小修改先完整投影拒绝()
    {
        using var f = await Fixture.Create(); var branch = f.Workspace.Project.Lyrics.Branches[1];
        var old = branch.Lines[0] with { StyleOverride = new() { Typography = new() { FontFamilyName = new string('汉', 180000) }, Opacity = .5f } };
        f.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics.ReplaceBranch(branch with { Lines = [old] }) });
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.ReadStyleAsync("client", f.Session.ProjectSessionId, "0", Line)).Error!.Code);
        var source = f.Workspace.Project;
        Assert.False(Accepted(await f.Command("0", Patch(Line, new StyleOpacityChangeDto(.5f)))).Changed);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", Patch(Line, new StyleOpacityChangeDto(.6f)))).Error!.Code);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
        Accepted(await f.Command("0", new RestoreStylesCommandDto([Line], ["typography.fontFamilyName"])));
        Assert.True((await f.Registry.ReadStyleAsync("client", f.Session.ProjectSessionId, "1", Line)).Accepted);
    }

    [Fact]
    public async Task 改变目标累计完整投影十六MiB拒绝末段而不部分提交()
    {
        using var f = await Fixture.Create(); var branch = f.Workspace.Project.Lyrics.Branches[1];
        var lines = Enumerable.Range(0, 40).Select(i => LyricLine.Create("line-" + i, "字") with
            { StyleOverride = new() { Typography = new() { FontFamilyName = new string('汉', 42000) } } }).ToImmutableArray();
        f.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics.ReplaceBranch(branch with { Lines = lines }) });
        Assert.True((await f.Registry.ReadStyleAsync("client", f.Session.ProjectSessionId, "0", new LineStyleTargetDto("secondary", "line-0"))).Accepted);
        var source = f.Workspace.Project; var targets = lines.Select(l => (StyleTargetDto)new LineStyleTargetDto("secondary", l.Id)).ToArray();
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", new PatchStylesCommandDto(targets, [new StyleOpacityChangeDto(.4f)]))).Error!.Code);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 新值展开预算先于目标准备且意图字体回退和色标均有界()
    {
        using var f = await Fixture.Create();
        var source = f.Workspace.Project;
        var changes = new StyleChangeDto[] { new StyleFallbackFamiliesChangeDto(Enumerable.Range(0, 32).Select(i => i + new string('f', 250)).ToArray()) };
        var targets = Enumerable.Range(0, 800).Select(i => (StyleTargetDto)new LineStyleTargetDto("s", i.ToString(CultureInfo.InvariantCulture))).ToArray();
        // ASCII is compact enough that the command still fits; escaped Unicode expands the shared new-value payload.
        changes[0] = new StyleFallbackFamiliesChangeDto(Enumerable.Range(0, 32).Select(i => i + new string('汉', 110)).ToArray());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes<ProjectCommandDto>(new PatchStylesCommandDto(targets, changes), Json).Length <= StyleEditingLimits.MaxIntentBytes);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(changes, Json).LongLength * targets.Length > StyleEditingLimits.MaxPreparedBytes);
        var rejected = await f.Command("0", new PatchStylesCommandDto(targets, changes));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, rejected.Error!.Code); Assert.Same(source, f.Workspace.Project);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", Patch(Line, new StyleFontFamilyChangeDto(new string('f', 257))))).Error!.Code);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", Patch(Line, new StyleFallbackFamiliesChangeDto(new string[33])))).Error!.Code);
    }

    [Fact]
    public async Task 完整实际配置回执预算在提交前拒绝且大changeSet仅全清IDs()
    {
        using var limited = await Fixture.Create(1024); var branch = limited.Workspace.Project.Lyrics.Branches[1];
        var lines = Enumerable.Range(0, 30).Select(i => LyricLine.Create(new string('l', 100) + i, "字")).ToImmutableArray();
        limited.Replace(limited.Workspace.Project with { Lyrics = limited.Workspace.Project.Lyrics.ReplaceBranch(branch with { Lines = lines }) });
        var before = limited.Workspace.Project;
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await limited.Command("0", new PatchStylesCommandDto(lines.Select(l => (StyleTargetDto)new LineStyleTargetDto("secondary", l.Id)).ToArray(), [new StyleOpacityChangeDto(.5f)]))).Error!.Code);
        Assert.Same(before, limited.Workspace.Project); Assert.Equal(0, (await limited.History()).UndoCount);
        using var f = await Fixture.Create(); branch = f.Workspace.Project.Lyrics.Branches[1];
        lines = Enumerable.Range(0, 200).Select(i => LyricLine.Create(new string('l', 100) + i, "字")).ToImmutableArray();
        f.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics.ReplaceBranch(branch with { Lines = lines }) });
        var result = Accepted(await f.Command("0", new PatchStylesCommandDto(lines.Select(l => (StyleTargetDto)new LineStyleTargetDto("secondary", l.Id)).ToArray(), [new StyleOpacityChangeDto(.5f)])));
        Assert.Equal(200, result.StyleResult!.ChangedTargetCount); Assert.Empty(result.ChangeSet.LineIds); Assert.Empty(result.ChangeSet.BranchIds); Assert.Empty(result.ChangeSet.TokenIds);
        Assert.Equal(["styles", "renderInputs"], result.ChangeSet.Areas); Assert.True(result.ChangeSet.RequiresFullRefresh);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 已载入坏样式读取有结构化拒绝且无关修改不500(int kind)
    {
        using var f = await Fixture.Create(); var source = f.Workspace.Project;
        LyricStyle invalid = kind switch
        {
            0 => source.LyricStyle with { Typography = source.LyricStyle.Typography with { FontSizePx = 0 } },
            1 => source.LyricStyle with { UnsungFill = new() { Material = new MaterialSpec.Gradient(new() { Stops = [new(1, RgbaColor.White), new(0, RgbaColor.White)] }) } },
            _ => source.LyricStyle with { Shadow = new() { Opacity = float.NaN } },
        };
        f.Replace(source with { LyricStyle = invalid });
        source = f.Workspace.Project;
        var read = await f.Registry.ReadStyleAsync("client", f.Session.ProjectSessionId, "0", new ProjectStyleTargetDto());
        Assert.Equal(BackendErrorCode.CommandRejected, read.Error!.Code); Assert.Equal("InvalidValue", read.Error.FailureCode); Assert.Equal("style", read.Error.Field);
        var rejected = await f.Command("0", Patch(new ProjectStyleTargetDto(), new StyleOpacityChangeDto(.5f)));
        Assert.Equal(BackendErrorCode.CommandRejected, rejected.Error!.Code); Assert.Same(source, f.Workspace.Project);
        var corresponding = await f.Command("0", Patch(new ProjectStyleTargetDto(), new StyleFontSizeChangeDto(80)));
        Assert.Equal(BackendErrorCode.CommandRejected, corresponding.Error!.Code); Assert.Equal("InvalidValue", corresponding.Error.FailureCode);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task 进程内非有限值在序列化门禁400不触及工程或回执(float value)
    {
        using var f = await Fixture.Create(); var source = f.Workspace.Project;
        var gradient = new StyleGradientDto(StyleGradientKind.Linear, value, .5, .5, .5, .5, StyleGradientCoordinateSpace.Line,
            StyleGradientColorSpace.LinearRgb, StyleGradientHuePath.Shorter, [new(0, new(1,2,3,4)), new(1, new(5,6,7,8))]);
        foreach (StyleChangeDto change in new StyleChangeDto[] { new StyleScaleChangeDto(value), new StyleUnsungShadowChangeDto(new(new(1,2,3,4), value, 0, 1, 2)), new StyleUnsungFillChangeDto(new GradientStyleMaterialDto(gradient)) })
        {
            var request = f.Request("0", Patch(new ProjectStyleTargetDto(), change));
            Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Execute(request)).Error!.Code);
            // Failed serialization admission has not pinned a receipt identity.
            Accepted(await f.Execute(request with { Command = Patch(new ProjectStyleTargetDto(), new StyleOpacityChangeDto(1)) }));
        }
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 旧超新增输入限额的字体回退与渐变仍可读且局部修改保留()
    {
        using var f = await Fixture.Create();
        var g = new GradientSpec { Stops = [.. Enumerable.Range(0, 300).Select(i => new GradientStop(i / 299d, new(1, 2, 3, 4)))] };
        var style = f.Workspace.Project.LyricStyle with { Typography = f.Workspace.Project.LyricStyle.Typography with
            { FontFamilyName = new string('f', 300), FallbackFamilyNames = [.. Enumerable.Range(0, 33).Select(i => "fallback" + i)] }, UnsungFill = new() { Material = new MaterialSpec.Gradient(g) } };
        f.Replace(f.Workspace.Project with { LyricStyle = style });
        var read = await f.Read(Line, "0"); Assert.Equal(300, Assert.IsType<GradientStyleMaterialDto>(read.StaticHierarchy.Unsung.Fill).Gradient.Stops.Length);
        Assert.Equal(33, read.StaticHierarchy.Typography.FallbackFamilyNames.Length);
        Accepted(await f.Command("0", Patch(Line, new StyleOpacityChangeDto(.3f))));
        Assert.Same(style, f.Workspace.Project.LyricStyle); Assert.Same(g, ((MaterialSpec.Gradient)f.Workspace.Project.LyricStyle.UnsungFill.Material).Value);
        Assert.Equal(300, (await f.Read(Line, "1")).StaticHierarchy.Typography.FontFamilyName!.Length);
    }

    [Fact]
    public async Task 读取项数先于旧渐变大数组投影分配拒绝()
    {
        using var f = await Fixture.Create();
        var gradient = new GradientSpec { Stops = [.. Enumerable.Range(0, 20001).Select(i => new GradientStop(i / 20000d, new(1,2,3,4)))] };
        f.Replace(f.Workspace.Project with { LyricStyle = f.Workspace.Project.LyricStyle with { UnsungFill = new() { Material = new MaterialSpec.Gradient(gradient) } } });
        var source = f.Workspace.Project; var read = await f.Registry.ReadStyleAsync("client", f.Session.ProjectSessionId, "0", new ProjectStyleTargetDto());
        Assert.Equal(BackendErrorCode.ResourceTooLarge, read.Error!.Code); Assert.Equal("style", read.Error.Field); Assert.Same(source, f.Workspace.Project);
    }

    [Fact]
    public async Task 预提交完整读预算按真实九到十修订位数增长拒绝()
    {
        using var f = await Fixture.Create(); Set(f.Session, "ProjectRevision", 9L);
        var branch = f.Workspace.Project.Lyrics.Branches[1]; string lineId = "edge";
        void Seed(int length) => f.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics.ReplaceBranch(branch with
            { Lines = [LyricLine.Create(lineId, "字") with { StyleOverride = new() { Typography = new() { FontFamilyName = new string('a', length) }, Opacity = .5f } }] }) });
        Seed(0); var target = new LineStyleTargetDto("secondary", lineId);
        int bytes = JsonSerializer.SerializeToUtf8Bytes(await f.Read(target, "9"), Json).Length;
        if ((ProjectReadModelLimits.MaxResponseBytes - bytes) % 2 != 0) { lineId += "x"; Seed(0); target = new("secondary", lineId); bytes = JsonSerializer.SerializeToUtf8Bytes(await f.Read(target, "9"), Json).Length; }
        Seed((ProjectReadModelLimits.MaxResponseBytes - bytes) / 2);
        Assert.Equal(ProjectReadModelLimits.MaxResponseBytes, JsonSerializer.SerializeToUtf8Bytes(await f.Read(target, "9"), Json).Length);
        var source = f.Workspace.Project;
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("9", Patch(target, new StyleOpacityChangeDto(.6f)))).Error!.Code);
        Assert.Same(source, f.Workspace.Project); Assert.Equal("9", (await f.History()).ProjectRevision); Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 排队前递归固定渐变色标数组()
    {
        using var f = await Fixture.Create(); var writer = (SemaphoreSlim)Get(f.Session, "Writer")!; await writer.WaitAsync();
        var stops = new StyleGradientStopDto[] { new(0, new(1,2,3,4)), new(.5, new(5,6,7,8)), new(1, new(9,10,11,12)) };
        var gradient = new StyleGradientDto(StyleGradientKind.Linear, 0, .5, .5, .5, .5, StyleGradientCoordinateSpace.Line, StyleGradientColorSpace.LinearRgb, StyleGradientHuePath.Shorter, stops);
        var task = f.Command("0", Patch(Line, new StyleUnsungFillChangeDto(new GradientStyleMaterialDto(gradient))));
        stops[1] = new(.8, new(100,101,102,103)); writer.Release(); Accepted(await task);
        var actual = Assert.IsType<GradientStyleMaterialDto>((await f.Read(Line, "1")).StaticHierarchy.Unsung.Fill).Gradient;
        Assert.Equal(.5, actual.Stops[1].Offset); Assert.Equal(new StyleColorDto(5,6,7,8), actual.Stops[1].Color);
    }

    [Fact]
    public async Task 非法typed判别符不能经进程内with偷换历史或属性()
    {
        using var f = await Fixture.Create();
        ProjectCommandDto[] commands = [Patch(Line, new StyleOpacityChangeDto(.5f)) with { Type = "history.undo" },
            Patch(Line, new StyleFontSizeChangeDto(80) with { Property = "stroke.widthPx" }),
            Patch(new BranchStyleTargetDto("secondary") with { Kind = "project" }, new StyleOpacityChangeDto(.5f))];
        foreach (var command in commands) Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("0", command)).Error!.Code);
        Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 固定预设存在提示是全工程信息且静态四层不执行应用()
    {
        using var f = await Fixture.Create();
        var preset = StyleTemplate.Create("preset", "fixed", StyleTemplateKind.Composite, f.Workspace.Project.LyricStyle with { Opacity = .9f }, [LyricStyleProperty.Opacity]);
        var applications = new PresetApplications([new("application", new(ProjectEffectBindingKind.Branch, "primary"), preset)]);
        f.Replace(f.Workspace.Project with { LyricStyle = f.Workspace.Project.LyricStyle with { Opacity = .4f }, PresetApplications = applications });
        var primary = await f.Read(new LineStyleTargetDto("primary", "one"), "0");
        var secondary = await f.Read(Line, "0"); Assert.True(primary.HasPresetApplications); Assert.True(secondary.HasPresetApplications);
        Assert.Equal(.4f, primary.StaticHierarchy.Opacity); Assert.Equal(.4f, secondary.StaticHierarchy.Opacity);
        Accepted(await f.Command("0", Patch(Line, new StyleOpacityChangeDto(.5f)))); Assert.Same(applications, f.Workspace.Project.PresetApplications);
    }

    private static object? Get(object value, string property) => value.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(value);
    private static void Set(object value, string property, object data) => value.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(value, data);
    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ProjectSessionRegistry Registry = null!; internal ProjectSession Session = null!; internal ProjectWorkspace Workspace = null!;
        private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        internal static async Task<Fixture> Create(int receiptBytes = 64 * 1024)
        {
            Fixture f = new(); ResourceStore store = new(new() { ResourceRoot = Path.Combine(f._directory.Path, "resources") });
            StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]甲乙\n"u8.ToArray()));
            StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            WaveformPeakBuilder builder = new(); builder.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), builder.Build(8000));
            f.Registry = new("host", store, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)), new FixedClock(), new() { MaxReceiptBytes = receiptBytes });
            f.Session = (await f.Registry.CreateAsync("client", new("styles", audio.AssetId, lyrics.AssetId))).Session!;
            f.Workspace = (ProjectWorkspace)Get(f.Session, "Workspace")!;
            LyricLine primary = LyricLine.Create("one", "甲", tokens: [LyricToken.Create("first", "甲")]);
            LyricLine secondary = LyricLine.Create("other", "乙", tokens: [LyricToken.Create("other-token", "乙")]);
            var document = LyricsDocument.Create([primary]);
            f.Replace(f.Workspace.Project with { Lyrics = document with { Branches = [document.Branches[0], LyricBranch.Create("secondary", "second", lines: [secondary])] } });
            return f;
        }
        internal void Replace(Project project) => Workspace.Replace(project);
        internal async Task<ProjectStyleResponse> Read(StyleTargetDto target, string revision) { var result = await Registry.ReadStyleAsync("client", Session.ProjectSessionId, revision, target); Assert.True(result.Accepted, result.Error?.Message); return result.Response!; }
        internal async Task<ProjectHistoryResponse> History() => (await Registry.ReadHistoryAsync("client", Session.ProjectSessionId))!;
        internal ProjectCommandRequest Request(string revision, ProjectCommandDto command) => new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, Now.AddMinutes(5));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Execute(Request(revision, command));
        internal Task<ProjectCommandExecutionResult> Execute(ProjectCommandRequest request) => Registry.ExecuteCommandAsync("client", Session.ProjectSessionId, request);
        public void Dispose() { Registry.Dispose(); _directory.Dispose(); }
        private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    }
}
