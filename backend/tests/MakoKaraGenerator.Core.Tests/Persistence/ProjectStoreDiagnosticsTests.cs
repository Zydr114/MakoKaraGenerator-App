using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>
/// 读取非法或不完整项目文件时的诊断测试。
/// </summary>
/// <remarks>
/// 阶段 1 的目标不是"遇到坏数据就崩"，而是"把坏数据读进来并说清楚哪里坏了"，
/// 因此这里的断言重点在诊断的标识与严重程度，而不是异常类型。
/// </remarks>
public sealed class ProjectStoreDiagnosticsTests : IDisposable
{
    private const string LegacyProjectJson = """
        {
          "schemaVersion": 1,
          "name": "测试项目",
          "composition": {
            "canvasWidth": 1920,
            "canvasHeight": 1080,
            "frameRate": { "numerator": 30, "denominator": 1 },
            "backgroundColor": "#000000FF",
            "outputDurationUs": 5000000
          }
        }
        """;

    private static string ValidProjectJson => LegacyProjectJson
        .Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {Project.CurrentSchemaVersion}", StringComparison.Ordinal)
        .Replace("\"name\": \"测试项目\"", $"\"applicationVersion\": \"{ProductVersion.Current}\",\n          \"name\": \"测试项目\"", StringComparison.Ordinal);

    private static string ValidLyricsJson => """
        {
          "schemaVersion": 13,
          "primaryBranchId": "primary",
          "branches": [
            {
              "id": "primary",
              "name": "主歌词",
              "role": "Primary",
              "language": "zh-CN",
              "timingMode": "Independent",
              "lines": [
                {
                  "id": "line-001",
                  "text": "你好",
                  "startUs": 1000000,
                  "endUs": 2000000,
                  "timingState": "Confirmed",
                  "tokens": [
                    { "id": "token-001", "text": "你", "startUs": 1000000, "endUs": 1500000, "timingState": "Confirmed" },
                    { "id": "token-002", "text": "好", "startUs": 1500000, "endUs": 2000000, "timingState": "Confirmed" }
                  ]
                }
              ]
            }
          ]
        }
        """.Replace("\"schemaVersion\": 13", $"\"schemaVersion\": {LyricsDocument.CurrentSchemaVersion}", StringComparison.Ordinal);

    private readonly TemporaryDirectory _directory = new();
    private string _lyricsFile = "data/lyrics/" + new string('0', 64) + ".json";

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 缺少项目文件时无法得到项目()
    {
        WriteLyrics(ValidLyricsJson);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.False(result.Loaded);
        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(DiagnosticCode.MissingProjectFile));
        Assert.True(result.Report.HasErrors);
    }

    [Fact]
    public void 缺少歌词文件时拒绝加载而不生成空歌词()
    {
        WriteProject(ValidProjectJson);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.False(result.Loaded);
        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(DiagnosticCode.MissingLyricsFile));
    }

    [Fact]
    public void JSON_语法错误被报告而不是抛异常()
    {
        WriteProject("{ \"schemaVersion\": 1, ");
        WriteLyrics(ValidLyricsJson);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.False(result.Loaded);
        Assert.True(result.Report.Contains(DiagnosticCode.MalformedJson));
    }

    [Fact]
    public void 缺少_schemaVersion_被报告()
    {
        WriteProject("""{ "name": "无版本" }""");
        WriteLyrics(ValidLyricsJson);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.False(result.Loaded);
        Assert.True(result.Report.Contains(DiagnosticCode.UnsupportedSchemaVersion));
    }

    [Fact]
    public void 来自更新版本的项目被拒绝且不尝试解析()
    {
        WriteProject(
            ValidProjectJson.Replace($"\"schemaVersion\": {Project.CurrentSchemaVersion}", "\"schemaVersion\": 99", StringComparison.Ordinal));
        WriteLyrics(ValidLyricsJson);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.False(result.Loaded);
        Assert.True(result.Report.Contains(DiagnosticCode.UnsupportedSchemaVersion));
    }

    [Fact]
    public void 读取不会修改磁盘上的原文件()
    {
        // docs/development.md §6.4：迁移失败时保留原文件。
        WriteProject(
            ValidProjectJson.Replace($"\"schemaVersion\": {Project.CurrentSchemaVersion}", "\"schemaVersion\": 42", StringComparison.Ordinal));
        WriteLyrics(ValidLyricsJson);

        string beforeProject = _directory.ReadFile(ProjectJson.ProjectFileName);
        string beforeLyrics = ProjectFileFixture.ReadLyrics(_directory.Path);

        ProjectStore.Load(_directory.Path);

        Assert.Equal(beforeProject, _directory.ReadFile(ProjectJson.ProjectFileName));
        Assert.Equal(beforeLyrics, ProjectFileFixture.ReadLyrics(_directory.Path));
    }

    [Fact]
    public void 未知的_timingState_被报告并保守回退()
    {
        WriteProject(ValidProjectJson);
        WriteLyrics(
            ValidLyricsJson.Replace("\"timingState\": \"Confirmed\"", "\"timingState\": \"随便写的\"", StringComparison.Ordinal));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Loaded);
        Assert.True(result.Report.Contains(DiagnosticCode.UnknownTimingState), result.Report.ToSummary());
        Assert.All(
            result.Project!.Lyrics.Lines[0].Tokens,
            static token => Assert.Equal(TimingStatus.Imported, token.TimingStatus));
    }

    [Fact]
    public void 数字形式的_timingState_不被接受()
    {
        // Enum.TryParse 会把 "3" 解析成 Edited，这里必须挡住，否则非法文件会被静默接受。
        WriteProject(ValidProjectJson);
        WriteLyrics(
            ValidLyricsJson.Replace("\"timingState\": \"Confirmed\"", "\"timingState\": \"3\"", StringComparison.Ordinal));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Report.Contains(DiagnosticCode.UnknownTimingState), result.Report.ToSummary());
    }

    [Fact]
    public void 当前工程拒绝旧平面样式键()
    {
        WriteProject(ValidProjectJson.Replace(
            "\"name\": \"测试项目\"",
            "\"lyricStyle\": { \"fontSizePx\": 48 },\n          \"name\": \"测试项目\"",
            StringComparison.Ordinal));
        WriteLyrics(ValidLyricsJson);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Null(result.Project);
        Assert.True(result.Report.HasErrors);
        Assert.Contains("fontSizePx", result.Report.ToSummary(), StringComparison.Ordinal);
    }

    [Fact]
    public void 有时间但缺少_timingState_时按导入处理并给出警告()
    {
        // 刻意使用独立字面量而不是修改 ValidLyricsJson：
        // 原始字符串字面量的缩进在 Replace 目标里很容易写错，独立字面量更清楚。
        WriteProject(ValidProjectJson);
        WriteLyrics(
            """
            {
              "schemaVersion": 1,
              "lines": [
                {
                  "id": "line-001",
                  "text": "你好",
                  "startUs": 1000000,
                  "endUs": 2000000
                }
              ]
            }
            """);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Loaded);
        Assert.True(result.Report.Contains(DiagnosticCode.MissingTimingState));
        Assert.Equal(TimingStatus.Imported, result.Project!.Lyrics.Lines[0].TimingStatus);
        Assert.False(result.Report.HasErrors, result.Report.ToSummary());
    }

    [Fact]
    public void 只有_startUs_的时间区间被报告为不完整()
    {
        WriteProject(ValidProjectJson);
        WriteLyrics(
            """{ "schemaVersion": 1, "lines": [ { "id": "line-001", "text": "你好", "startUs": 1000000 } ] }""");

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Report.Contains(DiagnosticCode.IncompleteTimeRange));
        Assert.Null(result.Project!.Lyrics.Lines[0].Time);
    }

    [Fact]
    public void 缺少行标识时会生成一个临时标识并报告()
    {
        WriteProject(ValidProjectJson);
        WriteLyrics(
            """{ "schemaVersion": 1, "lines": [ { "text": "你好" } ] }""");

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Report.Contains(DiagnosticCode.MissingIdentifier));
        Assert.Equal("line-001", result.Project!.Lyrics.Lines[0].Id);
    }

    [Fact]
    public void 缺少文本时被报告()
    {
        WriteProject(ValidProjectJson);
        WriteLyrics(
            """{ "schemaVersion": 1, "lines": [ { "id": "line-001" } ] }""");

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Report.Contains(DiagnosticCode.MissingText));
        Assert.Equal(string.Empty, result.Project!.Lyrics.Lines[0].Text);
    }

    [Fact]
    public void 非法背景色被报告并使用默认值()
    {
        WriteProject(
            ValidProjectJson.Replace("\"#000000FF\"", "\"不是颜色\"", StringComparison.Ordinal));
        WriteLyrics(ValidLyricsJson);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Report.Contains(DiagnosticCode.InvalidBackgroundColor));
        Assert.True(result.Loaded);
    }

    [Theory]
    [InlineData("""{ "numerator": 0, "denominator": 1 }""")]
    [InlineData("""{ "numerator": 30, "denominator": -1 }""")]
    [InlineData("""{ "numerator": 30 }""")]
    [InlineData("null")]
    public void 非法帧率被报告并使用默认值(string frameRateJson)
    {
        WriteProject(
            ValidProjectJson.Replace(
                """{ "numerator": 30, "denominator": 1 }""",
                frameRateJson,
                StringComparison.Ordinal));
        WriteLyrics(ValidLyricsJson);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Report.Contains(DiagnosticCode.InvalidFrameRateDefinition));
        Assert.True(result.Loaded);
    }

    [Fact]
    public void 缺少_composition_对象被报告并使用默认画布()
    {
        WriteProject(
            """{ "schemaVersion": 1, "name": "没有画布" }""");
        WriteLyrics(ValidLyricsJson);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Report.Contains(DiagnosticCode.MissingComposition));
        Assert.Equal(1920, result.Project!.Composition.CanvasWidth);
    }

    [Fact]
    public void 读取后立即给出打开上下文的语义校验()
    {
        // 声明为已确认却没有任何时间，属于语义问题，应由校验阶段而不是结构映射阶段报告。
        WriteProject(ValidProjectJson);
        WriteLyrics(
            """{ "schemaVersion": 1, "lines": [ { "id": "line-001", "text": "你好", "timingState": "Confirmed" } ] }""");

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Report.Contains(DiagnosticCode.TimeStatusMismatch));
        Assert.True(result.Report.HasErrors);
        Assert.False(result.CanExport);
    }

    [Fact]
    public void 诊断带有可定位的路径()
    {
        WriteProject(ValidProjectJson);
        WriteLyrics(
            """{ "schemaVersion": 1, "lines": [ { "id": "line-001", "text": "你好" } ] }""");

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        ProjectIssue missingTime = result.Report.Issues.Single(
            static issue => issue.Code == DiagnosticCode.MissingTime);

        Assert.Equal("lyrics.branches[0].lines[0]", missingTime.Location);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 空或缺失注音Text保留独立片段并报告不一致而不终止载入(bool missingText)
    {
        JsonNode lyrics = JsonNode.Parse(ValidLyricsJson)!;
        JsonObject ruby = new()
        {
            ["id"] = "ruby", ["baseTokenId"] = "token-001", ["text"] = "",
            ["segments"] = new JsonArray(new JsonObject
            {
                ["id"] = "ruby-segment", ["text"] = "に", ["startUs"] = 1_100_000,
                ["endUs"] = 1_300_000, ["timingState"] = "Imported",
            }),
        };
        if (missingText) ruby.Remove("text");
        lyrics["branches"]![0]!["lines"]![0]!["rubyAnnotations"] = new JsonArray(ruby);
        WriteProject(ValidProjectJson);
        WriteLyrics(lyrics.ToJsonString());
        string before = ProjectFileFixture.ReadLyrics(_directory.Path);
        ProjectLoadResult result = ProjectStore.Load(_directory.Path);
        Assert.True(result.Loaded, result.Report.ToSummary());
        RubyAnnotation restored = Assert.Single(result.Project!.Lyrics.Lines[0].RubyAnnotations);
        Assert.Equal("", restored.Text);
        Assert.Equal("に", Assert.Single(restored.Segments).Text);
        Assert.Equal(1_100_000, restored.Segments[0].Time!.Value.StartUs);
        Assert.Contains(result.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidRubyAnnotation
            && issue.Severity == IssueSeverity.Error && issue.Location?.EndsWith(".text", StringComparison.Ordinal) == true);
        if (missingText) Assert.True(result.Report.Contains(DiagnosticCode.MissingText));
        Assert.Equal(before, ProjectFileFixture.ReadLyrics(_directory.Path));
    }

    [Fact]
    public void v17缺少segments时长注音标识生成有界兼容片段标识()
    {
        string rubyId = new('字', 256);
        JsonNode lyrics = JsonNode.Parse(ValidLyricsJson)!;
        lyrics["branches"]![0]!["lines"]![0]!["rubyAnnotations"] = new JsonArray(new JsonObject
        {
            ["id"] = rubyId,
            ["baseTokenId"] = "token-001",
            ["text"] = "に",
            ["alignment"] = "Center",
        });
        WriteProject(ValidProjectJson);
        WriteLyrics(lyrics.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Loaded, result.Report.ToSummary());
        RubyTextSegment segment = Assert.Single(result.Project!.Lyrics.Lines[0].RubyAnnotations[0].Segments);
        Assert.Equal(RubyTextSegment.CompatibilityId(rubyId), segment.Id);
        Assert.InRange(segment.Id.Length, 1, 118);
        Assert.Equal(TimingStatus.Missing, segment.TimingStatus);
        Assert.False(result.Report.HasErrors, result.Report.ToSummary());
    }

    private void WriteProject(string json)
    {
        if (!json.Contains($"\"schemaVersion\": {Project.CurrentSchemaVersion}", StringComparison.Ordinal))
        {
            json = json.Replace("\"schemaVersion\": 1,",
                $"\"schemaVersion\": {Project.CurrentSchemaVersion},", StringComparison.Ordinal);
        }
        if (!json.Contains("\"applicationVersion\"", StringComparison.Ordinal))
        {
            int opening = json.IndexOf('{');
            if (opening >= 0)
            {
                json = json.Insert(opening + 1,
                    $" \"applicationVersion\": \"{ProductVersion.Current}\", ");
            }
        }

        try
        {
            var data = JsonNode.Parse(json)!.AsObject();
            data["lyricsFile"] = _lyricsFile;
            data["media"] = JsonNode.Parse("{\"audio\":null,\"background\":null}");
            data["audioAnalysis"] = JsonNode.Parse("{\"state\":\"noAudio\"}");
            data["effects"] = JsonNode.Parse("""
                { "contractVersion":1,"simulation":{"version":1,"stepHz":120,"random":"pcg32-v1"},
                  "budgets":{"surfaceMemoryBytes":536870912,"liveParticles":20000,"trailSamples":200000,"emissionRecords":100000,"drawCommands":100000},
                  "packages":[],"instances":[] }
                """);
            data["presetApplications"] = JsonNode.Parse("{\"contractVersion\":1,\"templateSchemaVersion\":6,\"items\":[]}");
            data["lyricPresentation"] ??= new JsonObject { ["nodes"] = new JsonArray(), ["programs"] = new JsonArray(), ["lineOverrides"] = new JsonArray() };
            json = data.ToJsonString(ProjectJson.Options);
        }
        catch (System.Text.Json.JsonException) { }
        _directory.WriteFile(ProjectJson.ProjectFileName, json);
    }

    private void WriteLyrics(string json)
    {
        json = json.Replace("\"schemaVersion\": 1,",
            $"\"schemaVersion\": {LyricsDocument.CurrentSchemaVersion},", StringComparison.Ordinal);
        // 诊断用片段放入现行主分支容器；损坏 JSON 仍原样交给读取器。
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            if (root.ContainsKey("lines"))
            {
                var lines = root["lines"]?.DeepClone();
                root.Remove("lines");
                root["primaryBranchId"] = "primary";
                root["branches"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = "primary", ["name"] = "主歌词", ["role"] = "Primary",
                    ["timingMode"] = "Independent", ["lines"] = lines,
                });
            }
            json = root.ToJsonString(ProjectJson.Options);
        }
        catch (System.Text.Json.JsonException) { }
        _lyricsFile = ProjectFileFixture.PublishLyrics(_directory.Path, json);
    }

}
