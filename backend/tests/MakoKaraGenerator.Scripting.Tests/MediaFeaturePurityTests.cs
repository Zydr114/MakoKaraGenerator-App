using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class MediaFeaturePurityTests
{
    private static readonly Lazy<ScriptCompiler> Compiler = new(() =>
        new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));

    [Fact]
    public async Task 真实作者编译接受目录安全值及TextVisual采样并生成可加载产物()
    {
        const string source = """
            using System.Collections.Immutable;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record Data(MediaQueryCatalog Catalog, MediaQueryId Query,
                ImmutableArray<MediaQueryProfile> Profiles, MediaFeatureInputInfo? Input,
                MediaFeatureSample? Prior, SourceMediaFeatureValues? SourceValues,
                CanvasMediaFeatureValues? CanvasValues, LinearPremulMean Mean) : PreparedEffect;
            public sealed class MediaProbe : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    var catalog = source.Media;
                    return new Data(catalog, catalog.GetId("contrast"), catalog.Profiles,
                        catalog.Input, null, null, null, new LinearPremulMean(0, 0, 0, 0));
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var media = frame.Media;
                    var sample = Read(media, ((Data)data).Query, new Rect2(.5, 0, .5000000000000001, 1), frame.TimeUs);
                    _ = sample.Input.State; _ = sample.Input.InputRevision; _ = sample.Input.MappingKey;
                    _ = sample.Input.ProfileDigest; _ = sample.Input.SourceKey; _ = sample.Input.ArtifactHash;
                    _ = sample.Input.DiagnosticCode; _ = sample.Input.NoMediaReason;
                    _ = sample.Query.WindowUs; _ = sample.Query.Space; _ = sample.Rect; _ = sample.ProjectTimeUs;
                    if (sample.SourceValues is { } source)
                    {
                        _ = source.Mean.R; _ = source.Mean.G; _ = source.Mean.B; _ = source.Mean.Alpha;
                        _ = source.MeanPremulY; _ = source.MeanSquaredPremulY;
                        _ = source.MeanAlphaTimesPremulY; _ = source.MeanSquaredAlpha; _ = source.PremulYVariance;
                    }
                }
                private static MediaFeatureSample Read(MediaFeatures media, MediaQueryId query, Rect2 rect, long time)
                    => media.Sample(query, rect, time);
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    var sample = frame.Media.Sample(((Data)data).Query, new Rect2(0, 0, 1, 1), frame.TimeUs);
                    if (sample.CanvasValues is { } canvas)
                    {
                        _ = canvas.Mean; _ = canvas.MeanPremulY; _ = canvas.MeanSquaredPremulY;
                        _ = canvas.PremulYVariance; _ = canvas.IsOpaque;
                    }
                }
            }
            """;
        ScriptCompilationResult result = await Compile(source);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics));
        Assert.NotEmpty(result.PeImage); Assert.NotEmpty(result.PdbImage);
        using ScriptModule module = new(result.PeImage.AsMemory(), result.PdbImage.AsMemory());
        Assert.IsAssignableFrom<IPureKaraokeEffect>(module.CreateInstance<IPureKaraokeEffect>("MediaProbe"));
    }

    [Theory]
    [InlineData("private MediaFeatures? _saved;", "_saved = frame.Media;", "MKV2005")]
    [InlineData("private MediaFeatures? Saved { get; set; }", "Saved = frame.Media;", "MKV2005")]
    [InlineData("private static MediaFeatures? Saved;", "Saved = frame.Media;", "MKV2005")]
    [InlineData("", "var values = new[] { frame.Media };", "MKV2005")]
    [InlineData("", "var pair = (frame.Media, 1); var values = new List<object>(); values.Add(pair);", "MKV2005")]
    [InlineData("", "_ = ImmutableArray.Create(frame.Media);", "MKV2005")]
    [InlineData("private ImmutableArray<ImmutableArray<MediaFeatures>> _saved;", "_ = _saved.Length;", "MKV2005")]
    [InlineData("private MediaFeatures[][]? _saved;", "_ = _saved;", "MKV2005")]
    [InlineData("", "var media = frame.Media; Func<MediaQueryCatalog> later = () => media.Catalog;", "MKV2005")]
    [InlineData("", "Func<MediaQueryId, Rect2, long, MediaFeatureSample> later = frame.Media.Sample;", "MKV2005")]
    [InlineData("private static MediaFeatures Echo(MediaFeatures value) => value;", "_ = Echo(frame.Media);", "MKV2005")]
    [InlineData("private object? _saved; private void Save(object value) { _saved = value; }", "Save(frame.Media);", "MKV2005")]
    [InlineData("private static object Echo(object value) => value;", "_ = Echo(frame.Media);", "MKV2005")]
    [InlineData("private object? _saved;", "object? value = frame.TimeUs == 0 ? (object)frame.Media : null; _saved = value;", "MKV2005")]
    [InlineData("private object? _saved;", "object? value = frame.TimeUs switch { 0 => (object)frame.Media, _ => null }; _saved = value;", "MKV2005")]
    [InlineData("", "MediaFeatures media = default!; _ = media;", "MKV2001")]
    public async Task 实际编译拒绝服务字段包装闭包返回和objecthelper逃逸(string members, string body, string code)
    {
        string source = Probe(members, body);
        AssertRejected(await Compile(source), code, "src/MediaProbe.cs");
    }

    [Theory]
    [InlineData("MediaFeatures")]
    [InlineData("ImmutableArray<MediaFeatures>")]
    [InlineData("ImmutableArray<ImmutableArray<MediaFeatures>>")]
    [InlineData("MediaFeatures[]")]
    public async Task 递归只读准备结果拒绝所有服务包装(string type)
    {
        string source = $$"""
            using System.Collections.Immutable;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record InvalidData({{type}} Value) : PreparedEffect;
            public sealed class MediaProbe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        AssertRejected(await Compile(source), "MKV2004", "src/MediaProbe.cs");
    }

    [Fact]
    public async Task 多文件objecthelper不能剥掉阶段句柄身份()
    {
        string source = Probe("", "Sink.Save(frame.Media);");
        const string helper = """
            public static class Sink
            {
                private static object? Saved;
                public static void Save(object value) { Saved = value; }
            }
            """;
        var result = await Compiler.Value.CompileAsync([new("src/MediaProbe.cs", source), new("src/Sink.cs", helper)], ["MediaProbe"]);
        AssertRejected(result, "MKV2005", "src/Sink.cs");
    }

    [Theory]
    [InlineData("new MediaFeatures(null!, default)")]
    [InlineData("new MediaQueryCatalog(null!, [])")]
    [InlineData("new MediaFeatureInputInfo(MediaInputState.NoMedia, \"x\", \"x\", \"x\")")]
    public async Task 非friend作者不能构造受信目录输入或阶段服务(string expression)
    {
        var result = await Compile(Probe("", "_ = " + expression + ";"));
        Assert.False(result.Succeeded); Assert.Empty(result.PeImage);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError
            && diagnostic.Code.StartsWith("CS", StringComparison.Ordinal));
    }

    private static string Probe(string members, string body) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed class MediaProbe : IPureKaraokeEffect
        {
            {{members}}
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { {{body}} }
        }
        """;
    private static Task<ScriptCompilationResult> Compile(string source)
        => Compiler.Value.CompileAsync([new("src/MediaProbe.cs", source)], ["MediaProbe"]);
    private static void AssertRejected(ScriptCompilationResult result, string code, string path)
    {
        Assert.False(result.Succeeded); Assert.Empty(result.PeImage);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError
            && diagnostic.Code.StartsWith("CS", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Code == code
            && diagnostic.Path == path && diagnostic.Line > 0 && diagnostic.Column > 0);
    }
}
