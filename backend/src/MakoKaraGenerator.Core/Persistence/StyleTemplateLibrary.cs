using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>模板库的读取结果，坏闭包不会退化成静态模板。</summary>
public sealed record StyleTemplateLibraryLoadResult(IReadOnlyList<StyleTemplate> Templates, IReadOnlyList<string> Issues)
{
    /// <summary>本次一次读取的实际索引字节修订；缺失库为Missing，诊断另行保留。</summary>
    public LibraryIndexRevision Revision { get; init; }

    /// <summary>文件声明的版本。</summary>
    public int? SchemaVersion { get; init; }
    /// <summary>有效版本与当前格式不一致。</summary>
    public bool VersionMismatch { get; init; }
    /// <summary>未读取的旧库仍保留；不是新库错误。</summary>
    public bool LegacyLibraryRetained { get; init; }
}

/// <summary>模板定义与完整包修订的应用级库。</summary>
public static partial class StyleTemplateLibrary
{
    /// <summary>新库目录，旧templates.json保持原位。</summary>
    public const string DirectoryName = "templates-v6";
    /// <summary>新库唯一入口。</summary>
    public const string FileName = "index.json";
    /// <summary>模板当前格式。</summary>
    public const int CurrentSchemaVersion = 6;
    /// <summary>完整规范入口字节限额；资源另受包预算限制。</summary>
    public const int MaximumIndexBytes = 16777216;
    /// <summary>静态只读内置模板。</summary>
    public static IReadOnlyList<StyleTemplate> BuiltIns { get; } =
    [
        StyleTemplate.Create("builtin:line-classic", "经典整行", StyleTemplateKind.Line, LyricStyle.Default, LyricStylePropertyExtensions.All),
        StyleTemplate.Create("builtin:token-highlight", "逐字强调", StyleTemplateKind.Token, LyricStyle.Default, [LyricStyleProperty.UnsungColor, LyricStyleProperty.SungColor]),
    ];
    /// <summary>从配置根取得当前入口，不读取旧库。</summary>
    public static string ResolvePath(string configurationDirectory)
    { ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory); return Path.Combine(configurationDirectory, DirectoryName, FileName); }
    /// <summary>规范受管包路径。</summary>
    public static string PackagePath(string revision) => "packages/" + ProjectEffectsJson.PackagePath(revision)["effects/packages/".Length..];
    /// <summary>完整资源先发布，入口最后替换；不修改旧库、已有或历史包。</summary>
    public static void Save(string path, IEnumerable<StyleTemplate> templates, EffectPackageContentBudget? budget = null)
        => _ = SaveCore(path, templates, budget, null, default);
    /// <summary>资源先发布，最后检查取消及预期索引修订，再发布入口；调用者须持有协作提交锁。</summary>
    public static LibraryIndexRevision Save(string path, IEnumerable<StyleTemplate> templates, LibraryIndexRevision expectedRevision,
        EffectPackageContentBudget? budget = null, CancellationToken cancellationToken = default)
        => SaveCore(path, templates, budget, expectedRevision, cancellationToken);
    private static LibraryIndexRevision SaveCore(string path, IEnumerable<StyleTemplate> templates, EffectPackageContentBudget? budget,
        LibraryIndexRevision? expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var items = Validate(templates); byte[] bytes = EncodeItems(items);
        var usage = new EffectPackageContentUsage(budget); usage.Claim(FileName, bytes.Length);
        foreach (var package in Packages(items))
        {
            usage.Claim(package.Revision + "/manifest.json", package.Manifest.Length);
            foreach (var file in package.Files) usage.Claim(package.Revision + "/" + file.Path, file.Content.Length);
        }
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!; ContentAddressFileStore.EnsureNoLinks(path); Directory.CreateDirectory(directory);
        if (File.Exists(path) && Load(path, budget).Issues.Count != 0)
            throw new IOException("现有模板入口或资源无效；拒绝覆盖，请先恢复原库或使用新的目录。");
        foreach (var package in Packages(items))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EffectPackageStorage.Publish(Path.Combine(directory, PackagePath(package.Revision)), package);
        }
        using var document = JsonDocument.Parse(bytes);
        byte[] published = JsonSerializer.SerializeToUtf8Bytes(document.RootElement, ProjectJson.Options);
        expectedRevision?.RequireCurrent(path, MaximumIndexBytes);
        cancellationToken.ThrowIfCancellationRequested();
        ProjectStore.WriteBytesAtomically(path, published);
        return LibraryIndexRevision.FromBytes(published);
    }
    /// <summary>读取当前入口及其完整资源；不存在时得到空库并报告旧库是否保留。</summary>
    public static StyleTemplateLibraryLoadResult Load(string path, EffectPackageContentBudget? budget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        LibraryIndexRevision revision = LibraryIndexRevision.Missing;
        try
        {
            byte[]? bytes = LibraryIndexRevision.ReadBytes(path, MaximumIndexBytes);
            if (bytes is null)
            {
                string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
                bool retained = Path.GetFileName(parent) == DirectoryName && (File.Exists(Path.Combine(Path.GetDirectoryName(parent!)!, "templates.json"))
                    || File.Exists(Path.Combine(Path.GetDirectoryName(parent!)!, "templates-v4", FileName))
                    || File.Exists(Path.Combine(Path.GetDirectoryName(parent!)!, "templates-v5", FileName)));
                return new([], []) { LegacyLibraryRetained = retained };
            }
            revision = LibraryIndexRevision.FromBytes(bytes);
            var usage = new EffectPackageContentUsage(budget); usage.Claim(FileName, bytes.Length);
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            return Deserialize(new UTF8Encoding(false, true).GetString(bytes), relative => EffectPackageStorage.ReadDirectoryWithUsageAsync(Path.Combine(directory, relative), ProjectEffectsJson.PackageProfile, budget, usage, default).GetAwaiter().GetResult()) with { Revision = revision };
        }
        catch (Exception error) when (StorageError(error)) { return new([], [$"模板库无法读取：{error.Message}"]) { Revision = revision }; }
    }
    /// <summary>固定定义JSON和显式完整包读取器；不执行作者或生成替代资源。</summary>
    public static StyleTemplateLibraryLoadResult Deserialize(string json, Func<string, EffectPackageContents>? readPackage = null)
    {
        ArgumentNullException.ThrowIfNull(json); int? version = null;
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumIndexBytes) throw new IOException("模板入口超过显式16MiB限额。");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int v) || v <= 0)
                throw new ArgumentException("模板库缺少有效的schema版本；文件未更改。");
            version = v;
            if (v != CurrentSchemaVersion) return new([], [$"模板库版本 {v} 与当前要求 {CurrentSchemaVersion} 不一致；文件未更改。"])
                { SchemaVersion = v, VersionMismatch = true };
            Keys(root, "templates", ["schemaVersion", "packages", "templates"], []);
            Dictionary<(string, string), EffectPackageContents> packages = [];
            foreach (var item in Array(root.GetProperty("packages"), "packages"))
            {
                Keys(item, "packages", ["packageId", "revision", "path"], []);
                string id = RequiredText(item, "packageId", "packages"), revision = RequiredText(item, "revision", "packages"), relative = RequiredText(item, "path", "packages");
                if (relative != PackagePath(revision)) throw new ArgumentException("模板包路径与内容修订不符。");
                var content = readPackage?.Invoke(relative) ?? throw new ArgumentException("模板必须提供完整固定包闭包。");
                if (content.Profile != ProjectEffectsJson.PackageProfile || content.PackageId != id || content.Revision != revision || !packages.TryAdd((id, revision), content))
                    throw new ArgumentException("模板包身份、profile、修订不符或重复。");
            }
            List<StyleTemplate> templates = [];
            foreach (var item in Array(root.GetProperty("templates"), "templates"))
            {
                templates.Add(ReadDefinition(item, packages));
            }
            var validated = Validate(templates);
            if (Packages(validated).Count() != packages.Count) throw new ArgumentException("模板入口登记了未引用资源；请显式移除登记。");
            return new(validated, []) { SchemaVersion = v };
        }
        catch (Exception error) when (StorageError(error)) { return new([], [$"模板库无效：{error.Message}"]) { SchemaVersion = version }; }
    }
    internal static bool StorageError(Exception error) => error is ArgumentException or JsonException or IOException or UnauthorizedAccessException or EffectPackageException or OverflowException;
    internal static StyleTemplate[] Validate(IEnumerable<StyleTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates); var items = templates.ToArray();
        if (items.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id) || t.Id.StartsWith("builtin:", StringComparison.Ordinal)) || items.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("个人模板身份无效、重复或冒用内置身份。");
        foreach (var template in items) _ = StyleTemplate.Create(template.Id, template.Name, template.Kind, template.Style, template.Properties, template.EffectRecipes, template.Packages);
        foreach (var package in Packages(items)) if (package.Profile != ProjectEffectsJson.PackageProfile) throw new ArgumentException("模板包profile与当前程序不符。");
        return items;
    }
    /// <summary>取得这些模板的独立资源闭包，按固定身份排序；包库存没有效果执行顺序。</summary>
    public static IEnumerable<EffectPackageContents> Packages(IEnumerable<StyleTemplate> templates)
        => templates.SelectMany(t => t.Packages).DistinctBy(p => (p.PackageId, p.Revision))
            .OrderBy(package => package.PackageId, StringComparer.Ordinal).ThenBy(package => package.Revision, StringComparer.Ordinal);
}
