using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class PreparedScriptInputTests
{
    private static string References => Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
    private static ScriptSource[] Sources => [new("src/Probe.cs", CompilerTests.Source)];

    [Fact]
    public void 流式参考身份等于编译器同快照身份()
    {
        var streaming = ScriptReferenceIdentity.Read(References);
        var fixedImages = ScriptReferenceIdentity.Read(References, out var images);
        Assert.Equal(fixedImages, streaming); Assert.Equal(streaming.Count, images.Length);
    }

    [Fact]
    public async Task 私有封包严格读取原始源码和固定产物且不再次Emit()
    {
        using TemporaryDirectory root = new(); var compiler = new ScriptCompiler(References);
        var input = ScriptCompilationInput.Create(Sources, ["Probe"], compiler.Identity);
        var result = await compiler.CompileAsync(input.Sources, input.EntryTypes);
        byte[] source = "original profile source"u8.ToArray();
        using var prepared = ScriptPreparedInputCodec.Prepare(source, root.Path, compiler.Identity, [(input, result)]);
        var read = ScriptPreparedInputCodec.Read(prepared.Envelope, ScriptCompilationIdentity.ForDeployment(References));
        Assert.Equal(source, read.Source);
        var fixedResult = await read.Compiler.CompileAsync(input.Sources, input.EntryTypes);
        Assert.Equal(result.PeImage.ToArray(), fixedResult.PeImage.ToArray()); Assert.Equal(result.PdbImage.ToArray(), fixedResult.PdbImage.ToArray());
        Assert.Equal(1, compiler.CompilationCount); Assert.True(prepared.CopiedArtifactBytes > result.PeImage.Length + result.PdbImage.Length);
        var metadata = Metadata(prepared.Envelope);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(metadata["Root"]!.GetValue<string>()));
        Assert.DoesNotContain(Convert.ToBase64String(result.PeImage.AsSpan()), Encoding.UTF8.GetString(prepared.Envelope), StringComparison.Ordinal);
        Assert.Equal(source.Length, metadata["SourceBytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task 失败源码保留准确诊断且拒绝其他路径和虚假行列()
    {
        using TemporaryDirectory root = new(); var compiler = new ScriptCompiler(References);
        ScriptSource[] sources = [new("src/Bad.cs", "invalid !!!")];
        var input = ScriptCompilationInput.Create(sources, [], compiler.Identity);
        var result = await compiler.CompileAsync(input.Sources, input.EntryTypes);
        Assert.False(result.Succeeded);
        using var prepared = ScriptPreparedInputCodec.Prepare("source"u8.ToArray(), root.Path, compiler.Identity, [(input, result)]);
        var read = ScriptPreparedInputCodec.Read(prepared.Envelope, compiler.Identity);
        var fixedResult = await read.Compiler.CompileAsync(input.Sources, input.EntryTypes);
        Assert.False(fixedResult.Succeeded); Assert.Equal(result.Diagnostics.ToArray(), fixedResult.Diagnostics.ToArray()); Assert.Null(fixedResult.Descriptor);
        var badPath = new ScriptFailedCompilation(input.ArtifactHash, compiler.Identity, input.EntryTypes,
            [new("CS1234", "error", true, "src/Other.cs", 1, 1)]);
        var provider = new FixedScriptArtifactProvider(compiler.Identity, [], failures: [badPath]);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.CompileAsync(input.Sources, input.EntryTypes));
        var badLine = badPath with { Diagnostics = [new("CS1234", "error", true, "src/Bad.cs", 2, 1)] };
        Assert.Throws<InvalidDataException>(() => badLine.ValidateSources(input));
        var crInput = ScriptCompilationInput.Create([new("src/Bad.cs", "one\rtwo")], [], compiler.Identity);
        new ScriptFailedCompilation(crInput.ArtifactHash, compiler.Identity, crInput.EntryTypes,
            [new("CS1234", "error", true, "src/Bad.cs", 2, 4)]).ValidateSources(crInput);
        Assert.Equal(1, compiler.CompilationCount);
    }

    [Fact]
    public void 四MiB原始输入独立保留且IPC不携带源码字节()
    {
        using TemporaryDirectory root = new(); var identity = ScriptCompilationIdentity.ForDeployment(References);
        byte[] source = new byte[ScriptPreparedInputCodec.MaximumSourceBytes]; Random.Shared.NextBytes(source);
        using var prepared = ScriptPreparedInputCodec.Prepare(source, root.Path, identity, []);
        Assert.True(prepared.Envelope.Length < 4096); Assert.Equal(source, ScriptPreparedInputCodec.Read(prepared.Envelope, identity).Source);
        Assert.Throws<InvalidDataException>(() => ScriptPreparedInputCodec.Prepare(new byte[source.Length + 1], root.Path, identity, []));
    }

    [Fact]
    public void 源码篡改逃逸路径重复字段和符号链接全部拒绝()
    {
        using TemporaryDirectory root = new(); var identity = ScriptCompilationIdentity.ForDeployment(References);
        using var prepared = ScriptPreparedInputCodec.Prepare("source"u8.ToArray(), root.Path, identity, []);
        JsonObject metadata = Metadata(prepared.Envelope);
        string sourcePath = Path.Combine(metadata["Root"]!.GetValue<string>(), "source.bin");
        File.WriteAllText(sourcePath, "change");
        Assert.Throws<InvalidDataException>(() => ScriptPreparedInputCodec.Read(prepared.Envelope, identity));
        File.WriteAllText(sourcePath, "source");
        metadata["SourcePath"] = "../source.bin";
        Assert.Throws<InvalidDataException>(() => ScriptPreparedInputCodec.Read(Encode(metadata), identity));
        metadata["SourcePath"] = "source.bin";
        string text = metadata.ToJsonString(); text = text.Insert(1, "\"SourcePath\":\"source.bin\",");
        Assert.Throws<InvalidDataException>(() => ScriptPreparedInputCodec.Read(Encode(text), identity));
        File.Delete(sourcePath);
        try { File.CreateSymbolicLink(sourcePath, Path.Combine(root.Path, "outside.bin")); }
        catch (IOException error) when (OperatingSystem.IsWindows() && error.HResult == unchecked((int)0x80070522)) { return; }
        File.WriteAllText(Path.Combine(root.Path, "outside.bin"), "source");
        Assert.Throws<InvalidDataException>(() => ScriptPreparedInputCodec.Read(prepared.Envelope, identity));
    }

    [Fact]
    public void 清理失败保留输入目录所有权并允许重试()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory root = new(); var identity = ScriptCompilationIdentity.ForDeployment(References);
        using var prepared = ScriptPreparedInputCodec.Prepare("source"u8.ToArray(), root.Path, identity, []);
        string inputRoot = Metadata(prepared.Envelope)["Root"]!.GetValue<string>();
        var mode = File.GetUnixFileMode(inputRoot);
        try
        {
            File.SetUnixFileMode(inputRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.ThrowsAny<UnauthorizedAccessException>(() => prepared.Dispose());
            Assert.True(Directory.Exists(inputRoot));
        }
        finally { File.SetUnixFileMode(inputRoot, mode); }
        prepared.Dispose(); Assert.False(Directory.Exists(inputRoot)); prepared.Dispose();
    }

    [Fact]
    public async Task PE与清单协同篡改仍不匹配可信封包描述()
    {
        using TemporaryDirectory root = new(); var compiler = new ScriptCompiler(References);
        var input = ScriptCompilationInput.Create(Sources, ["Probe"], compiler.Identity);
        var result = await compiler.CompileAsync(input.Sources, input.EntryTypes);
        using var prepared = ScriptPreparedInputCodec.Prepare("source"u8.ToArray(), root.Path, compiler.Identity, [(input, result)]);
        string directory = Path.Combine(Metadata(prepared.Envelope)["Root"]!.GetValue<string>(), "scripts", input.ArtifactHash);
        string pePath = Path.Combine(directory, "effect.dll"); byte[] pe = File.ReadAllBytes(pePath); pe[^1] ^= 1; File.WriteAllBytes(pePath, pe);
        string manifestPath = Path.Combine(directory, "build.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!;
        manifest["Descriptor"]!["PeHash"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pe));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        // The directory is internally self-consistent, but no longer the Host-authorized compilation.
        Assert.NotNull(ScriptArtifactStore.Load(directory));
        Assert.Throws<InvalidDataException>(() => ScriptPreparedInputCodec.Read(prepared.Envelope, compiler.Identity));
    }

    [Fact]
    public async Task 仅改成功警告仍拒绝且不信任目录自述完整诊断()
    {
        using TemporaryDirectory root = new(); var compiler = new ScriptCompiler(References);
        var input = ScriptCompilationInput.Create([new("src/Probe.cs", "#warning original warning\n" + CompilerTests.Source)], ["Probe"], compiler.Identity);
        var result = await compiler.CompileAsync(input.Sources, input.EntryTypes);
        using var prepared = ScriptPreparedInputCodec.Prepare("source"u8.ToArray(), root.Path, compiler.Identity, [(input, result)]);
        string directory = Path.Combine(Metadata(prepared.Envelope)["Root"]!.GetValue<string>(), "scripts", input.ArtifactHash);
        string path = Path.Combine(directory, "build.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(path))!;
        string originalMessage = manifest["Diagnostics"]![0]!["Message"]!.GetValue<string>();
        manifest["Diagnostics"]![0]!["Message"] = originalMessage.Replace("original warning", "replaced warning", StringComparison.Ordinal);
        File.WriteAllText(path, manifest.ToJsonString());
        Assert.Contains("replaced warning", Assert.Single(ScriptArtifactStore.Load(directory).Diagnostics).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ScriptPreparedInputCodec.Read(prepared.Envelope, compiler.Identity));
    }

    [Fact]
    public async Task 声明剩余预算不足在读取PE前拒绝()
    {
        using TemporaryDirectory root = new(); var compiler = new ScriptCompiler(References);
        var input = ScriptCompilationInput.Create(Sources, ["Probe"], compiler.Identity);
        var result = await compiler.CompileAsync(input.Sources, input.EntryTypes);
        using var prepared = ScriptPreparedInputCodec.Prepare("source"u8.ToArray(), root.Path, compiler.Identity, [(input, result)]);
        var metadata = Metadata(prepared.Envelope); metadata["CopiedArtifactBytes"] = 1;
        // Exclusive PE access proves the aggregate budget check precedes loading images.
        string pePath = Path.Combine(metadata["Root"]!.GetValue<string>(), "scripts", input.ArtifactHash, "effect.dll");
        using var locked = new FileStream(pePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var failure = Assert.Throws<InvalidDataException>(() => ScriptPreparedInputCodec.Read(Encode(metadata), compiler.Identity));
        Assert.Contains("预算", failure.Message);
    }

    [Fact]
    public void 总量超过预算在创建固定目录和拷贝任何产物以前拒绝()
    {
        using TemporaryDirectory root = new(); var identity = ScriptCompilationIdentity.ForDeployment(References);
        ImmutableArray<byte> image = ImmutableArray.Create(new byte[checked((int)ScriptArtifactBudget.Default.MaximumPeBytes)]);
        string imageHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image.AsSpan()));
        List<(ScriptCompilationInput, ScriptCompilationResult)> compilations = [];
        for (int index = 0; index < 4; index++)
        {
            var input = ScriptCompilationInput.Create([new("src/Probe.cs", "//" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))], [], identity);
            var descriptor = new ScriptArtifactDescriptor(input.ArtifactHash, identity, [], imageHash, imageHash, image.Length, image.Length);
            compilations.Add((input, new ScriptCompilationResult(input.ArtifactHash, image, image, []) { Descriptor = descriptor }));
        }
        Assert.Throws<InvalidDataException>(() => ScriptPreparedInputCodec.Prepare("source"u8.ToArray(), root.Path, identity, compilations));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    private static JsonObject Metadata(byte[] bytes) => JsonNode.Parse(bytes.AsSpan(8))!.AsObject();
    private static byte[] Encode(JsonObject metadata) => Encode(metadata.ToJsonString());
    private static byte[] Encode(string text) => [.. "MKSI1\0\0\0"u8, .. Encoding.UTF8.GetBytes(text)];
}
