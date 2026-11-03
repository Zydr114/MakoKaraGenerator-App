using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendTemplateLibraryTests
{
    [Fact]
    public void Full定义wire保留包材质参数配方顺序和UInt64种子()
    {
        var style = Style with
        {
            UnsungFill = new(new PackageStyleMaterialDto("pkg", "rev", "mat", "{\"large\":18446744073709551615}")),
        };
        var recipe = new TemplateEffectRecipeDto("r", "配方", TemplateEffectTargetDto.Line, "pkg", "rev", "fx",
            "{\"large\":18446744073709551615}", TemplateEffectOverlayLayerDto.Package, true,
            TemplateScriptReadScopeDto.BoundSources, null, "18446744073709551615", null);
        var source = new FullTemplateDefinitionDto("完整", TemplateKind.Line, style, [TemplateProperty.UnsungColor], [recipe], []);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        string json = JsonSerializer.Serialize<StaticTemplateDefinitionDto>(source, options);
        Assert.Contains("effectRecipes", json); Assert.Contains("parametersJson", json); Assert.Contains("18446744073709551615", json);
        var roundTrip = JsonSerializer.Deserialize<FullTemplateDefinitionDto>(json, options)!;
        Assert.Equal(source.Name, roundTrip.Name);
        Assert.Equal(source.EffectRecipes[0].FixedSeed, roundTrip.EffectRecipes[0].FixedSeed);
        Assert.Equal(source.EffectRecipes[0].ParametersJson, roundTrip.EffectRecipes[0].ParametersJson);
        Assert.Equal(source.Style.UnsungFill, roundTrip.Style.UnsungFill);
    }

    [Fact]
    public async Task Full静态定义可通过create并由full详情读回()
    {
        await using Fixture f = new();
        var glow = new StyleGlowDto(new(20, 30, 40, 255), .7f, 9, 3);
        var definition = new FullTemplateDefinitionDto("full静态", TemplateKind.Line, Style with { Glow = glow }, [TemplateProperty.FontSizePx, TemplateProperty.GlowSpec], [], []);
        var created = Accepted(await f.Command(f.Request("missing", new CreateTemplateCommandDto(definition, "full"))));
        var detail = Accepted(await f.Registry.ReadTemplateAsync("personal", created.Revision, created.TemplateId, "full"));
        Assert.Equal("full", detail.DefinitionFormat);
        var full = Assert.IsType<FullTemplateDefinitionDto>(detail.Definition);
        Assert.Equal(definition.Name, full.Name);
        Assert.Equal(glow, full.Style.Glow); Assert.Contains(TemplateProperty.GlowSpec, full.Properties);
        Assert.Empty(full.EffectRecipes);
        Assert.Empty(full.Packages);
    }

    [Fact]
    public async Task 静态模板辉光通过创建和替换读回()
    {
        await using Fixture f = new();
        var glow = new StyleGlowDto(new(10, 20, 30, 255), .6f, 7, 2);
        var definition = new StaticTemplateDefinitionDto("辉光", TemplateKind.Token, Style with { Glow = glow }, [TemplateProperty.GlowSpec]);
        var created = Accepted(await f.Command(f.Request("missing", new CreateTemplateCommandDto(definition))));
        var detail = Accepted(await f.Registry.ReadTemplateAsync("personal", created.Revision, created.TemplateId));
        Assert.Equal(glow, detail.Definition!.Style.Glow); Assert.Contains(TemplateProperty.GlowSpec, detail.Definition.Properties);
        var replacement = definition with { Style = definition.Style with { Glow = glow with { SpreadPx = 5 } } };
        var replaced = Accepted(await f.Command(f.Request(created.Revision, new ReplaceTemplateCommandDto(created.TemplateId, replacement))));
        var reread = Accepted(await f.Registry.ReadTemplateAsync("personal", replaced.Revision, created.TemplateId));
        Assert.Equal(5, reread.Definition!.Style.Glow!.SpreadPx);
    }

    private static CanonicalStyleDto Style => ProjectSession.ExportTemplateLibraryStyle(LyricStyle.Default);
    private static StaticTemplateDefinitionDto Definition(string name = "静态模板") => new(name, TemplateKind.Line, Style, [TemplateProperty.FontSizePx]);
    private static StyleGradientDto Gradient(StyleGradientCoordinateSpace coordinateSpace, StyleGradientColorSpace colorSpace,
        StyleGradientHuePath huePath) => new(StyleGradientKind.Linear, 0, .5, .5, .5, .5, coordinateSpace, colorSpace, huePath,
        [new(0, new(0, 0, 0, 255)), new(1, new(255, 255, 255, 255))]);
    private static StaticTemplateDefinitionDto GradientDefinition(string name, StyleGradientCoordinateSpace coordinateSpace,
        StyleGradientColorSpace colorSpace, StyleGradientHuePath huePath) => new(name, TemplateKind.Line,
        Style with
        {
            UnsungFill = new(new GradientStyleMaterialDto(Gradient(StyleGradientCoordinateSpace.Line, colorSpace, huePath))),
            SungFill = new(new GradientStyleMaterialDto(Gradient(StyleGradientCoordinateSpace.Line, colorSpace,
                huePath == StyleGradientHuePath.Shorter ? StyleGradientHuePath.Longer : huePath))),
            Stroke = new(new GradientStyleMaterialDto(Gradient(coordinateSpace, StyleGradientColorSpace.LinearRgb, huePath)), 2, StyleStrokeJoin.Round, StyleStrokePosition.Outside),
        }, [TemplateProperty.UnsungGradientSpec, TemplateProperty.SungGradientSpec]);
    private static T Accepted<T>(BackendOperationResult<T> result) { Assert.True(result.Accepted, result.Error?.ToString()); return result.Response!; }

    [Fact]
    public void 模板原21枚举与Core数值名称全集相同且完整canonical逐字段回环()
    {
        Assert.Equal(Enum.GetValues<StyleTemplateKind>().Select(x => ((int)x, x.ToString())), Enum.GetValues<TemplateKind>().Select(x => ((int)x, x.ToString())));
        Assert.Equal(Enum.GetValues<LyricStyleProperty>().Select(x => ((int)x, x.ToString())), Enum.GetValues<TemplateProperty>().Select(x => ((int)x, x.ToString())));
        var gradient = new GradientSpec { Kind = GradientKind.Radial, AngleDegrees = 7.1234567890123, CenterX = .7, CenterY = .8,
            RadiusX = .4, RadiusY = .5, CoordinateSpace = GradientCoordinateSpace.Progress, ColorSpace = GradientColorSpace.Oklch,
            HuePath = GradientHuePath.Decreasing, Stops = [new(0, new(1,2,3,4)), new(1, new(5,6,7,8))] };
        var source = LyricStyle.Default with { Typography = new() { FontFamilyName = "测试font", FallbackFamilyNames = ["甲", "乙"], FontSizePx = 63,
            FontWeight = 333, IsBold = true, IsItalic = true, LetterSpacingPx = -3 }, UnsungFill = new() { Material = new MaterialSpec.Gradient(gradient) },
            SungFill = new() { Material = new MaterialSpec.Solid(new(10,20,30,40)) }, Stroke = new() { Material = new MaterialSpec.Gradient(gradient),
                WidthPx = 3.75f, Join = StrokeJoin.Bevel, Position = StrokePosition.Outside }, Shadow = new() { Color = new(50,60,70,80), Opacity = .4f,
                AngleDegrees = -123.123456789, DistancePx = 7.2f, BlurRadiusPx = 8.5f }, Glow = new() { Color = new(1,2,3,4), Opacity = .4f, BlurRadiusPx = 6, SpreadPx = 2 }, Opacity = .6f, Scale = 1.3f, VerticalCenterRatio = .3,
            HorizontalAlignment = LyricHorizontalAlignment.Right };
        Assert.Equal(source, ProjectSession.ImportTemplateLibraryStyle(ProjectSession.ExportTemplateLibraryStyle(source)));
        foreach (var property in Enum.GetValues<LyricStyleProperty>())
        {
            var template = StyleTemplate.Create("x", "x", StyleTemplateKind.Line, source, [property]);
            Assert.Equal(template.Style, ProjectSession.ImportTemplateLibraryStyle(ProjectSession.ExportTemplateLibraryStyle(template.Style)));
        }
    }

    [Fact]
    public async Task 共享个人库CRUD精确修订分页与详情及跨客户端读取不产生工程事件()
    {
        await using Fixture f = new(); Assert.Equal(["builtin", "personal"], f.Registry.ReadTemplateLibraries().Libraries.Select(x => x.LibraryId));
        Assert.Equal("missing", Accepted(await f.Registry.ReadTemplatesAsync("personal", null)).Revision);
        var request = f.Request("missing", new CreateTemplateCommandDto(Definition()));
        var created = Accepted(await f.Command(request)); Assert.True(created.Changed); Assert.Equal("missing", created.PreviousRevision);
        var page = Accepted(await f.Registry.ReadTemplatesAsync("personal", created.Revision)); Assert.Single(page.Templates);
        var detail = Accepted(await f.Registry.ReadTemplateAsync("personal", created.Revision, created.TemplateId));
        Assert.Equal("staticAvailable", detail.DefinitionAvailability); Assert.NotNull(detail.Definition);
        var unchanged = Accepted(await f.Registry.ExecuteTemplateLibraryCommandAsync("other-client", "personal", f.Request(created.Revision, new ReplaceTemplateCommandDto(created.TemplateId, Definition()))));
        Assert.False(unchanged.Changed); Assert.Equal(created.Revision, unchanged.Revision);
        var conflict = await f.Command(f.Request("missing", new RenameTemplateCommandDto(created.TemplateId, "丢失")));
        Assert.Equal(BackendErrorCode.RevisionConflict, conflict.Error?.Code);
        var renamed = Accepted(await f.Command(f.Request(created.Revision, new RenameTemplateCommandDto(created.TemplateId, "改名"))));
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadTemplatesAsync("personal", created.Revision)).Error?.Code);
        var removed = Accepted(await f.Command(f.Request(renamed.Revision, new DeleteTemplateCommandDto(created.TemplateId))));
        Assert.Empty(Accepted(await f.Registry.ReadTemplatesAsync("personal", removed.Revision)).Templates);
        Assert.Equal(created, Accepted(await f.Command(request))); // Replay precedes existence/revision checks.
        Assert.Empty(f.Registry.ReadEvents("client", null).Events);
    }

    [Fact]
    public async Task 个人库渐变变体可读回并继续替换删除()
    {
        await using Fixture f = new();
        StaticTemplateDefinitionDto definition = GradientDefinition("变体", StyleGradientCoordinateSpace.Glyph,
            StyleGradientColorSpace.Oklch, StyleGradientHuePath.Shorter);
        var created = Accepted(await f.Command(f.Request("missing", new CreateTemplateCommandDto(definition))));
        var page = Accepted(await f.Registry.ReadTemplatesAsync("personal", created.Revision));
        Assert.Single(page.Templates);
        var detail = Accepted(await f.Registry.ReadTemplateAsync("personal", created.Revision, created.TemplateId));
        Assert.Equal("staticAvailable", detail.DefinitionAvailability);
        Assert.Equal(StyleGradientColorSpace.Oklch, ((GradientStyleMaterialDto)detail.Definition!.Style.UnsungFill.Material).Gradient.ColorSpace);
        Assert.Equal(StyleGradientCoordinateSpace.Glyph, ((GradientStyleMaterialDto)detail.Definition.Style.Stroke!.Material).Gradient.CoordinateSpace);

        StaticTemplateDefinitionDto replacement = GradientDefinition("替换", StyleGradientCoordinateSpace.Progress,
            StyleGradientColorSpace.Oklch, StyleGradientHuePath.Longer);
        var replaced = Accepted(await f.Command(f.Request(created.Revision,
            new ReplaceTemplateCommandDto(created.TemplateId, replacement))));
        var replacedDetail = Accepted(await f.Registry.ReadTemplateAsync("personal", replaced.Revision, created.TemplateId));
        Assert.Equal("替换", replacedDetail.Summary.Name);
        Assert.Equal(StyleGradientCoordinateSpace.Progress, ((GradientStyleMaterialDto)replacedDetail.Definition!.Style.Stroke!.Material).Gradient.CoordinateSpace);

        var deleted = Accepted(await f.Command(f.Request(replaced.Revision, new DeleteTemplateCommandDto(created.TemplateId))));
        Assert.Empty(Accepted(await f.Registry.ReadTemplatesAsync("personal", deleted.Revision)).Templates);
    }

    [Fact]
    public async Task 回执同瞬间不同时区复用而截止意图变化拒绝()
    {
        await using Fixture f = new(); var request = f.Request("missing", new CreateTemplateCommandDto(Definition()));
        var created = Accepted(await f.Command(request));
        var offset = request with { Metadata = request.Metadata with { ValidUntilUtc = request.Metadata.ValidUntilUtc!.Value.ToOffset(TimeSpan.FromHours(8)) } };
        Assert.Equal(created, Accepted(await f.Command(offset)));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Command(offset with { Metadata = offset.Metadata with { ValidUntilUtc = offset.Metadata.ValidUntilUtc!.Value.AddSeconds(1) } })).Error?.Code);
    }

    [Fact]
    public async Task 内置复制精确源修订且坏个人库不阻内置与发现()
    {
        await using Fixture f = new(); var builtIn = Accepted(await f.Registry.ReadTemplatesAsync("builtin", null)); Assert.Equal(2, builtIn.TotalCount);
        var request = f.Request("missing", new CopyTemplateCommandDto("builtin", builtIn.Revision, builtIn.Templates[0].TemplateId, "复制"));
        var copied = Accepted(await f.Command(request)); Assert.NotEqual(builtIn.Templates[0].TemplateId, copied.TemplateId);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Command(f.Request(copied.Revision, new CopyTemplateCommandDto("builtin", new('0',64), builtIn.Templates[0].TemplateId, "错源")))).Error?.Code);
        File.WriteAllText(f.Index, "private-machine-path corrupt");
        Assert.Equal(2, Accepted(await f.Registry.ReadTemplatesAsync("builtin", builtIn.Revision)).TotalCount);
        Assert.Equal(2, f.Registry.ReadTemplateLibraries().Libraries.Length);
        var bad = await f.Registry.ReadTemplatesAsync("personal", null);
        Assert.Equal(BackendErrorCode.CommandRejected, bad.Error?.Code); Assert.Equal("LibraryInvalid", bad.Error?.FailureCode);
        Assert.DoesNotContain(f.Directory.Path, bad.Error?.Message ?? ""); Assert.Null(bad.Response);
        Assert.Equal(BackendErrorCode.ReadOnlySession, (await f.Registry.ExecuteTemplateLibraryCommandAsync("client", "builtin", f.Request(builtIn.Revision, new DeleteTemplateCommandDto(builtIn.Templates[0].TemplateId)))).Error?.Code);
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    public async Task typed非有限值在指纹前拒绝不占回执(float bad)
    {
        await using Fixture f = new(); var definition = Definition() with { Style = Style with { Opacity = bad } };
        var request = f.Request("missing", new CreateTemplateCommandDto(definition));
        var result = await f.Command(request); Assert.Equal(BackendErrorCode.InvalidRequest, result.Error?.Code);
        Assert.False(f.Registry.Receipts.HasUnexpiredReceipt("client", request.Metadata.RequestId)); Assert.False(File.Exists(f.Index));
    }

    [Fact]
    public async Task fullstyle范围拒绝和超意图预算均零写入()
    {
        await using Fixture f = new(); var invalid = Definition() with { Style = Style with { Opacity = 2 } };
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Command(f.Request("missing", new CreateTemplateCommandDto(invalid)))).Error?.Code);
        string[] huge = Enumerable.Repeat(new string('中',256),32).ToArray();
        var large = Definition() with { Style = Style with { Typography = Style.Typography with { FallbackFamilyNames = huge } } };
        // Three gradients at the maximum input stop count may still fit 256KiB; exceeding primitive input bounds is independently rejected.
        var request = f.Request("missing", new CreateTemplateCommandDto(large with { Name = new string('x',257) }));
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command(request)).Error?.Code);
        Assert.False(f.Registry.Receipts.HasUnexpiredReceipt("client", request.Metadata.RequestId)); Assert.False(File.Exists(f.Index));
    }

    [Fact]
    public async Task accepted锁等待固定数组且取消与关闭排水后才释放profile()
    {
        await using Fixture f = new(); using var lease = LibraryCommitLease.TryEnter(f.Index);
        string[] fallback = ["原字体"]; TemplateProperty[] properties = [TemplateProperty.FontSizePx];
        var definition = Definition() with { Style = Style with { Typography = Style.Typography with { FallbackFamilyNames = fallback } }, Properties = properties };
        var request = f.Request("missing", new CreateTemplateCommandDto(definition));
        var replayRequest = TemplateLibraryCommandCodec.Capture(request);
        using var cancellation = new CancellationTokenSource();
        var command = f.Registry.ExecuteTemplateLibraryCommandAsync("client", "personal", request, cancellation.Token);
        Assert.True(f.Registry.Receipts.HasUnexpiredReceipt("client", request.Metadata.RequestId));
        fallback[0] = "后来修改"; properties[0] = TemplateProperty.Scale; cancellation.Cancel();
        // Builtin reads remain available while the personal owner waits on its external commit lease.
        Assert.Equal(2, Accepted(await f.Registry.ReadTemplatesAsync("builtin", null).WaitAsync(TimeSpan.FromSeconds(2))).TotalCount);
        var stop = f.Registry.ShutdownAsync().AsTask(); Assert.False(stop.IsCompleted);
        var profile = (IDisposable)typeof(ProjectSessionRegistry).GetField("_profileLease", BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(f.Registry)!;
        Assert.NotNull(profile);
        Assert.Equal(BackendErrorCode.SessionClosing, (await f.Registry.ReadTemplatesAsync("builtin", null)).Error?.Code);
        lease.Dispose();
        BackendOperationResult<TemplateLibraryCommandResponse>? completed = null;
        try { completed = await command; }
        catch (OperationCanceledException) { /* cancellation may win before the accepted owner publishes */ }
        Assert.True(completed is null || completed.Accepted || completed.Error is not null);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        var template = Assert.Single(StyleTemplateLibrary.Load(f.Index).Templates);
        Assert.Equal("原字体", Assert.Single(template.Style.Typography.FallbackFamilyNames));
        Assert.Equal(LyricStyleProperty.FontSizePx, Assert.Single(template.Properties));
        Assert.Null(typeof(ProjectSessionRegistry).GetField("_profileLease", BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(f.Registry));
        Assert.Equal(template.Id, Accepted(await f.Command(replayRequest)).TemplateId); // Existing completed receipts remain replayable after shutdown.
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal TemporaryDirectory Directory { get; } = new();
        internal string Index => StyleTemplateLibrary.ResolvePath(Path.Combine(Directory.Path,"state","libraries"));
        internal ProjectSessionRegistry Registry { get; }
        internal Fixture()
        {
            ResourceStore resources = new(new() {ResourceRoot=Path.Combine(Directory.Path,"resources")});
            Registry = new("host", resources, new((_,_)=>Task.FromResult<LoadedWaveform?>(null)));
            Registry.ConfigurePersistence(new(){StateRoot=Path.Combine(Directory.Path,"state")}); Registry.ConfigureTemplateLibrary();
        }
        internal TemplateLibraryCommandRequest Request(string revision, TemplateLibraryCommandDto command)
        {
            _ = Registry;
            return new(revision,
            new(Guid.NewGuid().ToString("N"),"host",DateTimeOffset.UtcNow.AddMinutes(1)), command);
        }
        internal Task<BackendOperationResult<TemplateLibraryCommandResponse>> Command(TemplateLibraryCommandRequest request) => Registry.ExecuteTemplateLibraryCommandAsync("client","personal",request);
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); Directory.Dispose(); }
    }
}
