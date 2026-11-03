using System.Text.Json;

namespace MakoKaraGenerator.Core.Persistence;

public static partial class StyleTemplateLibrary
{
    /// <summary>有界读取索引声明的唯一正整数版本，不加载模板或包；仅入口缺失时返回null，非法或无法读取时拒绝。</summary>
    public static int? ReadIndexSchemaVersion(string path)
    {
        byte[]? bytes = LibraryIndexRevision.ReadBytes(path, MaximumIndexBytes);
        if (bytes is null) return null;
        using var document = JsonDocument.Parse(bytes, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("模板入口必须为JSON对象。");
        int? version = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.NameEquals("schemaVersion")) continue;
            if (version is not null || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int value) || value <= 0)
                throw new IOException("模板入口必须声明唯一正整数版本。");
            version = value;
        }
        return version ?? throw new IOException("模板入口缺少版本声明。");
    }
}
