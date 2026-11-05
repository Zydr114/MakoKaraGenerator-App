using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class MediaFeaturePurityFlowTests
{
    private static readonly Lazy<ScriptCompiler> Compiler = new(() =>
        new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));

    [Theory]
    [InlineData("if (frame is { Media: object saved }) new List<object>().Add(saved);")]
    [InlineData("new List<object>().Add(frame switch { { Media: object saved } => saved });")]
    [InlineData("if (frame is { Source: { Indexes: object saved } }) new List<object>().Add(saved);")]
    public async Task 属性模式按成员类型保留阶段句柄身份(string body)
        => AssertRejected(await Compile("", body), "MKV2005");

    [Theory]
    [InlineData("private static object Erase(object value, MediaFeatures media) { value = media; return value; }",
        "new List<object>().Add(Erase(null!, frame.Media));")]
    [InlineData("private static object Erase<T>(T value) => value!;",
        "new List<object>().Add(Erase(frame.Media));")]
    [InlineData("",
        "object? value = null; if (frame.TimeUs == 0) value = frame.Media; else value = null; new List<object?>().Add(value);")]
    [InlineData("",
        "object? value = null; new List<object?>().Add(value = frame.Media);")]
    [InlineData("private static object Erase(object value) { if (value is object saved) return saved; return new object(); }",
        "new List<object>().Add(Erase(frame.Media));")]
    [InlineData("",
        "new List<object>().Add((object)frame.Media switch { var saved => saved });")]
    [InlineData("",
        "object? value = null; _ = frame.TimeUs switch { 0 => value = frame.Media, _ => value = null }; new List<object?>().Add(value);")]
    [InlineData("",
        "object? value = null; switch (frame.TimeUs) { case 0: value = frame.Media; break; default: value = null; break; } new List<object?>().Add(value);")]
    [InlineData("",
        "object? value = frame.Media; _ = frame.TimeUs == 0 && (value = null) is null; new List<object?>().Add(value);")]
    [InlineData("",
        "object? value = frame.Media; _ = (frame.TimeUs == 0 ? (object?)\"safe\" : null) ?? (value = null); new List<object?>().Add(value);")]
    [InlineData("",
        "object? value = null; value ??= frame.Media; new List<object?>().Add(value);")]
    [InlineData("",
        "object? value = frame.Media; string? text = frame.TimeUs == 0 ? null : \"safe\"; _ = text?.Equals(value = null); new List<object?>().Add(value);")]
    [InlineData("",
        "object? value = null; var saved = new List<object?>(); for (int i = 0; i < 2; i++) { saved.Add(value); value = frame.Media; }")]
    [InlineData("",
        "object? value = null; var saved = new List<object?>(); int i = 0; while (i++ < 2) { saved.Add(value); value = frame.Media; }")]
    [InlineData("",
        "object? value = null; var saved = new List<object?>(); foreach (int i in new[] { 0, 1 }) { saved.Add(value); value = frame.Media; }")]
    [InlineData("",
        "object? value = null; try { value = frame.Media; if (frame.TimeUs == 0) throw new InvalidOperationException(); value = null; } catch (InvalidOperationException) { } new List<object?>().Add(value);")]
    [InlineData("",
        "object? value = null; try { if (frame.TimeUs == 0) throw new InvalidOperationException(); } catch (InvalidOperationException) { value = frame.Media; } catch (ArgumentException) { value = null; } new List<object?>().Add(value);")]
    public async Task 句柄不能通过赋值分支模式循环或异常路径剥掉身份(string members, string body)
        => AssertRejected(await Compile(members, body), "MKV2005");

    [Fact]
    public async Task 非结构化goto必须明确拒绝()
    {
        var result = await Compile("", "object? value = frame.Media; goto Save; value = null; Save: new List<object?>().Add(value);");
        AssertRejected(result, "MKV2007");
    }

    [Theory]
    [InlineData("", "object? value = frame.Media; value = \"safe\"; new List<object?>().Add(value);")]
    [InlineData("private static void Read(object value) { value = \"safe\"; new List<object>().Add(value); }", "Read(frame.Media);")]
    [InlineData("", "object? value = frame.Media; if (frame.TimeUs == 0) value = \"left\"; else value = \"right\"; new List<object?>().Add(value);")]
    [InlineData("", "object? value = frame.Media; switch (frame.TimeUs) { case 0: value = \"left\"; break; default: value = \"right\"; break; } new List<object?>().Add(value);")]
    [InlineData("", "object? value = null; try { value = frame.Media; } finally { value = \"safe\"; } new List<object?>().Add(value);")]
    [InlineData("", "object? value = null; try { value = frame.Media; } finally { } value = \"safe\"; new List<object?>().Add(value);")]
    [InlineData("", "static void Read(MediaFeatures media) { _ = media.Catalog; } Read(frame.Media);")]
    [InlineData("", "if (frame.Media is { Catalog: var catalog }) new List<MediaQueryCatalog>().Add(catalog);")]
    [InlineData("", "var catalog = frame.Media.Catalog; object value = catalog; if (value is object saved) new List<object>().Add(saved);")]
    public async Task 明确安全重赋值当前调用helper与纯metadata模式保留接受(string members, string body)
    {
        ScriptCompilationResult result = await Compile(members, body);
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics.Select(static diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.NotEmpty(result.PeImage);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.IsError);
    }

    [Fact]
    public async Task 真正捕获外层阶段句柄的local仍拒绝()
    {
        var result = await Compile("", "var media = frame.Media; void Read() { _ = media.Catalog; } Read();");
        AssertRejected(result, "MKV2005");
    }

    private static Task<ScriptCompilationResult> Compile(string members, string body)
    {
        string source = $$"""
            using System;
            using System.Collections.Generic;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class MediaFlowProbe : IPureKaraokeEffect
            {
                {{members}}
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { {{body}} }
            }
            """;
        return Compiler.Value.CompileAsync([new("src/MediaFlowProbe.cs", source)], ["MediaFlowProbe"]);
    }

    private static void AssertRejected(ScriptCompilationResult result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Empty(result.PeImage);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.IsError
            && diagnostic.Code.StartsWith("CS", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Code == code
            && diagnostic.Path == "src/MediaFlowProbe.cs" && diagnostic.Line > 0 && diagnostic.Column > 0);
    }
}
