using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SvgSdkPurityTests
{
    private static readonly Lazy<ScriptCompiler> Compiler = new(() =>
        new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));

    [Fact]
    public async Task 真实v2编译接受SVG句柄纯值和资源类型声明并生成可加载产物()
    {
        var result = await Compile("_ = value.Kind;");

        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics));
        Assert.NotEmpty(result.PeImage); Assert.NotEmpty(result.PdbImage);
        Assert.NotNull(result.Descriptor);
        Assert.Equal("SvgProbe", Assert.Single(result.Descriptor.EntryTypes));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
        using ScriptModule module = new(result.PeImage.AsMemory(), result.PdbImage.AsMemory());
        var effect = module.CreateInstance<IPureKaraokeEffect>("SvgProbe");
        ContentCatalog content = new([], [], [], []); SceneCatalog scene = new(content, [], [], [], []);
        ResourceCatalog resources = new("svg", "sha256:" + new string('a', 64), [], [], [], ["star-vector"],
            ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("star-atlas", ["small"]));
        DeclareContext source = new(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, resources,
            42, TimelineIndex.Empty, new(1920, 1080), new("svg-owner"));
        var prepared = effect.Declare(source);
        Assert.Equal([VisualResourceKind.SvgVector, VisualResourceKind.SvgAtlas], prepared.VisualOutputs.Select(output => output.Resource));
        Assert.Same(resources.GetVector("star-vector"), prepared.GetType().GetProperty("Vector")!.GetValue(prepared));
        Assert.Same(resources.GetSprite("star-atlas", "small"), prepared.GetType().GetProperty("Sprite")!.GetValue(prepared));
    }

    [Theory]
    [InlineData("_ = System.IO.File.ReadAllText(\"outside.svg\");", "MKV2001")]
    [InlineData("_ = new System.IO.MemoryStream();", "MKV2001")]
    [InlineData("_ = System.Xml.XmlReader.Create(\"outside.svg\");", "MKV2006")]
    [InlineData("_ = new System.Uri(\"https://invalid.example/svg\");", "MKV2006")]
    [InlineData("_ = System.Runtime.InteropServices.Marshal.AllocHGlobal(1);", "MKV2001")]
    public async Task 支持SVG不能让纯脚本绕过IO解析器URI和native调用禁令(string body, string code)
    {
        var result = await Compile(body);

        Assert.False(result.Succeeded); Assert.Empty(result.PeImage); Assert.Empty(result.PdbImage);
        Assert.Null(result.Descriptor);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Code.StartsWith("CS", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Code == code
            && diagnostic.Path == "src/SvgProbe.cs" && diagnostic.Line > 0 && diagnostic.Column > 0);
    }

    private static Task<ScriptCompilationResult> Compile(string body)
    {
        string source = $$"""
            using System.Collections.Immutable;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;

            public sealed record SvgData(VectorHandle Vector, SpriteHandle Sprite,
                ImmutableArray<VectorVisual> Paths, ImmutableArray<AtlasSprite> Atlas, VisualResourceKind Kind) : PreparedEffect;

            public sealed class SvgProbe : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    var vector = source.Resources.GetVector("star-vector");
                    var sprite = source.Resources.GetSprite("star-atlas", "small");
                    var affine = new Affine2(-2, 1, 3, 4, 10, 20);
                    var tint = new ColorRgba(.2, .3, .4, .5);
                    return new SvgData(vector, sprite, [new VectorVisual(vector, affine, tint)],
                        [new AtlasSprite(sprite, affine, tint)], VisualResourceKind.SvgAtlas)
                    {
                        VisualOutputs =
                        [new(new("paths"), VisualPrimitiveKind.Paths, OverlayLayer.AboveText, VisualResourceKind.SvgVector),
                         new(new("atlas"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText, VisualResourceKind.SvgAtlas)]
                    };
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var value = (SvgData)data;
                    _ = value.Vector.Identity.ExportId;
                    _ = value.Sprite.Identity.Revision;
                    _ = value.Sprite.VariantId;
                    {{body}}
                }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    var value = (SvgData)data;
                    output.DrawPaths(new("paths"), value.Paths, OverlayLayer.AboveText);
                    output.DrawAtlasSprites(new("atlas"), value.Atlas, OverlayLayer.AboveText);
                }
            }
            """;
        return Compiler.Value.CompileAsync([new("src/SvgProbe.cs", source)], ["SvgProbe"]);
    }
}
