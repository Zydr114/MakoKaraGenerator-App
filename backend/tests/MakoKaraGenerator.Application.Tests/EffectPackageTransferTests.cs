using System.IO.Compression;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class EffectPackageTransferTests
{
    [Fact]
    public async Task 显式支持读取器对目录和归档分派v1v2且保留实际身份()
    {
        using TemporaryDirectory temp = new();
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion; manifest["sdkVersion"] = "2.0.7";
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile; manifest["requires"] = new JsonArray("token.affine");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        var current = EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
        string directory = temp.FilePath("v2-directory"); MakoKaraGenerator.Core.Persistence.EffectPackageStorage.Publish(directory, current);
        var restoredDirectory = await EffectPackageTransfer.ReadSupportedDirectoryAsync(directory);
        Assert.Equal(current.Revision, restoredDirectory.Revision); Assert.Equal("2.0.7", restoredDirectory.Profile.SdkVersion);
        string zip = temp.FilePath("v2.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(zip, current);
        var restoredArchive = await EffectPackageTransfer.ReadSupportedArchiveAsync(zip);
        Assert.Equal(current.Revision, restoredArchive.Revision); Assert.Equal(current.Profile, restoredArchive.Profile);
        Assert.Equal("PackageFileBudgetExceeded", (await Assert.ThrowsAsync<EffectPackageException>(() =>
            EffectPackageTransfer.ReadSupportedArchiveAsync(zip, new() { MaximumFileBytes = 1 }))).Code);
        string legacy = temp.FilePath("v1.mkfx"); var old = EffectPackageFixture.Contents(); await EffectPackageTransfer.WriteArchiveAsync(legacy, old);
        Assert.Equal(old.Revision, (await EffectPackageTransfer.ReadSupportedArchiveAsync(legacy)).Revision);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry("manifest.json")!.Delete(); manifest["requires"] = new JsonArray("future.unknown");
            using var output = archive.CreateEntry("manifest.json").Open(); await output.WriteAsync(EffectPackageFixture.Bytes(manifest));
        }
        Assert.Equal("PackageCapabilityUnsupported", (await Assert.ThrowsAsync<EffectPackageException>(() =>
            EffectPackageTransfer.ReadSupportedArchiveAsync(zip))).Code);
    }

    [Fact]
    public async Task 混合导出真实目录和归档离开源目录后保留全部语义与回退()
    {
        using TemporaryDirectory temp = new(); var contents = MixedEffectPackageFixture.Contents(fallback: MixedEffectPackageFixture.Gradient());
        string directory = temp.FilePath("mixed-source"); Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), contents.Manifest.ToArray());
        foreach (var file in contents.Files)
        {
            string path = Path.Combine(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, file.Content.ToArray());
        }
        var source = await EffectPackageTransfer.ReadDirectoryAsync(directory, EffectPackageFixture.Profile);
        string archive = temp.FilePath("mixed.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(archive, source);
        Directory.Move(directory, temp.FilePath("moved"));
        var imported = await EffectPackageTransfer.ReadArchiveAsync(archive, EffectPackageFixture.Profile);
        Assert.Equal(source.Revision, imported.Revision);
        var definition = EffectPackageDefinition.Read(imported);
        Assert.Equal(["controller", "ink", "tint"], definition.Exports.Select(export => export.Id));
        Assert.Equal(Assert.IsType<EffectMaterialFallback.Gradient>(EffectPackageDefinition.Read(source).GetMaterial("ink").Fallback).Value,
            Assert.IsType<EffectMaterialFallback.Gradient>(definition.GetMaterial("ink").Fallback).Value);
        Assert.Equal(EffectPackageGraphFixture.GraphPath, definition.GetGraph("tint").GraphPath);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task 图文件目录与分享归档保留同一程序边界和逐输入配方()
    {
        using TemporaryDirectory temp = new(); var contents = EffectPackageGraphFixture.Contents(); string directory = temp.FilePath("graph-source");
        Directory.CreateDirectory(directory); await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), contents.Manifest.ToArray());
        foreach (var file in contents.Files)
        {
            string path = Path.Combine(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, file.Content.ToArray());
        }
        var original = await EffectPackageTransfer.ReadDirectoryAsync(directory, EffectPackageFixture.Profile);
        string archive = temp.FilePath("graphs.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(archive, original);
        Directory.Move(directory, temp.FilePath("moved")); var imported = await EffectPackageTransfer.ReadArchiveAsync(archive, EffectPackageFixture.Profile);
        Assert.Equal(original.Revision, imported.Revision); var graph = EffectPackageImageGraphs.Read(imported)[EffectPackageGraphFixture.GraphPath];
        Assert.Equal(0, Assert.Single(graph.Plan.Nodes).Bounds.MaximumSampleOffsetPx); Assert.True(graph.Captures["original"].IncludeRuby);
        Assert.Equal(EffectPackageShaderFixture.ProgramPath, Assert.IsType<ImageGraphNode.RuntimeShader>(Assert.Single(graph.Plan.Nodes)).ProgramId);
    }

    [Fact]
    public async Task 独立程序目录与归档保留同一真实声明源码及完整资源修订()
    {
        using TemporaryDirectory temp = new(); var contents = EffectPackageShaderFixture.Contents();
        string directory = temp.FilePath("source"); Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), contents.Manifest.ToArray());
        foreach (var file in contents.Files)
        {
            string path = Path.Combine(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, file.Content.ToArray());
        }
        var original = await EffectPackageTransfer.ReadDirectoryAsync(directory, EffectPackageFixture.Profile);
        string archive = temp.FilePath("programs.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(archive, original);
        Directory.Move(directory, temp.FilePath("moved"));
        var imported = await EffectPackageTransfer.ReadArchiveAsync(archive, EffectPackageFixture.Profile);
        Assert.Equal(original.Revision, imported.Revision);
        var definition = EffectPackageDefinition.Read(imported);
        var program = Assert.Single(definition.ShaderPrograms);
        Assert.Equal(EffectPackageShaderFixture.ProgramPath, program.Key); Assert.Equal(EffectPackageShaderFixture.Source, program.Value.Source);
        Assert.Equal(ShaderUniformKind.Float, Assert.Single(program.Value.Uniforms).Kind);
        Assert.Equal("PackEffect", Assert.Single(definition.Controllers).EntryType);
    }

    [Theory]
    [InlineData("file", "PackageFileBudgetExceeded")]
    [InlineData("total", "PackageContentBudgetExceeded")]
    [InlineData("count", "PackageFileCountExceeded")]
    public async Task 目录与ZIP均在读取分配前执行同一展开预算(string kind, string code)
    {
        using TemporaryDirectory temp = new(); string directory = temp.FilePath("content"); EffectPackageFixture.WriteDirectory(directory);
        var content = await EffectPackageTransfer.ReadDirectoryAsync(directory, EffectPackageFixture.Profile); string zip = temp.FilePath("share.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(zip, content);
        long total = content.Manifest.Length + content.Files.Sum(file => (long)file.Content.Length);
        var budget = kind switch
        {
            "file" => new EffectPackageContentBudget { MaximumFileBytes = 1 },
            "total" => new EffectPackageContentBudget { MaximumBytes = total - 1 },
            _ => new EffectPackageContentBudget { MaximumFiles = content.Files.Length },
        };
        var directoryError = await Assert.ThrowsAsync<EffectPackageException>(() => EffectPackageTransfer.ReadDirectoryAsync(directory, EffectPackageFixture.Profile, budget: budget));
        var archiveError = await Assert.ThrowsAsync<EffectPackageException>(() => EffectPackageTransfer.ReadArchiveAsync(zip, EffectPackageFixture.Profile, budget: budget));
        Assert.Equal(code, directoryError.Code); Assert.Equal(code, archiveError.Code);
    }
    [Fact]
    public async Task 目录分享往返可离开原目录且重复ZIP字节一致()
    {
        using TemporaryDirectory temp = new(); string source = temp.FilePath("editable"); EffectPackageFixture.WriteDirectory(source);
        var fromDirectory = await EffectPackageTransfer.ReadDirectoryAsync(source, EffectPackageFixture.Profile);
        string archive = temp.FilePath("share.mkfx"), second = temp.FilePath("second.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, fromDirectory); await EffectPackageTransfer.WriteArchiveAsync(second, fromDirectory);
        Assert.Equal(await File.ReadAllBytesAsync(archive), await File.ReadAllBytesAsync(second));
        Directory.Move(source, temp.FilePath("moved")); var fromArchive = await EffectPackageTransfer.ReadArchiveAsync(archive, EffectPackageFixture.Profile);
        Assert.Equal(fromDirectory.Revision, fromArchive.Revision); Assert.True(fromDirectory.Manifest.AsSpan().SequenceEqual(fromArchive.Manifest.AsSpan()));
        foreach (var file in fromDirectory.Files) Assert.True(file.Content.AsSpan().SequenceEqual(fromArchive.Files.Single(value => value.Path == file.Path).Content.AsSpan()));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }
    [Theory]
    [InlineData("../outside", "PackagePathInvalid")]
    [InlineData("src\\bad", "PackagePathInvalid")]
    [InlineData("link", "PackageLinkRejected")]
    [InlineData("duplicate", "PackageFileDuplicate")]
    [InlineData("Src/", "PackagePathCaseConflict")]
    public async Task 实际ZIP非法条目不先解压或忽略(string kind, string code)
    {
        using TemporaryDirectory temp = new(); string path = temp.FilePath("bad.mkfx"); var contents = EffectPackageFixture.Contents();
        await EffectPackageTransfer.WriteArchiveAsync(path, contents);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            string name = kind switch { "link" => "asset-link", "duplicate" => "README.md", _ => kind };
            var entry = archive.CreateEntry(name); if (kind == "link") entry.ExternalAttributes = 0xA000 << 16;
        }
        Assert.Equal(code, (await Assert.ThrowsAsync<EffectPackageException>(() => EffectPackageTransfer.ReadArchiveAsync(path, EffectPackageFixture.Profile))).Code);
        Assert.False(File.Exists(temp.FilePath("outside")));
    }
    [Fact]
    public async Task 目录及源码链接拒绝完整性失败不补读外部资源()
    {
        using TemporaryDirectory temp = new(); string source = temp.FilePath("editable"); EffectPackageFixture.WriteDirectory(source);
        string linked = temp.FilePath("linked"); Directory.CreateSymbolicLink(linked, source);
        Assert.Equal("PackageLinkRejected", (await Assert.ThrowsAsync<EffectPackageException>(() => EffectPackageTransfer.ReadDirectoryAsync(linked, EffectPackageFixture.Profile))).Code);
        string file = Path.Combine(source, "README.md"); File.Move(file, temp.FilePath("external.md")); File.CreateSymbolicLink(file, temp.FilePath("external.md"));
        Assert.Equal("PackageLinkRejected", (await Assert.ThrowsAsync<EffectPackageException>(() => EffectPackageTransfer.ReadDirectoryAsync(source, EffectPackageFixture.Profile))).Code);
    }
    [Fact]
    public async Task 失败或取消分享保持原目标且不残留临时文件()
    {
        using TemporaryDirectory temp = new(); string path = temp.FilePath("existing.mkfx"); await File.WriteAllTextAsync(path, "original");
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EffectPackageTransfer.WriteArchiveAsync(path, EffectPackageFixture.Contents(), token.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        string blocked = temp.FilePath("directory.mkfx"); Directory.CreateDirectory(blocked);
        await Assert.ThrowsAnyAsync<IOException>(() => EffectPackageTransfer.WriteArchiveAsync(blocked, EffectPackageFixture.Contents()));
        Assert.True(Directory.Exists(blocked)); Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }
    [Fact]
    public async Task 实际写入中取消分享也保持原目标并清理半成品()
    {
        using TemporaryDirectory temp = new(); string target = temp.FilePath("existing.mkfx"); await File.WriteAllTextAsync(target, "original");
        var files = EffectPackageFixture.Files(); byte[] bytes = new byte[16 * 1024 * 1024]; new Random(42).NextBytes(bytes); files["preview.bin"] = bytes;
        var contents = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        using var cancellation = new CancellationTokenSource(); using var monitorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool observed = false;
        var monitor = Task.Run(async () =>
        {
            while (!Directory.EnumerateFiles(temp.Path, "*.tmp").Any()) await Task.Delay(1, monitorTimeout.Token);
            observed = true; cancellation.Cancel();
        });
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EffectPackageTransfer.WriteArchiveAsync(target, contents, cancellation.Token));
            await monitor; Assert.True(observed); Assert.Equal("original", await File.ReadAllTextAsync(target)); Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        }
        finally
        {
            monitorTimeout.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }
    }
    [Fact]
    public async Task 缺清单与额外文件及坏摘要给出实际路径()
    {
        using TemporaryDirectory temp = new(); string source = temp.FilePath("editable"); EffectPackageFixture.WriteDirectory(source);
        File.WriteAllText(Path.Combine(source, "README.md"), "changed");
        var error = await Assert.ThrowsAsync<EffectPackageException>(() => EffectPackageTransfer.ReadDirectoryAsync(source, EffectPackageFixture.Profile));
        Assert.Equal("README.md", error.Path); Assert.Equal("PackageFileHashMismatch", error.Code);
        File.Delete(Path.Combine(source, "manifest.json"));
        Assert.Equal("PackageManifestMissing", (await Assert.ThrowsAsync<EffectPackageException>(() => EffectPackageTransfer.ReadDirectoryAsync(source, EffectPackageFixture.Profile))).Code);
    }
    [Fact]
    public async Task 损坏归档给出包诊断而不是缺清单或空包成功()
    {
        using TemporaryDirectory temp = new(); string path = temp.FilePath("broken.mkfx"); await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        var error = await Assert.ThrowsAsync<EffectPackageException>(() => EffectPackageTransfer.ReadArchiveAsync(path, EffectPackageFixture.Profile));
        Assert.Equal("PackageArchiveInvalid", error.Code); Assert.IsType<InvalidDataException>(error.InnerException);
    }
}
