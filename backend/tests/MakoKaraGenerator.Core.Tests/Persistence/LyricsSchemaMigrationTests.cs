using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>歌词旧版本接纳边界与 v16 → v17 迁移的回归。</summary>
public sealed class LyricsSchemaMigrationTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 只有真实v16可进入v17而v1到v15因缺少定义拒绝()
    {
        foreach (int version in Enumerable.Range(1, 15))
        {
            JsonObject document = LegacyLyrics(version);

            Assert.Throws<InvalidOperationException>(() => ProjectSchemaMigrator.Migrate(
                document,
                version,
                LyricsDocument.CurrentSchemaVersion,
                ProjectSchemaMigrator.LyricsDefault));
        }

        JsonObject current = LegacyLyrics(16);
        MigrationLog log = ProjectSchemaMigrator.Migrate(
            current, 16, LyricsDocument.CurrentSchemaVersion, ProjectSchemaMigrator.LyricsDefault);
        Assert.Equal(LyricsDocument.CurrentSchemaVersion, current["schemaVersion"]!.GetValue<int>());
        Assert.NotEmpty(log.Entries);
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

    private static JsonObject LegacyLyrics(int schemaVersion) => new()
    {
        ["schemaVersion"] = schemaVersion,
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
            ["effects"] = JsonNode.Parse("{\"contractVersion\":1,\"simulation\":{\"version\":1,\"stepHz\":120,\"random\":\"pcg32-v1\"},\"budgets\":{\"surfaceMemoryBytes\":536870912,\"liveParticles\":20000,\"trailSamples\":200000,\"emissionRecords\":100000,\"drawCommands\":100000},\"packages\":[],\"instances\":[]}"),
            ["presetApplications"] = JsonNode.Parse("{\"contractVersion\":1,\"templateSchemaVersion\":6,\"items\":[]}"),
            ["lyricPresentation"] = new JsonObject { ["nodes"] = new JsonArray(), ["programs"] = new JsonArray(), ["lineOverrides"] = new JsonArray() },
        };

        _directory.WriteFile(ProjectJson.ProjectFileName, project.ToJsonString(ProjectJson.Options));
    }
}
