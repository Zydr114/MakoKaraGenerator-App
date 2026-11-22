using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>歌词 schema 版本门禁回归测试。</summary>
public sealed class LyricsSchemaMigrationTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void 非当前歌词schemaVersion被拒绝并报告UnsupportedSchemaVersion(int delta)
    {
        WriteProject();

        JsonObject lyrics = new()
        {
            ["schemaVersion"] = LyricsDocument.CurrentSchemaVersion + delta,
            ["primaryBranchId"] = "primary",
            ["branches"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "primary",
                    ["name"] = "主歌词",
                    ["role"] = "Primary",
                    ["timingMode"] = "Independent",
                    ["lines"] = new JsonArray(),
                },
            },
        };
        string reference = ProjectFileFixture.PublishLyrics(_directory.Path, lyrics.ToJsonString(ProjectJson.Options));
        JsonObject project = JsonNode.Parse(_directory.ReadFile(ProjectJson.ProjectFileName))!.AsObject();
        project["lyricsFile"] = reference;
        _directory.WriteFile(ProjectJson.ProjectFileName, project.ToJsonString(ProjectJson.Options));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Null(result.Project);
        Assert.True(result.Report.HasErrors, result.Report.ToSummary());
        Assert.Contains(result.Report.Issues, static issue => issue.Code == DiagnosticCode.UnsupportedSchemaVersion);
    }

    [Fact]
    public void v8内容不会被仅改版本号解释而是拒绝()
    {
        WriteProject();

        JsonObject lyrics = new()
        {
            ["schemaVersion"] = 8,
            ["primaryBranchId"] = "missing",
            ["branches"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "",
                    ["name"] = "",
                    ["role"] = "Primary",
                    ["timingMode"] = "Independent",
                    ["lines"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "line-1", ["text"] = "坏数据" },
                    },
                },
            },
        };
        string reference = ProjectFileFixture.PublishLyrics(_directory.Path, lyrics.ToJsonString(ProjectJson.Options));
        JsonObject project = JsonNode.Parse(_directory.ReadFile(ProjectJson.ProjectFileName))!.AsObject();
        project["lyricsFile"] = reference;
        _directory.WriteFile(ProjectJson.ProjectFileName, project.ToJsonString(ProjectJson.Options));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Null(result.Project);
        Assert.True(result.Report.HasErrors, result.Report.ToSummary());
        Assert.Contains(result.Report.Issues, static issue => issue.Code == DiagnosticCode.UnsupportedSchemaVersion);
    }

    private void WriteProject()
    {
        JsonObject project = new()
        {
            ["schemaVersion"] = Project.CurrentSchemaVersion,
            ["applicationVersion"] = ProductVersion.Current,
            ["name"] = "歌词迁移测试",
            ["composition"] = new JsonObject
            {
                ["canvasWidth"] = 1920,
                ["canvasHeight"] = 1080,
                ["frameRate"] = new JsonObject { ["numerator"] = 30, ["denominator"] = 1 },
                ["backgroundColor"] = "#000000FF",
                ["outputDurationUs"] = 5_000_000,
            },
            ["lyricsFile"] = "data/lyrics/" + new string('0', 64) + ".json",
            ["media"] = JsonNode.Parse("{\"audio\":null,\"background\":null}"),
            ["audioAnalysis"] = JsonNode.Parse("{\"state\":\"noAudio\"}"),
            ["effects"] = JsonNode.Parse(ProjectEffectsJson.Encode(ProjectEffects.Empty)),
            ["presetApplications"] = JsonNode.Parse($"{{\"contractVersion\":1,\"templateSchemaVersion\":{StyleTemplateLibrary.CurrentSchemaVersion},\"items\":[]}}"),
            ["lyricPresentation"] = new JsonObject { ["nodes"] = new JsonArray(), ["programs"] = new JsonArray(), ["lineOverrides"] = new JsonArray() },
        };

        _directory.WriteFile(ProjectJson.ProjectFileName, project.ToJsonString(ProjectJson.Options));
    }
}
