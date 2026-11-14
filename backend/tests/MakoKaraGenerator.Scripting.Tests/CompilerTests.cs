using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using Xunit;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class CompilerTests
{
    private static readonly Lazy<ScriptCompiler> Compiler = new(() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
    internal const string Source = """
        using System;
        using MakoKaraGenerator.Scripting;
        public sealed class Probe : IDisposable
        {
            private bool disposed;
            public TokenId Apply(TokenId token)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return new TokenId(token.Value + ":effect");
            }
            public void Dispose() => disposed = true;
        }
        """;

    [Fact]
    public async Task 新编译器复用真实持久产物保留警告且损坏代次拒绝重编译掩盖()
    {
        using TemporaryDirectory temporary = new();
        string references = Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
        ScriptSource[] sources = [new("src/Probe.cs", "#warning author warning\n" + Source)];
        var first = await new ScriptCompiler(references, temporary.Path).CompileAsync(sources, ["Probe"]);
        Assert.True(first.Succeeded); Assert.Contains(first.Diagnostics, value => value.Code == "CS1030" && !value.IsError);
        var second = await new ScriptCompiler(references, temporary.Path).CompileAsync(sources, ["Probe"]);
        Assert.Equal(first.ArtifactHash, second.ArtifactHash); Assert.Equal(first.Diagnostics.ToArray(), second.Diagnostics.ToArray());
        Assert.Equal(first.PeImage.ToArray(), second.PeImage.ToArray()); Assert.Equal(first.PdbImage.ToArray(), second.PdbImage.ToArray());
        string directory = Path.Combine(temporary.Path, first.ArtifactHash);
        File.WriteAllBytes(Path.Combine(directory, "effect.dll"), [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ScriptCompiler(references, temporary.Path).CompileAsync(sources, ["Probe"]));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "effect.dll")));
    }

    [Fact]
    public async Task 新源码和入口不复用旧缓存错误不发布或覆盖成功代次()
    {
        using TemporaryDirectory temporary = new();
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temporary.Path);
        var good = await compiler.CompileAsync([new("src/Probe.cs", Source)], ["Probe"]);
        var entry = await compiler.CompileAsync([new("src/Probe.cs", Source)], ["Other"]);
        Assert.NotEqual(good.ArtifactHash, entry.ArtifactHash);
        var bad = await compiler.CompileAsync([new("src/Probe.cs", "invalid !!!")], ["Probe"]);
        Assert.False(bad.Succeeded); Assert.Empty(bad.PeImage);
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, bad.ArtifactHash)));
        Assert.Equal(good.PeImage.ToArray(), ScriptArtifactStore.Load(Path.Combine(temporary.Path, good.ArtifactHash)).PeImage.ToArray());
    }

    [Fact]
    public async Task 并发提交复杂嵌套SDK类型仍生成相同DLL及PDB且不吞编译错误()
    {
        const string source = """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var value = MaterialValue.PackageExport(frame.Resources.GetMaterial("ink"),
                        new([new("amount",new ParameterValue.Number(.5))]));
                    foreach (var token in frame.Presentations.TokensInBinding) output.SetFill(token.WithRuby, TextState.Sung, value);
                }
            }
            """;
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Compiler.Value.CompileAsync([new("src/PackEffect.cs", source)])));
        Assert.All(results, result => { Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics));
            Assert.Equal(results[0].ArtifactHash, result.ArtifactHash); Assert.Equal(results[0].PeImage.ToArray(), result.PeImage.ToArray());
            Assert.Equal(results[0].PdbImage.ToArray(), result.PdbImage.ToArray()); });
        var bad = await Compiler.Value.CompileAsync([new("src/Bad.cs", "public class Bad { invalid syntax }")]);
        Assert.False(bad.Succeeded); Assert.Contains(bad.Diagnostics, d => d.IsError);
    }

    [Fact]
    public async Task 编译入口与宿主共用SDK且释放后可卸载()
    {
        ScriptCompilationResult result = await Compiler.Value.CompileAsync([new("src/Probe.cs", Source)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics));
        Assert.NotEmpty(result.PdbImage);
        WeakReference weak = InvokeAndUnload(result);
        for (int i = 0; i < 12 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference InvokeAndUnload(ScriptCompilationResult result)
    {
        using ScriptModule module = new(result.PeImage.AsMemory(), result.PdbImage.AsMemory());
        IDisposable instance = module.CreateInstance<IDisposable>("Probe");
        MethodInfo apply = instance.GetType().GetMethod("Apply")!;
        Assert.Equal(typeof(TokenId), apply.GetParameters()[0].ParameterType);
        Assert.Equal(new TokenId("字:effect"), (TokenId)apply.Invoke(instance, [new TokenId("字")])!);
        return module.UnloadReference;
    }

    [Fact]
    public async Task 新进程只使用持久DLL而不重新编译()
    {
        string root = Path.Combine(Path.GetTempPath(), "makokara-r1-" + Guid.NewGuid().ToString("N"));
        try
        {
            ScriptCompilationResult result = await Compiler.Value.CompileAsync([new("src/Probe.cs", Source)]);
            string artifact = ScriptArtifactStore.Publish(root, result);
            Assert.Equal(artifact, ScriptArtifactStore.Publish(root, result));
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(typeof(CompilerTests).Assembly.Location);
            info.ArgumentList.Add("--reuse"); info.ArgumentList.Add(artifact);
            using Process process = Process.Start(info)!;
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            Assert.Contains("字:effect", output, StringComparison.Ordinal);
            string? outputRoot = Environment.GetEnvironmentVariable("MAKOKARA_SCRIPT_SPIKE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(outputRoot))
                ScriptArtifactStore.Publish(Path.Combine(outputRoot, "compiled"), result);
            File.WriteAllBytes(Path.Combine(artifact, "effect.dll"), [1, 2, 3]);
            Assert.Throws<InvalidDataException>(() => ScriptArtifactStore.Load(artifact));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task 语法错误保留文件行列且不发布产物()
    {
        ScriptCompilationResult result = await Compiler.Value.CompileAsync([new("src/Broken.cs", "public class Broken { invalid !!! }")]);
        Assert.False(result.Succeeded);
        Assert.Empty(result.PeImage);
        Assert.Contains(result.Diagnostics, d => d.IsError && d.Path == "src/Broken.cs" && d.Line == 1 && d.Column > 0);
    }

    [Fact]
    public async Task 错误目录内的完整有效缓存也不能代替当前代次()
    {
        using var temporary = new TemporaryDirectory();
        ScriptCompilationResult original = await Compiler.Value.CompileAsync([new("src/Probe.cs", Source)]);
        ScriptCompilationResult changed = await Compiler.Value.CompileAsync([new("src/Probe.cs", Source.Replace(":effect", ":other", StringComparison.Ordinal))]);
        string published = ScriptArtifactStore.Publish(temporary.Path, original);
        string wrongPath = Path.Combine(temporary.Path, changed.ArtifactHash);
        Directory.Move(published, wrongPath);
        Assert.Throws<InvalidDataException>(() => ScriptArtifactStore.Publish(temporary.Path, changed));
        Assert.Equal(original.ArtifactHash, ScriptArtifactStore.Load(wrongPath).ArtifactHash);
    }

    [Fact]
    public async Task 固定引用及源码次序生成相同DLL和指纹()
    {
        ScriptSource a = new("src/A.cs", "public static class A { public const int Value = 3; }");
        ScriptSource b = new("src/B.cs", "public static class B { public static int Value => A.Value; }");
        ScriptCompilationResult first = await Compiler.Value.CompileAsync([a, b]);
        ScriptCompilationResult second = await Compiler.Value.CompileAsync([b, a]);
        ScriptCompilationResult changed = await Compiler.Value.CompileAsync([a with { Text = a.Text.Replace("3", "4", StringComparison.Ordinal) }, b]);
        Assert.True(first.Succeeded);
        Assert.Equal(first.ArtifactHash, second.ArtifactHash);
        Assert.True(first.PeImage.AsSpan().SequenceEqual(second.PeImage.AsSpan()));
        Assert.True(first.PdbImage.AsSpan().SequenceEqual(second.PdbImage.AsSpan()));
        Assert.NotEqual(first.ArtifactHash, changed.ArtifactHash);
    }

    [Fact]
    public async Task 取消不会被吞为编译失败()
    {
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Compiler.Value.CompileAsync([new("src/Probe.cs", Source)], cancel.Token));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("/root.cs")]
    [InlineData("src\\File.cs")]
    public void 非法源码路径拒绝(string path)
        => Assert.Throws<ArgumentException>(() => { _ = Compiler.Value.CompileAsync([new(path, Source)]); });
}

internal static class ArtifactReuseProcess
{
    public static int Main(string[] args)
    {
        if (args is ["--compile-cache", var root, var label])
        {
            try
            {
                var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), root);
                File.WriteAllText(Path.Combine(root, label + ".ready"), "ready");
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, ".start")))
                {
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Compiler barrier timed out.");
                    Thread.Sleep(10);
                }
                var artifact = compiler.CompileAsync([new("src/Probe.cs", CompilerTests.Source)], ["Probe"]).GetAwaiter().GetResult();
                Console.WriteLine($"{compiler.CompilationCount}|{artifact.ArtifactHash}|{Environment.ProcessId}"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is ["--crash-staging", var stagingRoot, var stagingName])
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(stagingRoot, ".locks"));
                using var gate = new FileStream(Path.Combine(stagingRoot, ".locks", "cache"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                string directory = Path.Combine(stagingRoot, stagingName); Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "effect.dll"), [1, 2, 3]);
                Console.WriteLine("staged"); Console.Out.Flush(); _ = Console.ReadLine(); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is ["--hold-artifact", var cache, var hash])
        {
            try
            {
                using var lease = ScriptArtifactStore.AcquireLease(cache, hash);
                Console.WriteLine("leased"); Console.Out.Flush(); _ = Console.ReadLine(); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is not ["--reuse", var path]) return 2;
        try
        {
            ScriptCompilationResult result = ScriptArtifactStore.Load(path);
            using ScriptModule module = new(result.PeImage.AsMemory(), result.PdbImage.AsMemory());
            IDisposable instance = module.CreateInstance<IDisposable>("Probe");
            TokenId token = (TokenId)instance.GetType().GetMethod("Apply")!.Invoke(instance, [new TokenId("字")])!;
            Console.WriteLine(token.Value);
            return token.Value == "字:effect" ? 0 : 1;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
