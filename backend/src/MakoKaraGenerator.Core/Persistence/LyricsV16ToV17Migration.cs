using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>歌词文件 v16 → v17：把旧的聚合注音文本显式化为一个无时间片段。</summary>
/// <remarks>
/// 迁移只补兼容结构，不按字符或 mora 猜测时间。片段标识由 ruby 标识派生，
/// 并在整份 JSON 文档内检查冲突；源文件不会由读取流程就地改写。
/// </remarks>
internal sealed class LyricsV16ToV17Migration : IProjectSchemaMigration
{
    public int FromVersion => 16;
    public int ToVersion => 17;
    public string Description => "注音文本拆分为无时间兼容片段";

    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        HashSet<string> identifiers = [];
        CollectIdentifiers(document, identifiers);
        int created = 0;
        Visit(document, identifiers, ref created);
        document["schemaVersion"] = ToVersion;
        log.Add("v16 注音缺少 segments：每个旧 text 创建一个 Missing 兼容片段，不拆分文本或推测时间。");
        if (created > 0)
            log.Add($"已创建 {created} 个兼容注音片段；其时间状态为 Missing。");
    }

    private static void CollectIdentifiers(JsonNode? node, HashSet<string> identifiers)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in new[] { "id", "baseTokenId", "primaryLineId" })
                if (obj[key] is JsonValue value && value.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
                    identifiers.Add(id);
            foreach (KeyValuePair<string, JsonNode?> child in obj)
                CollectIdentifiers(child.Value, identifiers);
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array) CollectIdentifiers(child, identifiers);
        }
    }

    private static void Visit(JsonNode? node, HashSet<string> identifiers, ref int created)
    {
        if (node is JsonObject obj)
        {
            if (obj["rubyAnnotations"] is JsonArray rubies)
            {
                foreach (JsonNode? rubyNode in rubies)
                {
                    if (rubyNode is not JsonObject ruby || ruby.ContainsKey("segments")) continue;
                    string text = ruby["text"] is JsonValue textValue && textValue.TryGetValue(out string? value)
                        ? value ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrEmpty(text))
                    {
                        ruby["segments"] = new JsonArray();
                        continue;
                    }

                    string rubyId = ruby["id"] is JsonValue idValue && idValue.TryGetValue(out string? id)
                        && !string.IsNullOrWhiteSpace(id)
                        ? id
                        : "ruby";
                    string segmentId = UniqueSegmentId(RubyTextSegment.CompatibilityId(rubyId), identifiers);
                    ruby["segments"] = new JsonArray(new JsonObject
                    {
                        ["id"] = segmentId,
                        ["text"] = text,
                        ["timingState"] = "Missing",
                    });
                    created++;
                }
            }

            foreach (KeyValuePair<string, JsonNode?> child in obj)
                Visit(child.Value, identifiers, ref created);
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array) Visit(child, identifiers, ref created);
        }
    }

    private static string UniqueSegmentId(string candidate, HashSet<string> identifiers)
    {
        if (identifiers.Add(candidate)) return candidate;
        for (int suffix = 2; suffix < int.MaxValue; suffix++)
        {
            string next = candidate + "-" + suffix;
            if (identifiers.Add(next)) return next;
        }
        throw new InvalidOperationException("注音片段标识空间已耗尽。");
    }
}
