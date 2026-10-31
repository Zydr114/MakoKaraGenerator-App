using System.Diagnostics;
using System.Text.Json;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Application.Tests;

public sealed partial class LibraryRepositoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 两个真实进程空库先后或同时新增不能静默丢失更新(bool simultaneous)
    {
        using TemporaryDirectory temp = new(); string index = StyleTemplateLibrary.ResolvePath(temp.Path);
        using var first = Child.Start(temp, "first", index, "add", new { Id = "first", Name = "第一份" });
        using var second = Child.Start(temp, "second", index, "add", new { Id = "second", Name = "第二份" });
        await first.Ready(); await second.Ready();
        first.Go(); if (simultaneous) second.Go();
        string a = await first.Result(); if (!simultaneous) second.Go(); string b = await second.Result();
        Assert.Equal(["LibraryRevisionConflictException", "ok"], new[] { a, b }.OrderBy(x => x, StringComparer.Ordinal));
        var loaded = StyleTemplateLibrary.Load(index); Assert.Empty(loaded.Issues);
        Assert.Equal(a == "ok" ? "first" : "second", Assert.Single(loaded.Templates).Id);
    }
    [Fact]
    public async Task 两个真实进程编辑同一模板只允许原修订的一次更新()
    {
        using TemporaryDirectory temp = new(); string index = StyleTemplateLibrary.ResolvePath(temp.Path);
        StyleTemplateLibrary.Save(index, [Template("same")]);
        using var first = Child.Start(temp, "first", index, "edit", new { Id = "same", Name = "先修改" });
        using var second = Child.Start(temp, "second", index, "edit", new { Id = "same", Name = "旧作者" });
        await first.Ready(); await second.Ready(); first.Go(); Assert.Equal("ok", await first.Result());
        byte[] before = File.ReadAllBytes(index); second.Go(); Assert.Equal("LibraryRevisionConflictException", await second.Result());
        Assert.Equal(before, File.ReadAllBytes(index)); Assert.Equal("先修改", Assert.Single(StyleTemplateLibrary.Load(index).Templates).Name);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 真实删除和归档导入竞争以原修订决定整次拒绝(bool importFirst)
    {
        using TemporaryDirectory temp = new(); string index = StyleTemplateLibrary.ResolvePath(temp.Path);
        StyleTemplateLibrary.Save(index, [Template("old")]); string archive = temp.FilePath("incoming.mktpl");
        await StyleTemplateTransfer.WriteArchiveAsync(archive, [Template("incoming")]);
        using var delete = Child.Start(temp, "delete", index, "delete", new { Id = "old", Name = "删除" });
        using var import = Child.Start(temp, "import", index, "import", new { Id = "incoming", Name = "导入", Archive = archive });
        await delete.Ready(); await import.Ready();
        Child first = importFirst ? import : delete, second = importFirst ? delete : import;
        first.Go(); Assert.Equal("ok", await first.Result()); byte[] before = File.ReadAllBytes(index);
        second.Go(); Assert.Equal("LibraryRevisionConflictException", await second.Result()); Assert.Equal(before, File.ReadAllBytes(index));
        Assert.Equal(importFirst ? ["old", "incoming"] : Array.Empty<string>(), StyleTemplateLibrary.Load(index).Templates.Select(item => item.Id));
    }
    [Fact]
    public async Task 真实持锁进程等待可取消且进程退出后保留锁文件也能提交()
    {
        using TemporaryDirectory temp = new(); string index = StyleTemplateLibrary.ResolvePath(temp.Path);
        using var holder = Child.Start(temp, "holder", index, "hold", new { Id = "hold", Name = "持锁" });
        await holder.Ready(); var repo = new TemplateLibraryRepository(index);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.SaveAsync(repo.Read().Revision, [Template("cancel")], cancel.Token));
        Assert.False(File.Exists(index)); Assert.False(holder.Process.HasExited);
        holder.Process.Kill(entireProcessTree: true); await holder.Process.WaitForExitAsync();
        Assert.True(File.Exists(LibraryCommitLease.LockPath(index)));
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var saved = await repo.SaveAsync(repo.Read().Revision, [Template("after-exit")], bounded.Token);
        Assert.Equal("after-exit", Assert.Single(saved.Templates).Id);
    }
    [Fact]
    public async Task 两个真实脚本配置进程也拒绝相同空修订的第二次新增()
    {
        using TemporaryDirectory temp = new(); string index = ScriptConfigurationLibrary.ResolvePath(temp.Path);
        string package = temp.FilePath("material.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(package, MixedEffectPackageFixture.MaterialContents());
        using var first = Child.Start(temp, "first", index, "script-add", new { Id = "a", Name = "A", Package = package });
        using var second = Child.Start(temp, "second", index, "script-add", new { Id = "b", Name = "B", Package = package });
        await first.Ready(); await second.Ready(); first.Go(); second.Go();
        var results = new[] { await first.Result(), await second.Result() };
        Assert.Equal(["LibraryRevisionConflictException", "ok"], results.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Single(ScriptConfigurationLibrary.Load(index).Configurations);
    }

    private sealed class Child : IDisposable
    {
        private readonly string _ready, _go, _result;
        private readonly Task<string> _stderr;
        internal Process Process { get; }
        private Child(Process process, string ready, string go, string result)
        { Process = process; _ready = ready; _go = go; _result = result; _stderr = process.StandardError.ReadToEndAsync(); }
        internal static Child Start(TemporaryDirectory temp, string name, string index, string mode, object payload)
        {
            string ready = temp.FilePath(name + ".ready"), go = temp.FilePath(name + ".go"), result = temp.FilePath(name + ".result");
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true };
            foreach (string arg in new[] { Path.Combine(AppContext.BaseDirectory, "library-worker", "MakoKaraGenerator.Library.Worker.dll"), index, mode,
                JsonSerializer.Serialize(payload), ready, go, result }) info.ArgumentList.Add(arg);
            return new(Process.Start(info)!, ready, go, result);
        }
        internal void Go() => File.WriteAllText(_go, "go");
        internal Task Ready() => Until(_ready);
        internal async Task<string> Result()
        {
            await Until(_result); await Process.WaitForExitAsync();
            Assert.Equal(0, Process.ExitCode);
            using var document = JsonDocument.Parse(File.ReadAllText(_result)); return document.RootElement.GetProperty("status").GetString()!;
        }
        private async Task Until(string path)
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(path) && !Process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
            Assert.True(File.Exists(path), "Worker did not produce " + Path.GetFileName(path) + ": " + (Process.HasExited ? await _stderr : "deadline"));
        }
        public void Dispose()
        {
            if (!Process.HasExited) { Process.Kill(entireProcessTree: true); Process.WaitForExit(); }
            Process.Dispose(); GC.SuppressFinalize(this);
        }
    }
}
