using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>
/// schema 迁移框架的测试（roadmap CORE-06）。
/// </summary>
/// <remarks>
/// 链本身的语义（多步串联、缺步骤、只能向前）用假步骤验证；
/// 真实注册表那几笔则各自断言“改写数据的只有该改写的那一步”。
/// </remarks>
public sealed class ProjectSchemaMigratorTests
{
    [Fact]
    public void 版本相同时什么也不做()
    {
        JsonObject document = new() { ["schemaVersion"] = 2, ["name"] = "测试" };

        MigrationLog log = ProjectSchemaMigrator.Migrate(document, 2, 2);

        Assert.True(log.IsEmpty);
        Assert.Equal("测试", document["name"]!.GetValue<string>());
    }

    [Fact]
    public void 多步迁移按顺序串联()
    {
        JsonObject document = new() { ["schemaVersion"] = 1, ["steps"] = new JsonArray() };

        MigrationLog log = ProjectSchemaMigrator.Migrate(
            document,
            1,
            3,
            [new RecordingMigration(1, 2, "加字段"), new RecordingMigration(2, 3, "改名字")]);

        // 两步各留一条日志（由框架记录步骤说明）。
        Assert.Equal(2, log.Entries.Count);
        Assert.Contains("加字段", log.Entries[0], StringComparison.Ordinal);
        Assert.Contains("改名字", log.Entries[1], StringComparison.Ordinal);

        // 每一步都真的跑过，且顺序正确。
        Assert.Equal(["v1→v2", "v2→v3"], document["steps"]!.AsArray().Select(static node => node!.GetValue<string>()));
    }

    [Fact]
    public void 缺少步骤会立刻报错而不是当成功()
    {
        JsonObject document = new() { ["schemaVersion"] = 1 };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProjectSchemaMigrator.Migrate(document, 1, 3, [new RecordingMigration(2, 3, "只给了后一段")]));

        Assert.Contains("v1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 不允许向低版本迁移()
    {
        JsonObject document = new() { ["schemaVersion"] = 2 };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProjectSchemaMigrator.Migrate(document, 2, 1));
    }

    [Fact]
    public void 历史注册表止于断代前版本()
    {
        JsonObject document = new() { ["schemaVersion"] = 1 };
        int historicalEnd = ProjectSchemaMigrator.Default[^1].ToVersion;

        MigrationLog log = ProjectSchemaMigrator.Migrate(document, 1, historicalEnd);

        Assert.False(log.IsEmpty);
        Assert.Contains(
            historicalEnd.ToString(System.Globalization.CultureInfo.InvariantCulture),
            log.Entries[^1],
            StringComparison.Ordinal);
    }

    [Fact]
    public void 完整渐变版本迁移保留旧起止色字段()
    {
        IProjectSchemaMigration migration = ProjectSchemaMigrator.Default[^1];
        JsonObject document = new()
        {
            ["schemaVersion"] = migration.FromVersion,
            ["lyricStyle"] = new JsonObject
            {
                ["unsungGradientStartColor"] = "#102030",
                ["unsungGradientEndColor"] = "#405060",
            },
        };

        MigrationLog log = ProjectSchemaMigrator.Migrate(
            document,
            migration.FromVersion,
            migration.ToVersion,
            ProjectSchemaMigrator.Default);

        Assert.False(log.IsEmpty);
        JsonObject style = document["lyricStyle"]!.AsObject();
        Assert.Equal("#102030", style["unsungGradientStartColor"]!.GetValue<string>());
        Assert.Equal("#405060", style["unsungGradientEndColor"]!.GetValue<string>());
    }

    [Fact]
    public void 空引用被拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectSchemaMigrator.Migrate(null!, 1, 2));
    }

    /// <summary>
    /// v3 → v4（工程文件）只写日志，不往旧文件里补默认值。
    /// </summary>
    /// <remarks>
    /// 补值会把“这一版新增了什么”掩盖掉，也让旧文件的 diff 凭空长出一片：
    /// 缺字段的语义本来就是“未设置”。
    /// </remarks>
    [Fact]
    public void 工程文件_v3_迁移到_v4_不改写数据()
    {
        JsonObject document = new()
        {
            ["schemaVersion"] = 3,
            ["lyricStyle"] = new JsonObject { ["fontSizePx"] = 64 },
        };

        MigrationLog log = ProjectSchemaMigrator.Migrate(
            document,
            3,
            4,
            ProjectSchemaMigrator.Default);

        Assert.False(log.IsEmpty);

        JsonObject style = document["lyricStyle"]!.AsObject();
        Assert.Single(style);
        Assert.Equal(64, style["fontSizePx"]!.GetValue<int>());
        Assert.Null(style["strokeWidthPx"]);
        Assert.Null(style["sungGradientStartColor"]);
    }

    /// <summary>开发期那一对单一渐变字段会被译成“已唱渐变”，而不是静默丢弃。</summary>
    [Fact]
    public void 开发期的单一渐变字段被翻译成已唱渐变()
    {
        JsonObject document = new()
        {
            ["schemaVersion"] = 3,
            ["lyricStyle"] = new JsonObject
            {
                ["gradientStartColor"] = "#00E0FFFF",
                ["gradientEndColor"] = "#7C4DFFFF",
            },
        };

        MigrationLog log = ProjectSchemaMigrator.Migrate(
            document,
            3,
            4,
            ProjectSchemaMigrator.Default);

        JsonObject style = document["lyricStyle"]!.AsObject();
        Assert.Null(style["gradientStartColor"]);
        Assert.Null(style["gradientEndColor"]);
        Assert.Equal("#00E0FFFF", style["sungGradientStartColor"]!.GetValue<string>());
        Assert.Equal("#7C4DFFFF", style["sungGradientEndColor"]!.GetValue<string>());
        Assert.Contains(log.Entries, static entry => entry.Contains("已唱渐变", StringComparison.Ordinal));
    }

    [Fact]
    public void 歌词文件_v2_迁移到_v3_不改写数据()
    {
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["lines"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "line-001",
                    ["styleOverride"] = new JsonObject { ["sungColor"] = "#FF0000FF" },
                    ["tokens"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "token-001",
                            ["styleOverride"] = new JsonObject
                            {
                                ["gradientStartColor"] = "#112233FF",
                            },
                        },
                    },
                },
            },
        };

        MigrationLog log = ProjectSchemaMigrator.Migrate(
            document,
            2,
            3,
            ProjectSchemaMigrator.LyricsDefault);

        JsonObject line = document["lines"]!.AsArray()[0]!.AsObject();
        Assert.Single(line["styleOverride"]!.AsObject());

        JsonObject token = line["tokens"]!.AsArray()[0]!.AsObject();
        JsonObject tokenStyle = token["styleOverride"]!.AsObject();
        Assert.Null(tokenStyle["gradientStartColor"]);
        Assert.Equal("#112233FF", tokenStyle["sungGradientStartColor"]!.GetValue<string>());
        Assert.False(log.IsEmpty);
    }

    [Fact]
    public void v6样式迁移会移除当前元素颜色字段()
    {
        JsonObject document = new()
        {
            ["schemaVersion"] = 6,
            ["lyricStyle"] = new JsonObject
            {
                ["unsungColor"] = "#111111FF",
                ["currentColor"] = "#FF0000FF",
                ["currentGradientStartColor"] = "#00FF00FF",
                ["currentGradientEndColor"] = "#0000FFFF",
            },
        };

        MigrationLog log = ProjectSchemaMigrator.Migrate(
            document,
            6,
            ProjectSchemaMigrator.Default[^1].ToVersion,
            ProjectSchemaMigrator.Default);

        JsonObject style = document["lyricStyle"]!.AsObject();
        Assert.Equal("#111111FF", style["unsungColor"]!.GetValue<string>());
        Assert.Null(style["currentColor"]);
        Assert.Null(style["currentGradientStartColor"]);
        Assert.Null(style["currentGradientEndColor"]);
        Assert.Contains(log.Entries, static entry => entry.Contains("当前元素", StringComparison.Ordinal));
    }

    /// <summary>歌词当前格式只为真实 v16 提供 v17 迁移；历史断代格式不能仅改版本号读取。</summary>
    [Fact]
    public void 歌词文件从_v16_迁到_v17()
    {
        JsonObject document = new()
        {
            ["schemaVersion"] = 16,
            ["branches"] = new JsonArray(),
        };

        MigrationLog log = ProjectSchemaMigrator.Migrate(
            document,
            16,
            17,
            ProjectSchemaMigrator.LyricsDefault);

        Assert.Equal(17, document["schemaVersion"]!.GetValue<int>());
        Assert.Contains(log.Entries, static entry => entry.Contains("v16", StringComparison.Ordinal));
    }

    /// <summary>两张步骤表讲的是不同的事，不能互相借。</summary>
    [Fact]
    public void 工程与歌词的步骤表互不借用()
    {
        Assert.Contains(
            ProjectSchemaMigrator.Default,
            static step => step.Description.Contains("音频", StringComparison.Ordinal));
        Assert.DoesNotContain(
            ProjectSchemaMigrator.LyricsDefault,
            static step => step.Description.Contains("音频", StringComparison.Ordinal));

        // ADR-0050 后不再把断代歌词格式桥接为当前格式；历史步骤仍可独立用于旧数据工具。
        AssertChain(ProjectSchemaMigrator.Default, 1, ProjectSchemaMigrator.Default[^1].ToVersion);
        AssertChain(ProjectSchemaMigrator.LyricsDefault.Take(7).ToArray(), 1, 8);
        AssertChain(ProjectSchemaMigrator.LyricsDefault, 16, 17);
    }

    /// <summary>验证一张步骤表能从起始版本一路走到目标版本。</summary>
    private static void AssertChain(
        IReadOnlyList<IProjectSchemaMigration> steps,
        int fromVersion,
        int toVersion)
    {
        JsonObject document = new() { ["schemaVersion"] = fromVersion };

        ProjectSchemaMigrator.Migrate(document, fromVersion, toVersion, steps);
    }

    /// <summary>一个只记录自己跑过的假迁移步骤。</summary>
    private sealed class RecordingMigration(int fromVersion, int toVersion, string description) : IProjectSchemaMigration
    {
        public int FromVersion { get; } = fromVersion;

        public int ToVersion { get; } = toVersion;

        public string Description { get; } = description;

        public void Apply(JsonObject document, MigrationLog log)
        {
            ArgumentNullException.ThrowIfNull(document);

            ArgumentNullException.ThrowIfNull(log);

            if (document["steps"] is JsonArray steps)
            {
                steps.Add($"v{FromVersion}→v{ToVersion}");
            }
        }
    }
}
