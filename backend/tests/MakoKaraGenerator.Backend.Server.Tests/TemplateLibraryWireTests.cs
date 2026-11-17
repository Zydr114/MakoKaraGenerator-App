using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed class TemplateLibraryWireTests
{
    internal const string Style = """{"typography":{"fontFamilyName":null,"fallbackFamilyNames":[],"fontSizePx":48,"fontWeight":400,"isBold":false,"isItalic":false,"letterSpacingPx":0},"unsung":{"fill":{"kind":"solid","color":{"red":1,"green":2,"blue":3,"alpha":255}},"strokes":[],"shadow":null,"glow":null},"sung":{"fill":{"kind":"solid","color":{"red":4,"green":5,"blue":6,"alpha":255}},"strokes":{"kind":"follow"},"shadow":{"kind":"follow"},"glow":{"kind":"follow"}},"wipe":{"featherRatio":0,"highlight":null},"verticalCenterRatio":0.5,"horizontalAlignment":"center","opacity":1,"scale":1,"ruby":{"relativeScale":0.6,"gapPx":null}}""";
    internal static string Definition(string name = "模板") => "{\"name\":" + JsonSerializer.Serialize(name) + ",\"kind\":\"line\",\"style\":" + Style + ",\"properties\":[\"typography.fontSizePx\"]}";
    internal static string Request(string command, string deadline = "2030-01-01T00:00:00Z") => "{\"expectedRevision\":\"missing\",\"metadata\":{\"requestId\":\"request\",\"hostInstanceId\":\"host\",\"validUntilUtc\":" + JsonSerializer.Serialize(deadline) + "},\"command\":" + command + "}";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions IgnoreNullJson = new(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    [Theory]
    [InlineData("2030-01-01T00:00:00Z")]
    [InlineData("2030-01-01T08:00:00+08:00")]
    [InlineData("2029-12-31T19:00:00-05:00")]
    public void 新Metadata要求真实显式时区且不同offset同一时刻(string deadline)
    {
        var value = JsonSerializer.Deserialize<TemplateLibraryCommandRequest>(Request("{\"type\":\"delete\",\"templateId\":\"x\"}", deadline), Json)!;
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), value.Metadata.ValidUntilUtc);
    }
    [Theory]
    [InlineData("2030-01-01T00:00:00")]
    [InlineData("2030-01-01")]
    [InlineData("2030-01-01T00:00:00+0800")]
    [InlineData("2030-01-01T00:00:00+99:99")]
    public void 新Metadata拒绝依赖本地时区或非法时区(string deadline) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TemplateLibraryCommandRequest>(Request("{\"type\":\"delete\",\"templateId\":\"x\"}", deadline), Json));

    public static IEnumerable<object[]> Commands()
    {
        yield return ["{\"type\":\"create\",\"definition\":" + Definition() + "}", typeof(CreateTemplateCommandDto)];
        yield return ["{\"type\":\"replace\",\"templateId\":\" 旧/身份 断\",\"definition\":" + Definition() + "}", typeof(ReplaceTemplateCommandDto)];
        yield return ["{\"type\":\"copy\",\"sourceLibraryId\":\"builtin\",\"sourceRevision\":\"missing\",\"templateId\":\"builtin:line-classic\",\"name\":\"副本\"}", typeof(CopyTemplateCommandDto)];
        yield return ["{\"type\":\"rename\",\"templateId\":\"x\",\"name\":\"  保留完整定义  \"}", typeof(RenameTemplateCommandDto)];
        yield return ["{\"type\":\"delete\",\"templateId\":\"x\"}", typeof(DeleteTemplateCommandDto)];
    }
    [Theory]
    [MemberData(nameof(Commands))]
    public void 五型真实序列化完整往返且显式空值保持(string command, Type type)
    {
        var first = JsonSerializer.Deserialize<TemplateLibraryCommandRequest>(Request(command), Json)!;
        Assert.Equal(type, first.Command.GetType());
        string serialized = JsonSerializer.Serialize(first, Json);
        Assert.Equal(serialized, JsonSerializer.Serialize(TemplateLibraryCommandCodec.Capture(first), Json));
        if (first.Command is CreateTemplateCommandDto or ReplaceTemplateCommandDto)
        { Assert.Contains("\"fontFamilyName\":null", serialized); Assert.Contains("\"strokes\":[]", serialized); Assert.Contains("\"highlight\":null", serialized); Assert.Contains("\"glow\":{\"kind\":\"follow\"}", serialized); Assert.Contains("\"shadow\":null", serialized); Assert.Contains("\"glow\":null", serialized); Assert.Contains("\"ruby\":{\"relativeScale\":0.6,\"gapPx\":null}", serialized); }
    }
    public static IEnumerable<object[]> InvalidShapes()
    {
        string request = Request("{\"type\":\"create\",\"definition\":" + Definition() + "}");
        yield return [Request("{\"type\":\"rename\",\"templateId\":\"x\",\"name\":\"\\uD800\"}")];
        foreach (var pair in new (string From, string To)[] {
            ("\"expectedRevision\":\"missing\",", ""), ("\"expectedRevision\":\"missing\"", "\"expectedRevision\":null"),
            ("\"requestId\":\"request\",", ""), ("\"hostInstanceId\":\"host\",", ""),
            ("\"validUntilUtc\":\"2030-01-01T00:00:00Z\"", "\"validUntilUtc\":null"),
            ("\"requestId\":\"request\"", "\"requestId\":\"r\",\"requestId\":\"request\""),
            ("\"metadata\":{", "\"metadata\":{\"unexpected\":null,"), ("\"type\":\"create\"", "\"type\":\"Create\""),
            ("\"type\":\"create\"", "\"type\":\"create\",\"templateId\":null"), ("\"type\":\"create\"", "\"type\":\"create\",\"type\":\"create\""),
            ("\"kind\":\"line\"", "\"kind\":0"), ("\"kind\":\"line\"", "\"kind\":\"Line\""),
            ("\"properties\":[\"typography.fontSizePx\"]", "\"properties\":[]"), ("\"properties\":[\"typography.fontSizePx\"]", "\"properties\":[\"typography.fontSizePx\",\"typography.fontSizePx\"]"),
            ("\"properties\":[\"typography.fontSizePx\"]", "\"properties\":[\"fontSizePx\"]"), ("\"properties\":[\"typography.fontSizePx\"]", "\"properties\":[\"stroke\"]"),
            ("\"fontFamilyName\":null,", ""), ("\"fontWeight\":400", "\"fontWeight\":400.5"),
            ("\"isBold\":false", "\"isBold\":null"), ("\"scale\":1", "\"scale\":\"1\""),
            ("\"horizontalAlignment\":\"center\"", "\"horizontalAlignment\":\"Center\""),
            ("\"alpha\":255", "\"alpha\":256"), ("\"red\":1", "\"red\":1,\"red\":2"),
            ("\"strokes\":[],", ""), ("\"strokes\":[]", "\"strokes\":null"), ("\"shadow\":null", "\"shadow\":{}"), ("\"glow\":null}", "}"),
            ("\"strokes\":{\"kind\":\"follow\"}", "\"strokes\":{\"kind\":\"follow\",\"value\":[]}"), ("\"shadow\":{\"kind\":\"follow\"}", "\"shadow\":null"),
            ("\"glow\":{\"kind\":\"follow\"}", "\"glow\":{\"kind\":\"set\"}"), ("\"featherRatio\":0", "\"featherRatio\":null"),
            ("\"wipe\":{\"featherRatio\":0,\"highlight\":null},", ""), ("\"highlight\":null", "\"highlight\":{}"),
            ("\"fallbackFamilyNames\":[]", "\"fallbackFamilyNames\":[null]"),
            ("\"opacity\":1", "\"opacity\":1e309"),
            ("\"ruby\":{\"relativeScale\":0.6,\"gapPx\":null}", "\"ruby\":{\"relativeScale\":0.6}"),
        }) yield return [request.Replace(pair.From, pair.To, StringComparison.Ordinal)];
    }
    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public void 所有嵌套字段strict缺失重复未知null数值枚举均拒绝(string raw) => Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<TemplateLibraryCommandRequest>(raw, Json));

    [Fact]
    public void Definition读不套新input限额但Command新写入必须限额()
    {
        string definition = Definition(new string('旧', 257));
        Assert.NotNull(JsonSerializer.Deserialize<StaticTemplateDefinitionDto>(definition, Json));
        Assert.Throws<TemplateLibraryBudgetExceededException>(() => JsonSerializer.Deserialize<TemplateLibraryCommandRequest>(Request("{\"type\":\"create\",\"definition\":" + definition + "}"), Json));
        string request = Request("{\"type\":\"delete\",\"templateId\":\"x\"}");
        Assert.NotNull(JsonSerializer.Deserialize<TemplateLibraryCommandRequest>(new string(' ', 300_000) + request, Json));
        Assert.Throws<TemplateLibraryBudgetExceededException>(() => JsonSerializer.Deserialize<TemplateLibraryCommandRequest>(Request("{\"type\":\"delete\",\"templateId\":\"" + new string('a', 262144) + "\"}"), Json));
    }
    [Fact]
    public void TypedCapture深固定数组并拒绝篡改type与非法浮点()
    {
        var request = JsonSerializer.Deserialize<TemplateLibraryCommandRequest>(Request("{\"type\":\"create\",\"definition\":" + Definition() + "}"), Json)!;
        var captured = TemplateLibraryCommandCodec.Capture(request);
        ((CreateTemplateCommandDto)request.Command).Definition.Properties[0] = "scale";
        Assert.Equal("typography.fontSizePx", ((CreateTemplateCommandDto)captured.Command).Definition.Properties[0]);
        Assert.Throws<JsonException>(() => TemplateLibraryCommandCodec.Capture(request with { Command = new DeleteTemplateCommandDto("x") with { Type = "rename" } }));
        var create = (CreateTemplateCommandDto)captured.Command;
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<ArgumentException>(() => TemplateLibraryCommandCodec.Capture(captured with { Command = create with { Definition = create.Definition with { Style = create.Definition.Style with { Scale = value } } } }));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateTemplateCommandDto>("{\"type\":\"delete\",\"templateId\":\"x\"}", Json));
    }
    [Fact]
    public void 完整三渐变大于旧64KiB仍合法且保持所有几何与属性选择()
    {
        string stop = "{\"offset\":0.12345678901234567,\"color\":{\"red\":255,\"green\":254,\"blue\":253,\"alpha\":252}}";
        string material = "{\"kind\":\"gradient\",\"gradient\":{\"kind\":\"linear\",\"angleDegrees\":123.45678901234567,\"centerX\":-3,\"centerY\":4,\"radiusX\":-5,\"radiusY\":-6,\"coordinateSpace\":\"glyph\",\"colorSpace\":\"oklch\",\"huePath\":\"decreasing\",\"stops\":[" + string.Join(',', Enumerable.Repeat(stop,256)) + "]}}";
        string style = Style.Replace("{\"kind\":\"solid\",\"color\":{\"red\":1,\"green\":2,\"blue\":3,\"alpha\":255}}", material, StringComparison.Ordinal)
            .Replace("{\"kind\":\"solid\",\"color\":{\"red\":4,\"green\":5,\"blue\":6,\"alpha\":255}}", material, StringComparison.Ordinal)
            .Replace("\"strokes\":[]", "\"strokes\":[{\"material\":" + material + ",\"widthPx\":3,\"join\":\"bevel\"}]", StringComparison.Ordinal);
        string definition = Definition().Replace(Style, style, StringComparison.Ordinal);
        var command = JsonSerializer.Deserialize<TemplateLibraryCommandDto>("{\"type\":\"create\",\"definition\":" + definition + "}", Json)!;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(command, Json); Assert.True(bytes.Length > 64 * 1024); Assert.True(bytes.Length <= TemplateLibraryLimits.MaxIntentBytes);
        var gradient = Assert.IsType<GradientStyleMaterialDto>(Assert.IsType<CreateTemplateCommandDto>(command).Definition.Style.Unsung.Strokes[0].Material).Gradient;
        Assert.Equal(256, gradient.Stops.Length); Assert.Equal(-5, gradient.RadiusX); Assert.Equal(StyleGradientColorSpace.Oklch, gradient.ColorSpace);
        string properties = JsonSerializer.Serialize(MakoKaraGenerator.Core.Projects.LyricStylePropertyExtensions.All.Select(MakoKaraGenerator.Core.Projects.LyricStylePropertyExtensions.ToJsonName), Json);
        Assert.Equal(45, JsonSerializer.Deserialize<StaticTemplateDefinitionDto>(Definition().Replace("[\"typography.fontSizePx\"]",properties,StringComparison.Ordinal),Json)!.Properties.Length);
        Assert.Throws<TemplateLibraryBudgetExceededException>(() => JsonSerializer.Deserialize<TemplateLibraryCommandDto>("{\"type\":\"create\",\"definition\":" + definition.Replace(string.Join(',',Enumerable.Repeat(stop,256)),string.Join(',',Enumerable.Repeat(stop,257)),StringComparison.Ordinal) + "}",Json));
    }
    [Fact]
    public void 未选包材质同样不能借完整静态定义写入()
    {
        string definition = Definition().Replace("{\"kind\":\"solid\",\"color\":{\"red\":1,\"green\":2,\"blue\":3,\"alpha\":255}}", "{\"kind\":\"package\",\"packageId\":\"p\",\"revision\":\"r\",\"exportId\":\"e\"}",StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StaticTemplateDefinitionDto>(definition,Json));
    }
    [Fact]
    public void 高级Detail必需null不被全局忽略规则吞掉()
    {
        var summary = new TemplateSummaryDto("x", "高级", TemplateKind.Line, new string('a',64), ["scale"], true, false, true);
        string raw = JsonSerializer.Serialize(new TemplateDetailResponse("personal", "missing", summary, "advancedUnsupported", null), IgnoreNullJson);
        using var doc = JsonDocument.Parse(raw); Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("definition").ValueKind);
    }
}
