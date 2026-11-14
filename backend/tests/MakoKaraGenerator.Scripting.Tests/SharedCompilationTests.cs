using System.Diagnostics;
using System.Text;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SharedCompilationTests
{
    private static readonly ConditionalWeakTable<Process, OwnedProcess> Owned = new();
    private static readonly int[] SharingViolationCodes = [11, 32, 33];
    private sealed record OwnedProcess(int Pid, long StartTicks);
    private static string References => Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
    private static ScriptSource[] Sources(string suffix = "") => [new("src/Probe.cs", CompilerTests.Source + suffix)];

    [Fact]
    public void 身份拒绝无法无损编码的源码路径和入口()
    {
        var compiler = new ScriptCompiler(References);
        Assert.Throws<EncoderFallbackException>(() => ScriptCompilationInput.Create(Sources("\n// \ud800"), ["Probe"], compiler.Identity));
        Assert.Throws<EncoderFallbackException>(() => ScriptCompilationInput.Create([new("src/\ud801.cs", CompilerTests.Source)], ["Probe"], compiler.Identity));
        Assert.Throws<EncoderFallbackException>(() => ScriptCompilationInput.Create(Sources(), ["\ud800"], compiler.Identity));
    }

    [Fact]
    public async Task 降低配额时缓存命中保护当前产物并清理其他产物()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var first = await compiler.CompileAsync(Sources("\n// quota first"), ["Probe"]);
        var second = await compiler.CompileAsync(Sources("\n// quota second"), ["Probe"]);
        string firstDirectory = Path.Combine(temporary.Path, first.ArtifactHash);
        string secondDirectory = Path.Combine(temporary.Path, second.ArtifactHash);
        long firstSize = Directory.GetFiles(firstDirectory).Sum(path => new FileInfo(path).Length);
        var constrained = new ScriptCompiler(References, temporary.Path, maximumCacheBytes: firstSize);
        Assert.Equal(first.ArtifactHash, (await constrained.CompileAsync(Sources("\n// quota first"), ["Probe"])).ArtifactHash);
        Assert.Equal(0, constrained.CompilationCount);
        Assert.True(Directory.Exists(firstDirectory)); Assert.False(Directory.Exists(secondDirectory));
        _ = await compiler.CompileAsync(Sources("\n// quota second"), ["Probe"]);
        Assert.Equal(firstDirectory, ScriptArtifactStore.Publish(temporary.Path, first, maximumCacheBytes: firstSize));
        Assert.True(Directory.Exists(firstDirectory)); Assert.False(Directory.Exists(secondDirectory));
    }

    [Fact]
    public async Task 降低配额到当前产物以下时命中与重复发布失败且保留产物()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var result = await compiler.CompileAsync(Sources("\n// quota held"), ["Probe"]);
        string directory = Path.Combine(temporary.Path, result.ArtifactHash);
        var constrained = new ScriptCompiler(References, temporary.Path, maximumCacheBytes: 1);
        await Assert.ThrowsAsync<IOException>(() => constrained.CompileAsync(Sources("\n// quota held"), ["Probe"]));
        Assert.True(Directory.Exists(directory)); Assert.Equal(0, constrained.CompilationCount);
        Assert.Throws<IOException>(() => ScriptArtifactStore.Publish(temporary.Path, result, maximumCacheBytes: 1));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task 同键并发真正只Emit一次且新编译器命中不Emit()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var calls = Enumerable.Range(0, 12).Select(_ => compiler.CompileAsync(Sources(), ["Probe"])).ToArray();
        var results = await Task.WhenAll(calls);
        Assert.All(results, result => Assert.True(result.Succeeded)); Assert.Equal(1, compiler.CompilationCount);
        Assert.All(results, result => Assert.Equal(results[0].ArtifactHash, result.ArtifactHash));
        var next = new ScriptCompiler(References, temporary.Path);
        Assert.Equal(results[0].ArtifactHash, (await next.CompileAsync(Sources(), ["Probe"])).ArtifactHash);
        Assert.Equal(0, next.CompilationCount);
    }

    [Fact]
    public async Task 无持久缓存同键并发也合并为一次真实Emit()
    {
        var compiler = new ScriptCompiler(References);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => compiler.CompileAsync(Sources("\n// no persistent cache"), ["Probe"])));
        Assert.All(results, result => Assert.True(result.Succeeded)); Assert.Equal(1, compiler.CompilationCount);
    }

    [Fact]
    public async Task 参数默认值资源种子变化不Emit而源码仅重编所属包()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var files = V2PackageTestHelper.Files();
        var manifest = V2PackageTestHelper.Manifest(files);
        EffectPackageDefinition Read() => EffectPackageDefinition.Read(EffectPackageContents.Create(
            V2PackageTestHelper.Bytes(manifest), V2PackageTestHelper.Inputs(files), EffectPackageProfiles.V2));
        var first = await CompiledEffectPackage.CompileAsync(Read(), compiler);
        var unrelated = await compiler.CompileAsync(Sources("\n// independent package"), ["Probe"]);
        Assert.Equal(2, compiler.CompilationCount);
        manifest["parameters"]![0]!["default"] = 2.5;
        var changedDefault = await CompiledEffectPackage.CompileAsync(Read(), compiler);
        _ = changedDefault.CreateEffect("scale", new("one"), new EffectBinding.Tokens([new("word")]), 10, "{\"peak\":2}"u8.ToArray());
        _ = changedDefault.CreateEffect("scale", new("two"), new EffectBinding.Tokens([new("word")]), 99, "{\"peak\":3}"u8.ToArray());
        files["preview.bin"] = [7, 8, 9]; manifest = V2PackageTestHelper.Manifest(files);
        var changedResource = await CompiledEffectPackage.CompileAsync(Read(), compiler);
        Assert.Equal(first.Artifact.ArtifactHash, changedDefault.Artifact.ArtifactHash);
        Assert.Equal(first.Artifact.ArtifactHash, changedResource.Artifact.ArtifactHash); Assert.Equal(2, compiler.CompilationCount);
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(V2PackageTestHelper.EffectSource + "\n// changed source");
        manifest = V2PackageTestHelper.Manifest(files);
        var changedSource = await CompiledEffectPackage.CompileAsync(Read(), compiler);
        Assert.NotEqual(first.Artifact.ArtifactHash, changedSource.Artifact.ArtifactHash);
        Assert.Equal(unrelated.ArtifactHash, (await compiler.CompileAsync(Sources("\n// independent package"), ["Probe"])).ArtifactHash);
        Assert.Equal(3, compiler.CompilationCount);
    }

    [Fact]
    public async Task 两个真实进程竞争同键只一方Emit后续PID零Emit()
    {
        using TemporaryDirectory temporary = new();
        var first = Start("--compile-cache", temporary.Path, "first");
        var second = Start("--compile-cache", temporary.Path, "second");
        using (first) using (second)
        {
            try
            {
                await WaitForReady(temporary.Path, "first"); await WaitForReady(temporary.Path, "second");
                File.WriteAllText(Path.Combine(temporary.Path, ".start"), "ready");
                var a = await Finish(first); var b = await Finish(second);
                Assert.Equal(1, a.Count + b.Count); Assert.Equal(a.Hash, b.Hash); Assert.NotEqual(a.Pid, b.Pid);
                using var third = Start("--compile-cache", temporary.Path, "third"); var c = await Finish(third);
                Assert.Equal(0, c.Count); Assert.Equal(a.Hash, c.Hash); Assert.NotEqual(a.Pid, c.Pid);
            }
            finally { await StopOwned(first); await StopOwned(second); }
        }
    }

    [Fact]
    public async Task 取消一个同键等待者不取消另一个且全部取消不发布()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var input = ScriptCompilationInput.Create(Sources(), ["Probe"], compiler.Identity);
        using var blocker = await ScriptArtifactStore.AcquireCompilationLockAsync(temporary.Path, input.ArtifactHash);
        using var cancel = new CancellationTokenSource();
        Task<ScriptCompilationResult> a = compiler.CompileAsync(Sources(), ["Probe"], cancel.Token);
        Task<ScriptCompilationResult> b = compiler.CompileAsync(Sources(), ["Probe"]);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.False(b.IsCompleted); blocker.Dispose(); Assert.True((await b).Succeeded); Assert.Equal(1, compiler.CompilationCount);

        var cancelled = ScriptCompilationInput.Create(Sources("\n// all cancelled"), ["Probe"], compiler.Identity);
        using var secondBlocker = await ScriptArtifactStore.AcquireCompilationLockAsync(temporary.Path, cancelled.ArtifactHash);
        using var all = new CancellationTokenSource();
        Task<ScriptCompilationResult> pending = compiler.CompileAsync(cancelled.Sources, cancelled.EntryTypes, all.Token);
        all.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, cancelled.ArtifactHash))); Assert.Equal(1, compiler.CompilationCount);
        // A fresh non-cancelled caller must not join the abandoned cancelled flight.
        Task<ScriptCompilationResult> retry = compiler.CompileAsync(cancelled.Sources, cancelled.EntryTypes);
        secondBlocker.Dispose(); Assert.True((await retry).Succeeded); Assert.Equal(2, compiler.CompilationCount);
    }

    [Fact]
    public async Task 错误编译不持久发布且重复失败重新诊断()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var a = await compiler.CompileAsync([new("src/Bad.cs", "invalid !!!")]);
        var b = await compiler.CompileAsync([new("src/Bad.cs", "invalid !!!")]);
        Assert.False(a.Succeeded); Assert.Null(a.Descriptor); Assert.Equal(a.Diagnostics.ToArray(), b.Diagnostics.ToArray());
        Assert.Equal(2, compiler.CompilationCount); Assert.False(Directory.Exists(Path.Combine(temporary.Path, a.ArtifactHash)));
    }

    [Fact]
    public async Task 在用租约保护配额淘汰并在归还后允许清理()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var first = await compiler.CompileAsync(Sources(), ["Probe"]);
        using var lease = ScriptArtifactStore.AcquireLease(temporary.Path, first.ArtifactHash);
        Assert.Throws<IOException>(() => ScriptArtifactStore.Prune(temporary.Path, 1));
        Assert.True(Directory.Exists(Path.Combine(temporary.Path, first.ArtifactHash)));
        lease.Dispose(); ScriptArtifactStore.Prune(temporary.Path, 1);
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, first.ArtifactHash)));
    }

    [Fact]
    public async Task 跨进程持有租约时清理不能删除产物()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var result = await compiler.CompileAsync(Sources(), ["Probe"]);
        using var process = Start("--hold-artifact", temporary.Path, result.ArtifactHash);
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal("leased", ready);
            Assert.Throws<IOException>(() => ScriptArtifactStore.Prune(temporary.Path, 1));
            await process.StandardInput.WriteLineAsync("release");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(0, process.ExitCode);
            ScriptArtifactStore.Prune(temporary.Path, 1); Assert.False(Directory.Exists(Path.Combine(temporary.Path, result.ArtifactHash)));
        }
        finally { await StopOwned(process); }
    }

    [Fact]
    public async Task 实际锁竞争报告已识别共享冲突而坏目录立即失败()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References);
        var input = ScriptCompilationInput.Create(Sources(), ["Probe"], compiler.Identity);
        using var held = await ScriptArtifactStore.AcquireCompilationLockAsync(temporary.Path, input.ArtifactHash);
        string path = Path.Combine(temporary.Path, ".locks", "compile-" + input.ArtifactHash[..2]);
        var error = Assert.Throws<IOException>(() => { using var conflict = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        Assert.Contains(error.HResult & 0xffff, SharingViolationCodes);
        Console.WriteLine($"Actual sharing-contention HRESULT: 0x{error.HResult:x8}");
        string invalidRoot = Path.Combine(temporary.Path, "not-a-directory"); File.WriteAllText(invalidRoot, "file");
        await Assert.ThrowsAnyAsync<IOException>(() => ScriptArtifactStore.AcquireCompilationLockAsync(invalidRoot, input.ArtifactHash));
    }

    [Fact]
    public async Task 产物预算在读前拒绝且配额不足不发布代次()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var result = await compiler.CompileAsync(Sources(), ["Probe"]); string directory = Path.Combine(temporary.Path, result.ArtifactHash);
        Assert.Throws<InvalidDataException>(() => ScriptArtifactStore.Load(directory, new() { MaximumPeBytes = result.PeImage.Length - 1 }));
        Assert.Throws<InvalidDataException>(() => ScriptArtifactStore.Load(directory, new() { MaximumManifestBytes = 1 }));
        using TemporaryDirectory small = new(); var constrained = new ScriptCompiler(References, small.Path, maximumCacheBytes: 1);
        await Assert.ThrowsAsync<IOException>(() => constrained.CompileAsync(Sources(), ["Probe"]));
        Assert.False(Directory.Exists(Path.Combine(small.Path, result.ArtifactHash)));
    }

    [Fact]
    public async Task 失败源码和淘汰代次不会无限保留锁文件()
    {
        using TemporaryDirectory temporary = new();
        // Simulate many rejected source identities without paying Roslyn cost for each stripe.
        for (int index = 0; index < 1024; index++)
        {
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("failed-source-" + index)));
            using var held = await ScriptArtifactStore.AcquireCompilationLockAsync(temporary.Path, hash);
        }
        string locks = Path.Combine(temporary.Path, ".locks");
        Assert.InRange(Directory.GetFiles(locks, "compile-*").Length, 1, 256);
        var compiler = new ScriptCompiler(References, temporary.Path);
        for (int index = 0; index < 4; index++)
        {
            var result = await compiler.CompileAsync(Sources("\n// eviction " + index), ["Probe"]);
            using (ScriptArtifactStore.AcquireLease(temporary.Path, result.ArtifactHash)) { }
            Assert.Single(Directory.GetFiles(locks, "lease-*"));
            ScriptArtifactStore.Prune(temporary.Path, 1); Assert.Empty(Directory.GetFiles(locks, "lease-*"));
        }
        var invalid = await compiler.CompileAsync([new("src/Bad.cs", "invalid !!!")]); Assert.False(invalid.Succeeded);
        Assert.InRange(Directory.GetFiles(locks).Length, 1, 257); // 256 compile stripes plus one cache gate.
        string missing = new('f', 64);
        Assert.Null(ScriptArtifactStore.TryAcquireLease(temporary.Path, missing)); Assert.Empty(Directory.GetFiles(locks, "lease-*"));
    }

    [Fact]
    public async Task 发布进程中断的部分暂存闭包在重启清理且不删除无关目录()
    {
        using TemporaryDirectory temporary = new();
        string staging = ".build-" + Guid.NewGuid().ToString("N");
        string unrelated = Path.Combine(temporary.Path, ".build-personal"); Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
        using var process = Start("--crash-staging", temporary.Path, staging);
        try
        {
            Assert.Equal("staged", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(Directory.Exists(Path.Combine(temporary.Path, staging)));
            await StopOwned(process); // Actual owned PID is killed while holding the cache gate and a partial file.
        }
        finally { await StopOwned(process); }
        ScriptArtifactStore.Prune(temporary.Path, 1);
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, staging))); Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        var compiler = new ScriptCompiler(References, temporary.Path);
        Assert.All(await Task.WhenAll(Enumerable.Range(0, 4).Select(i => compiler.CompileAsync(Sources("\n// restart " + i), ["Probe"]))), result => Assert.True(result.Succeeded));
        Assert.DoesNotContain(Directory.GetDirectories(temporary.Path, ".build-*"), path => Path.GetFileName(path) != ".build-personal");
        string malformed = Path.Combine(temporary.Path, ".build-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(malformed);
        File.WriteAllText(Path.Combine(malformed, "user.txt"), "do not delete");
        Assert.Throws<InvalidDataException>(() => ScriptArtifactStore.Prune(temporary.Path)); Assert.True(File.Exists(Path.Combine(malformed, "user.txt")));
    }

    [Fact]
    public async Task 固定消费校验部署身份源码入口字节及预算而不使用Roslyn()
    {
        using TemporaryDirectory temporary = new(); var compiler = new ScriptCompiler(References, temporary.Path);
        var result = await compiler.CompileAsync(Sources(), ["Probe"]);
        var provider = new FixedScriptArtifactProvider(compiler.Identity, [result]);
        Assert.Same(result, await provider.CompileAsync(Sources(), ["Probe"]));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.CompileAsync(Sources("\n// other"), ["Probe"]));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.CompileAsync(Sources(), ["Other"]));
        Assert.Throws<InvalidDataException>(() => new FixedScriptArtifactProvider(compiler.Identity with { EngineIdentity = "other" }, [result]));
        Assert.Throws<InvalidDataException>(() => new FixedScriptArtifactProvider(compiler.Identity, [result with { PeImage = [1, 2, 3] }]));
        Assert.Throws<InvalidDataException>(() => new FixedScriptArtifactProvider(compiler.Identity, [result], new() { MaximumPdbBytes = 1 }));
        Assert.Equal(1, compiler.CompilationCount);
    }

    private static async Task WaitForReady(string root, string label)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, label + ".ready")))
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Child compiler did not reach the barrier.");
            await Task.Delay(10);
        }
    }
    private static Process Start(string mode, string root, string value)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        info.ArgumentList.Add(typeof(CompilerTests).Assembly.Location); info.ArgumentList.Add(mode); info.ArgumentList.Add(root); info.ArgumentList.Add(value);
        var process = Process.Start(info)!;
        Owned.Add(process, new(process.Id, process.StartTime.ToUniversalTime().Ticks)); return process;
    }
    private static async Task<(int Count, string Hash, int Pid)> Finish(Process process)
    {
        Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(process.ExitCode == 0, await error);
            string[] fields = (await output).Trim().Split('|');
            return (int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture), fields[1], int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { await StopOwned(process); }
    }
    private static async Task StopOwned(Process process)
    {
        try
        {
            if (Owned.TryGetValue(process, out var identity) && !process.HasExited && process.Id == identity.Pid
                && process.StartTime.ToUniversalTime().Ticks == identity.StartTicks)
            {
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}
