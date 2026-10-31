using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class RubyTextSegmentTests
{
    [Fact]
    public void 注音工厂为旧文本建立单个无时间兼容片段()
    {
        RubyAnnotation ruby = RubyAnnotation.Create("ruby-1", "token-1", "せん");

        Assert.Equal("せん", ruby.Text);
        Assert.Equal(["ruby-1-segment-1"], ruby.Segments.Select(segment => segment.Id));
        Assert.Equal(TimingStatus.Missing, ruby.Segments[0].TimingStatus);
        Assert.Null(ruby.Segments[0].Time);
    }

    [Fact]
    public void 多个注音片段保持独立时间和拼接投影()
    {
        RubyAnnotation ruby = RubyAnnotation.Create("ruby-1", "token-1", "せん") with
        {
            Segments =
            [
                RubyTextSegment.Create("segment-1", "せ", new TimeRange(10, 20), TimingStatus.Imported),
                RubyTextSegment.Create("segment-2", "ん", new TimeRange(20, 35), TimingStatus.Edited),
            ],
        };

        Assert.True(ruby.IsValid);
        Assert.Equal("せん", string.Concat(ruby.Segments.Select(segment => segment.Text)));
        Assert.Equal(new TimeRange(10, 20), ruby.Segments[0].Time);
        Assert.Equal(new TimeRange(20, 35), ruby.Segments[1].Time);
    }

    [Fact]
    public void v16迁移从ruby文本创建稳定兼容片段并处理ID冲突()
    {
        JsonObject document = new()
        {
            ["schemaVersion"] = 16,
            ["branches"] = new JsonArray
            {
                new JsonObject
                {
                    ["lines"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["rubyAnnotations"] = new JsonArray
                            {
                                new JsonObject { ["id"] = "ruby", ["text"] = "せ" },
                                new JsonObject { ["id"] = "ruby", ["text"] = "ん" },
                            },
                        },
                    },
                },
            },
        };

        MigrationLog log = ProjectSchemaMigrator.Migrate(document, 16, 17, ProjectSchemaMigrator.LyricsDefault);
        JsonArray rubies = document["branches"]![0]!["lines"]![0]!["rubyAnnotations"]!.AsArray();

        Assert.Equal(17, document["schemaVersion"]!.GetValue<int>());
        Assert.Equal("ruby-segment-1", rubies[0]!["segments"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("ruby-segment-1-2", rubies[1]!["segments"]![0]!["id"]!.GetValue<string>());
        Assert.Contains(log.Entries, static entry => entry.Contains("Missing", StringComparison.Ordinal));
    }
    [Fact]
    public void 长身份迁移保持原身份并生成有界稳定且不冲突的片段身份()
    {
        string id = new('字', 128);
        JsonObject document = new() { ["schemaVersion"] = 16, ["rubyAnnotations"] = new JsonArray(new JsonObject { ["id"] = id, ["text"] = "せ" }, new JsonObject { ["id"] = id, ["text"] = "ん" }) };
        var copy = document.DeepClone().AsObject();
        ProjectSchemaMigrator.Migrate(document, 16, 17, ProjectSchemaMigrator.LyricsDefault);
        ProjectSchemaMigrator.Migrate(copy, 16, 17, ProjectSchemaMigrator.LyricsDefault);
        Assert.Equal(document.ToJsonString(), copy.ToJsonString());
        var rubies = document["rubyAnnotations"]!.AsArray();
        Assert.Equal(id, rubies[0]!["id"]!.GetValue<string>());
        string first = rubies[0]!["segments"]![0]!["id"]!.GetValue<string>(), second = rubies[1]!["segments"]![0]!["id"]!.GetValue<string>();
        Assert.InRange(first.Length, 1, 128); Assert.InRange(second.Length, 1, 128); Assert.NotEqual(first, second);
    }

}
