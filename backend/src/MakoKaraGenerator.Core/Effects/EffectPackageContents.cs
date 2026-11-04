using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>宿主明确支持的包合同；不从应用版本推断 SDK 或 profile。</summary>
/// <param name="PackageVersion">精确包格式版本。</param><param name="SdkVersion">实际 SDK 合同。</param><param name="ShaderProfile">精确后端 profile。</param>
public sealed record EffectPackageProfile(int PackageVersion, string SdkVersion, string ShaderProfile);

/// <summary>固定发射器声明；尚未接通的发射器不能以空实现获准执行。</summary>
/// <param name="Id">包内唯一身份。</param><param name="Kind">已声明种类。</param><param name="MaxLifetimeUs">最大寿命。</param>
public sealed record EffectEmitterDefinition(string Id, string Kind, long MaxLifetimeUs);

/// <summary>内容闭包错误，路径为逻辑包路径，不泄漏机器资源引用。</summary>
public sealed class EffectPackageException : Exception
{
    /// <summary>建立带位置的明确失败。</summary>
    public EffectPackageException(string code, string path, string message, Exception? inner = null) : base(message, inner)
    { Code = code; Path = path; }
    /// <summary>稳定诊断编号。</summary>
    public string Code { get; }
    /// <summary>包内路径或清单字段。</summary>
    public string Path { get; }
}

/// <summary>内容地址下的唯一、不可变资源；不持有本地路径。</summary>
public sealed class EffectPackageFile
{
    internal EffectPackageFile(string path, string role, string hash, ReadOnlyMemory<byte> content)
    { Path = path; Role = role; Sha256 = hash; Content = [.. content.Span]; }
    /// <summary>大小写敏感的包内相对路径。</summary>
    public string Path { get; }
    /// <summary>清单明确声明的用途，未知用途不被执行器暗中解释。</summary>
    public string Role { get; }
    /// <summary>完整、小写文件摘要。</summary>
    public string Sha256 { get; }
    /// <summary>独立复制的不可变原字节。</summary>
    public ImmutableArray<byte> Content { get; }
    /// <summary>严格读取 UTF-8，不修复错误字符。</summary>
    public string ReadUtf8() => EffectPackageContents.Decode(Content.AsSpan(), Path);
}

/// <summary>已验证的文件闭包；参数／导出完整语义未检查，不能直接作为可运行包启用。</summary>
public sealed class EffectPackageContents
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private EffectPackageContents(string id, string name, string version, EffectPackageProfile profile,
        ImmutableArray<byte> manifest, ImmutableArray<EffectPackageFile> files, string revision,
        ImmutableArray<string> requires, long lineExitDurationUs, ImmutableArray<EffectEmitterDefinition> emitters)
    { PackageId = id; Name = name; Version = version; Profile = profile; Manifest = manifest; Files = files; Revision = revision;
        Requires = requires; LineExitDurationUs = lineExitDurationUs; Emitters = emitters; }
    /// <summary>清单包身份。</summary>
    public string PackageId { get; }
    /// <summary>用户可读名称。</summary>
    public string Name { get; }
    /// <summary>作者声明的产品版本；不替代内容修订。</summary>
    public string Version { get; }
    /// <summary>明确匹配的三条合同。</summary>
    public EffectPackageProfile Profile { get; }
    /// <summary>保留全部字段的规范 UTF-8 清单；不是另一份参数模型。</summary>
    public ImmutableArray<byte> Manifest { get; }
    /// <summary>ordinal 路径排序的完整资源。</summary>
    public ImmutableArray<EffectPackageFile> Files { get; }
    /// <summary>包括清单及所有资源的 sha256 内容修订。</summary>
    public string Revision { get; }
    /// <summary>包级必须能力；v1 没有此声明。</summary>
    public ImmutableArray<string> Requires { get; }
    /// <summary>固定逐行退场寿命；v1 为零。</summary>
    public long LineExitDurationUs { get; }
    /// <summary>已验证的固定发射器声明；能力在执行器开通前明确拒绝。</summary>
    public ImmutableArray<EffectEmitterDefinition> Emitters { get; }

    /// <summary>按明确 v1／v2 身份分派后验证完整闭包，不将未知 SDK 回退为旧执行器。</summary>
    public static EffectPackageContents CreateSupported(ReadOnlyMemory<byte> manifest,
        IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> files, EffectPackageContentBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        new EffectPackageContentUsage(budget).Claim("manifest.json", manifest.Length);
        return Create(manifest, files, EffectPackageProfiles.SelectSupported(manifest), budget, cancellationToken);
    }

    /// <summary>验证完整清单与文件集合，复制内容；缺失、额外文件、坏摘要及版本均失败。</summary>
    public static EffectPackageContents Create(ReadOnlyMemory<byte> manifest,
        IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> files, EffectPackageProfile expected, EffectPackageContentBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files); ArgumentNullException.ThrowIfNull(expected);
        if (expected.PackageVersion <= 0 || string.IsNullOrWhiteSpace(expected.SdkVersion) || string.IsNullOrWhiteSpace(expected.ShaderProfile))
            throw new ArgumentException("宿主必须明确支持的包／SDK／profile。", nameof(expected));
        cancellationToken.ThrowIfCancellationRequested();
        EffectPackageContentUsage usage = new(budget); usage.Claim("manifest.json", manifest.Length);
        using var document = Parse(manifest.Span);
        var root = document.RootElement;
        bool v2 = EffectPackageProfiles.IsV2(expected);
        Keys(root, "manifest.json", v2
            ? ["packageVersion", "packageId", "name", "version", "sdkVersion", "shaderProfile", "requires", "lineExit", "emitters", "files", "parameters", "exports"]
            : ["packageVersion", "packageId", "name", "version", "sdkVersion", "shaderProfile", "files", "parameters", "exports"]);
        if (root.GetProperty("packageVersion").ValueKind != JsonValueKind.Number
            || !root.GetProperty("packageVersion").TryGetInt32(out int packageVersion))
            throw Error("PackageProfileMismatch", "manifest.json", "包版本、SDK 或 shader profile 与明确支持值不一致。");
        var actual = new EffectPackageProfile(packageVersion, Text(root, "sdkVersion", "manifest.json"), Text(root, "shaderProfile", "manifest.json"));
        if (!EffectPackageProfiles.Compatible(actual, expected))
            throw Error("PackageProfileMismatch", "manifest.json", "包版本、SDK 或 shader profile 与明确支持值不一致。");
        ImmutableArray<string> requires = []; ImmutableArray<EffectEmitterDefinition> emitters = []; long lineExitDurationUs = 0;
        if (v2)
        {
            requires = EffectPackageJson.Strings(root.GetProperty("requires"), "manifest.json.requires");
            foreach (string capability in requires) EffectPackageProfiles.RequireKnownCapability(capability, "manifest.json.requires");
            var exit = root.GetProperty("lineExit"); Keys(exit, "manifest.json.lineExit", ["durationUs"]);
            if (exit.GetProperty("durationUs").ValueKind != JsonValueKind.Number
                || !exit.GetProperty("durationUs").TryGetInt64(out lineExitDurationUs) || lineExitDurationUs is < 0 or > 3_000_000)
                throw Error("PackageLineExitInvalid", "manifest.json.lineExit.durationUs", "退场寿命须为 0 至 3000000 的整数微秒。");
            if (lineExitDurationUs > 0 && !requires.Contains("line.exit", StringComparer.Ordinal))
                throw Error("PackageCapabilityMissing", "manifest.json.requires", "非零退场寿命必须声明 line.exit。");
            emitters = ReadEmitters(root.GetProperty("emitters"), requires, cancellationToken);
        }
        string id = Text(root, "packageId", "manifest.json"), name = Text(root, "name", "manifest.json"), version = Text(root, "version", "manifest.json");
        foreach (string field in new[] { "parameters", "exports", "files" })
            if (root.GetProperty(field).ValueKind != JsonValueKind.Array) throw Error("PackageManifestType", field, "清单集合必须为数组。");
        Dictionary<string, ReadOnlyMemory<byte>> contents = new(StringComparer.Ordinal);
        foreach (var pair in files)
        {
            cancellationToken.ThrowIfCancellationRequested(); ValidatePath(pair.Key);
            if (pair.Key == "manifest.json" || !contents.TryAdd(pair.Key, pair.Value))
                throw Error("PackageFileDuplicate", pair.Key, "文件重复或清单列入自身。");
            usage.Claim(pair.Key, pair.Value.Length);
        }
        ValidatePaths(contents.Keys.Append("manifest.json"), []);
        List<EffectPackageFile> verified = []; HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("files").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested(); Keys(entry, "files", ["path", "role", "sha256"]);
            string path = Text(entry, "path", "files"), role = Text(entry, "role", "files"), hash = Text(entry, "sha256", "files");
            ValidatePath(path);
            if (path == "manifest.json" || !declared.Add(path)) throw Error("PackageFileDuplicate", path, "资源声明重复或清单列入自身。");
            if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw Error("PackageFileHashInvalid", path, "文件摘要须为完整小写 SHA-256。");
            if (!contents.TryGetValue(path, out var content)) throw Error("PackageFileMissing", path, "包中缺少声明的资源。");
            // 在校验前固定字节，避免调用者之后修改数组破坏已确认闭包。
            var file = new EffectPackageFile(path, role, hash, content);
            if (Convert.ToHexStringLower(SHA256.HashData(file.Content.AsSpan())) != hash)
                throw Error("PackageFileHashMismatch", path, "文件内容与清单摘要不一致。");
            if (role is "csharp" or "shader" or "graph" or "shaderProgram") _ = file.ReadUtf8();
            verified.Add(file);
        }
        ValidatePaths(declared.Append("manifest.json"), []);
        var extra = contents.Keys.FirstOrDefault(path => !declared.Contains(path));
        if (extra is not null) throw Error("PackageFileUndeclared", extra, "包中存在未声明文件。");
        verified.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        using MemoryStream canonical = new();
        using (var writer = new Utf8JsonWriter(canonical)) WriteCanonical(writer, root, "manifest.json", cancellationToken);
        ImmutableArray<byte> normalized = [.. canonical.ToArray()];
        // 分享使用规范清单；同时验证规范后的闭包，不发布无法按相同预算重新读取的包。
        EffectPackageContentUsage normalizedUsage = new(budget); normalizedUsage.Claim("manifest.json", normalized.Length);
        foreach (var file in verified) normalizedUsage.Claim(file.Path, file.Content.Length);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(digest, normalized.AsSpan());
        Span<byte> size = stackalloc byte[8];
        foreach (var file in verified)
        {
            cancellationToken.ThrowIfCancellationRequested(); Add(digest, Encoding.UTF8.GetBytes(file.Path));
            BinaryPrimitives.WriteInt64LittleEndian(size, file.Content.Length); Add(digest, size);
            Add(digest, Convert.FromHexString(file.Sha256));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = new EffectPackageContents(id, name, version, actual, normalized, [.. verified], "sha256:" + Convert.ToHexStringLower(digest.GetHashAndReset()),
            requires, lineExitDurationUs, emitters);
        // 描述解析只读取本固定闭包，不递归构造包；known pending 不代表执行能力获准。
        _ = EffectPackageSvgResources.Read(result, cancellationToken);
        return result;
    }

    private static ImmutableArray<EffectEmitterDefinition> ReadEmitters(JsonElement value, ImmutableArray<string> requires, CancellationToken token)
    {
        const string path = "manifest.json.emitters";
        if (value.ValueKind != JsonValueKind.Array) throw Error("PackageManifestType", path, "发射器声明必须为数组。");
        if (value.GetArrayLength() > 32) throw Error("PackageEmitterBudgetExceeded", path, "单包发射器不能超过 32 个。");
        var result = ImmutableArray.CreateBuilder<EffectEmitterDefinition>(); HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); string itemPath = path + "[" + result.Count + "]";
            Keys(entry, itemPath, ["id", "kind", "maxLifetimeUs"]);
            string id = Text(entry, "id", itemPath), kind = Text(entry, "kind", itemPath);
            if (!ids.Add(id)) throw Error("PackageEmitterDuplicate", itemPath + ".id", "发射器身份重复。");
            string capability = kind switch
            {
                "cursor" => "particles.continuous", "burst" => "particles.burst",
                _ => throw Error("PackageEmitterKindUnsupported", itemPath + ".kind", "发射器 kind 须明确为 cursor 或 burst。"),
            };
            var lifetime = entry.GetProperty("maxLifetimeUs");
            if (lifetime.ValueKind != JsonValueKind.Number || !lifetime.TryGetInt64(out long maximum) || maximum is <= 0 or > 5_000_000)
                throw Error("PackageEmitterLifetimeInvalid", itemPath + ".maxLifetimeUs", "最大寿命须为 1 至 5000000 的整数微秒。");
            EffectPackageProfiles.RequireCapability(capability, itemPath);
            if (!requires.Contains(capability, StringComparer.Ordinal))
                throw Error("PackageCapabilityMissing", itemPath, "发射器必须同时在包 requires 中声明能力：" + capability);
            result.Add(new(id, kind, maximum));
        }
        return result.ToImmutable();
    }

    /// <summary>检查跨平台相对逻辑路径，不将不合法路径规范化成另一路径。</summary>
    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal)
            || path.Split('/').Any(segment => segment is "" or "." or ".." || segment.Any(char.IsControl)))
            throw Error("PackagePathInvalid", path ?? "", "包路径必须为非空、相对正斜线路径。");
    }
    /// <summary>验证文件／目录集合的大小写段与前缀冲突；目录条目不成为资源内容。</summary>
    public static void ValidatePaths(IEnumerable<string> filePaths, IEnumerable<string> directoryPaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths); ArgumentNullException.ThrowIfNull(directoryPaths);
        var files = filePaths.ToHashSet(StringComparer.Ordinal); var directories = directoryPaths.ToArray();
        Dictionary<string, string> prefixes = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in files.Concat(directories))
        {
            ValidatePath(path);
            var segments = path.Split('/'); string prefix = "";
            for (int i = 0; i < segments.Length; i++)
            {
                prefix = i == 0 ? segments[i] : prefix + "/" + segments[i];
                if (prefixes.TryGetValue(prefix, out var prior) && prior != prefix)
                    throw Error("PackagePathCaseConflict", path, "包路径段在跨平台存在大小写冲突。");
                prefixes[prefix] = prefix;
                if ((i < segments.Length - 1 || directories.Contains(path, StringComparer.Ordinal)) && files.Contains(prefix))
                    throw Error("PackagePathPrefixConflict", path, "文件与目录前缀冲突。");
            }
        }
    }
    internal static string Decode(ReadOnlySpan<byte> content, string path)
    {
        try { return StrictUtf8.GetString(content); }
        catch (DecoderFallbackException exception) { throw Error("PackageUtf8Invalid", path, "文本必须为严格 UTF-8。", exception); }
    }
    private static JsonDocument Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var text = Decode(bytes, "manifest.json");
            return JsonDocument.Parse(text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text);
        }
        catch (JsonException exception) { throw Error("PackageManifestInvalid", "manifest.json", "清单不是合法 JSON。", exception); }
    }
    private static void Keys(JsonElement value, string path, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error("PackageManifestType", path, "清单字段必须为对象。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw Error("PackageJsonDuplicateKey", path + "." + property.Name, "JSON 字段重复。");
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw Error("PackageManifestUnknownField", path + "." + property.Name, "未知清单字段。");
        }
        foreach (string field in allowed) if (!names.Contains(field)) throw Error("PackageManifestMissingField", path + "." + field, "清单缺字段。");
    }
    private static string Text(JsonElement parent, string key, string path)
    {
        var value = parent.GetProperty(key);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw Error("PackageManifestType", path + "." + key, "清单文本字段必须明确非空。");
        return value.GetString()!;
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw Error("PackageJsonDuplicateKey", path, "JSON 对象存在重复键。");
            writer.WriteStartObject();
            foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value, path + "." + property.Name, token); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); int index = 0;
            foreach (var entry in value.EnumerateArray()) WriteCanonical(writer, entry, path + "[" + index++ + "]", token);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
    private static void Add(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
        hash.AppendData(length); hash.AppendData(bytes);
    }
    private static EffectPackageException Error(string code, string path, string message, Exception? inner = null) => new(code, path, message, inner);
}
