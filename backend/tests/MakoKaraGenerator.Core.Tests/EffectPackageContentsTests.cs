using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests;

public sealed class EffectPackageContentsTests
{
    private static EffectPackageContents Read(JsonObject manifest, Dictionary<string, byte[]> files, EffectPackageProfile? profile = null)
        => EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), profile ?? EffectPackageFixture.Profile);
    [Fact]
    public void 包内容固定独立字节并保存全部规范字段不受外部数组或清单修改影响()
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files); var bytes = EffectPackageFixture.Bytes(manifest);
        var contents = EffectPackageContents.Create(bytes, EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var revision = contents.Revision; files["preview.bin"][0] = 99; Array.Fill(bytes, (byte)0); manifest["name"] = "changed";
        Assert.Equal("Demo", contents.Name); Assert.Equal(revision, contents.Revision); Assert.Equal(71, revision.Length);
        Assert.Equal(1, contents.Files.Single(file => file.Path == "preview.bin").Content[0]);
        var canonical = JsonNode.Parse(contents.Manifest.AsSpan())!.AsObject(); Assert.Equal("PackEffect", canonical["exports"]![0]!["entryType"]!.GetValue<string>());
        Assert.Equal(contents.Files.OrderBy(file => file.Path, StringComparer.Ordinal).Select(file => file.Path), contents.Files.Select(file => file.Path));
    }
    [Fact]
    public void 空白对象属性顺序与输入枚举顺序不改变修订但数字词形与资源及默认值会改变()
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files); var original = Read(manifest, files);
        JsonObject reversed = new(); foreach (var property in manifest.Reverse()) reversed.Add(property.Key, property.Value?.DeepClone());
        var alternate = EffectPackageContents.Create(Encoding.UTF8.GetBytes(reversed.ToJsonString(new() { WriteIndented = true })),
            EffectPackageFixture.Inputs(files).Reverse(), EffectPackageFixture.Profile);
        Assert.Equal(original.Revision, alternate.Revision); Assert.True(original.Manifest.AsSpan().SequenceEqual(alternate.Manifest.AsSpan()));
        var changed = EffectPackageFixture.Contents(9); Assert.NotEqual(original.Revision, changed.Revision);
        manifest["parameters"]![0]!["default"] = 2.5; Assert.NotEqual(original.Revision, Read(manifest, files).Revision);
        var source = Encoding.UTF8.GetString(alternate.Manifest.AsSpan());
        var decimalForm = EffectPackageContents.Create(Encoding.UTF8.GetBytes(source.Replace("\"max\":4", "\"max\":4.0", StringComparison.Ordinal)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        Assert.NotEqual(alternate.Revision, decimalForm.Revision);
    }
    [Theory]
    [InlineData("../x")] [InlineData("/x")] [InlineData("src\\x.cs")] [InlineData("C:/x")]
    [InlineData("src//x")] [InlineData("src/./x")] [InlineData("src/..")]
    public void 非法路径不被整理成可读文件(string path)
        => Assert.Equal("PackagePathInvalid", Assert.Throws<EffectPackageException>(() => EffectPackageContents.ValidatePath(path)).Code);
    [Theory]
    [InlineData("Src/A.cs", "src/B.cs", "PackagePathCaseConflict")]
    [InlineData("A.cs", "a.cs", "PackagePathCaseConflict")]
    [InlineData("src", "src/A.cs", "PackagePathPrefixConflict")]
    public void 文件与每级目录大小写及前缀冲突被拒绝(string first, string second, string code)
        => Assert.Equal(code, Assert.Throws<EffectPackageException>(() => EffectPackageContents.ValidatePaths([first, second], [])).Code);
    [Fact]
    public void 空目录条目也不能产生大小写冲突或充当已有文件目录()
    {
        Assert.Equal("PackagePathCaseConflict", Assert.Throws<EffectPackageException>(() => EffectPackageContents.ValidatePaths(["src/A.cs"], ["Src"])).Code);
        Assert.Equal("PackagePathPrefixConflict", Assert.Throws<EffectPackageException>(() => EffectPackageContents.ValidatePaths(["src"], ["src"])).Code);
    }
    [Theory]
    [InlineData("missing", "PackageFileMissing")] [InlineData("extra", "PackageFileUndeclared")]
    [InlineData("hash", "PackageFileHashMismatch")] [InlineData("declaration", "PackageFileDuplicate")]
    [InlineData("hashShape", "PackageFileHashInvalid")]
    public void 全部文件须匹配声明与实际摘要(string failure, string code)
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        if (failure == "missing") files.Remove("preview.bin");
        if (failure == "extra") files.Add("other.bin", [1]);
        if (failure == "hash") files["preview.bin"][0] = 9;
        if (failure == "declaration") manifest["files"]!.AsArray().Add(manifest["files"]![0]!.DeepClone());
        if (failure == "hashShape") manifest["files"]![0]!["sha256"] = "ABC";
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => Read(manifest, files)).Code);
    }
    [Theory]
    [InlineData("packageVersion")] [InlineData("sdkVersion")] [InlineData("shaderProfile")]
    public void 三条版本精确拒绝而不是根据程序版本转换(string field)
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest[field] = field == "packageVersion" ? JsonValue.Create(9) : JsonValue.Create("other");
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() => Read(manifest, files)).Code);
    }
    [Theory]
    [InlineData("unknown", "PackageManifestUnknownField")] [InlineData("missing", "PackageManifestMissingField")]
    [InlineData("array", "PackageManifestType")]
    public void 清单缺失未知或错型不使用默认值(string kind, string code)
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        if (kind == "unknown") manifest["other"] = true;
        if (kind == "missing") manifest.Remove("parameters");
        if (kind == "array") manifest["files"] = "wrong";
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => Read(manifest, files)).Code);
    }
    [Fact]
    public void 嵌套重复JSON键和坏UTF8明确失败且取消不转换为包错误()
    {
        var files = EffectPackageFixture.Files(); string json = EffectPackageFixture.Manifest(files).ToJsonString();
        json = json.Replace("\"entryType\":\"PackEffect\"", "\"entryType\":\"PackEffect\",\"entryType\":\"other\"", StringComparison.Ordinal);
        Assert.Equal("PackageJsonDuplicateKey", Assert.Throws<EffectPackageException>(() => EffectPackageContents.Create(Encoding.UTF8.GetBytes(json), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile)).Code);
        files["src/PackEffect.cs"] = [0xC3, 0x28]; Assert.Equal("PackageUtf8Invalid", Assert.Throws<EffectPackageException>(() => Read(EffectPackageFixture.Manifest(files), files)).Code);
        Assert.Equal("PackageUtf8Invalid", Assert.Throws<EffectPackageException>(() => EffectPackageContents.Create(new byte[] { 0xFF }, [], EffectPackageFixture.Profile)).Code);
        using var token = new CancellationTokenSource(); token.Cancel();
        Assert.Throws<OperationCanceledException>(() => EffectPackageContents.Create(ReadOnlyMemory<byte>.Empty, [], EffectPackageFixture.Profile, cancellationToken: token.Token));
    }
}
