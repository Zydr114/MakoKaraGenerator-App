using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>共享配置库的严格读取结果，失败不返回部分配置。</summary>
public sealed record ScriptConfigurationLibraryLoadResult(IReadOnlyList<ScriptConfiguration> Configurations, IReadOnlyList<string> Issues)
{
    /// <summary>本次一次读取的实际索引字节修订；缺失库为Missing，诊断另行保留。</summary>
    public LibraryIndexRevision Revision { get; init; }

    /// <summary>文件声明的格式。</summary>
    public int? SchemaVersion { get; init; }
    /// <summary>版本不匹配，原文件保持。</summary>
    public bool VersionMismatch { get; init; }
}

/// <summary>歌曲无关的脚本配置与固定资源闭包；包先发布、入口最后替换。</summary>
public static partial class ScriptConfigurationLibrary
{
    /// <summary>与模板库独立的应用级目录。</summary>
    public const string DirectoryName = "script-configurations-v1";
    /// <summary>配置库入口。</summary>
    public const string FileName = "index.json";
    /// <summary>当前独立格式。</summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>配置元数据上限。</summary>
    public const int MaximumIndexBytes = 16777216;

    /// <summary>从应用配置根取得入口，不使用工程路径。</summary>
    public static string ResolvePath(string configurationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);
        return Path.Combine(configurationDirectory, DirectoryName, FileName);
    }

    /// <summary>严格固定资源地址。</summary>
    public static string PackagePath(string revision)
        => "packages/" + ProjectEffectsJson.PackagePath(revision)["effects/packages/".Length..];

    /// <summary>保存完整配置；失败保留旧入口，历史包从不删除。</summary>
    public static void Save(string path, IEnumerable<ScriptConfiguration> configurations, EffectPackageContentBudget? budget = null)
        => _ = SaveCore(path, configurations, budget, null, default);
    /// <summary>资源先发布，最后检查取消及预期索引修订，再发布入口；调用者须持有协作提交锁。</summary>
    public static LibraryIndexRevision Save(string path, IEnumerable<ScriptConfiguration> configurations, LibraryIndexRevision expectedRevision,
        EffectPackageContentBudget? budget = null, CancellationToken cancellationToken = default)
        => SaveCore(path, configurations, budget, expectedRevision, cancellationToken);
    private static LibraryIndexRevision SaveCore(string path, IEnumerable<ScriptConfiguration> configurations, EffectPackageContentBudget? budget,
        LibraryIndexRevision? expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var items = Validate(configurations); byte[] bytes = EncodeItems(items);
        var usage = new EffectPackageContentUsage(budget); usage.Claim(FileName, bytes.Length);
        var packages = Packages(items).ToArray();
        foreach (var package in packages)
        {
            usage.Claim(package.Revision + "/manifest.json", package.Manifest.Length);
            foreach (var file in package.Files) usage.Claim(package.Revision + "/" + file.Path, file.Content.Length);
        }
        path = Path.GetFullPath(path); ContentAddressFileStore.EnsureNoLinks(path);
        string directory = Path.GetDirectoryName(path)!;
        if (File.Exists(path) && Load(path, budget).Issues.Count != 0)
            throw new IOException("现有脚本配置入口或资源无效；拒绝覆盖，请先恢复或使用新的目录。");
        Directory.CreateDirectory(directory);
        foreach (var package in packages)
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

    /// <summary>严格读取一次入口和完整资源；缺失库返回空，坏库返回诊断。</summary>
    public static ScriptConfigurationLibraryLoadResult Load(string path, EffectPackageContentBudget? budget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        LibraryIndexRevision revision = LibraryIndexRevision.Missing;
        try
        {
            byte[]? bytes = LibraryIndexRevision.ReadBytes(path, MaximumIndexBytes);
            if (bytes is null) return new([], []);
            revision = LibraryIndexRevision.FromBytes(bytes);
            var usage = new EffectPackageContentUsage(budget); usage.Claim(FileName, bytes.Length);
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            return Deserialize(new UTF8Encoding(false, true).GetString(bytes), relative =>
                EffectPackageStorage.ReadDirectoryWithUsageAsync(Path.Combine(directory, relative),
                    ProjectEffectsJson.PackageProfile, budget, usage, default).GetAwaiter().GetResult()) with { Revision = revision };
        }
        catch (Exception error) when (StyleTemplateLibrary.StorageError(error))
        { return new([], [$"脚本配置库无效：{error.Message}"]) { Revision = revision }; }
    }

    /// <summary>读取严格元数据及显式固定包，不依赖机器上的编译缓存。</summary>
    public static ScriptConfigurationLibraryLoadResult Deserialize(string json, Func<string, EffectPackageContents>? readPackage = null)
    {
        ArgumentNullException.ThrowIfNull(json); int? version = null;
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumIndexBytes) throw new IOException("脚本配置入口超过16MiB限额。");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int value) || value <= 0)
                throw new ArgumentException("脚本配置库缺少有效schema版本。");
            version = value;
            if (value != CurrentSchemaVersion) return new([], [$"脚本配置库版本{value}与当前要求{CurrentSchemaVersion}不一致；文件未更改。"])
                { SchemaVersion = value, VersionMismatch = true };
            Keys(root, "scriptConfigurations", ["schemaVersion", "packages", "configurations"], []);
            Dictionary<(string, string), EffectPackageContents> packages = [];
            foreach (var item in Array(root.GetProperty("packages"), "packages"))
            {
                Keys(item, "package", ["packageId", "revision", "path"], []);
                string id = RequiredText(item, "packageId", "package"), revision = RequiredText(item, "revision", "package"), relative = RequiredText(item, "path", "package");
                if (relative != PackagePath(revision)) throw new ArgumentException("配置包路径与内容地址不一致。");
                var package = readPackage?.Invoke(relative) ?? throw new ArgumentException("配置缺少完整固定包闭包。");
                if (package.Profile != ProjectEffectsJson.PackageProfile || package.PackageId != id || package.Revision != revision || !packages.TryAdd((id, revision), package))
                    throw new ArgumentException("配置包身份、profile或修订无效／重复。");
            }
            var items = Array(root.GetProperty("configurations"), "configurations").Select(item => ReadConfiguration(item, packages)).ToArray();
            _ = Validate(items);
            if (Packages(items).Count() != packages.Count) throw new ArgumentException("配置库包含未引用的包登记。");
            return new(items, []) { SchemaVersion = value };
        }
        catch (Exception error) when (StyleTemplateLibrary.StorageError(error))
        { return new([], [$"脚本配置库无效：{error.Message}"]) { SchemaVersion = version }; }
    }

    private static ScriptConfiguration[] Validate(IEnumerable<ScriptConfiguration> configurations)
    {
        ArgumentNullException.ThrowIfNull(configurations); var items = configurations.ToArray();
        if (items.Any(item => item is null || item.Id.StartsWith("builtin:", StringComparison.Ordinal))
            || items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("配置身份无效、重复或冒用内置身份。", nameof(configurations));
        foreach (var item in items) _ = ScriptConfiguration.Create(item.Id, item.Name, item.Package, item.ExportId,
            item.Parameters, item.Target, item.ReadScope, item.BranchInput, item.FixedSeed, item.OverlayLayer);
        return items;
    }

    private static IEnumerable<EffectPackageContents> Packages(IEnumerable<ScriptConfiguration> items)
        => items.Select(item => item.Package).DistinctBy(package => (package.PackageId, package.Revision))
            .OrderBy(package => package.PackageId, StringComparer.Ordinal).ThenBy(package => package.Revision, StringComparer.Ordinal);
}
