using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SceneCatalogSdkPurityTests
{
    private static readonly Lazy<ScriptCompiler> Compiler = new(() =>
        new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));

    [Fact]
    public async Task 真实v2作者编译允许固定场景目录值与根组选择并生成可加载产物()
    {
        var result = await Compile("");

        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics));
        Assert.NotEmpty(result.PeImage);
        Assert.NotEmpty(result.PdbImage);
        Assert.NotNull(result.Descriptor);
        using ScriptModule module = new(result.PeImage.AsMemory(), result.PdbImage.AsMemory());
        Assert.IsAssignableFrom<IPureKaraokeEffect>(module.CreateInstance<IPureKaraokeEffect>("SceneProbe"));
    }

    [Theory]
    [InlineData("_ = frame.Source.Scene.Programs[0].GetHashCode();", "MKV2001")]
    [InlineData("_ = System.IO.File.ReadAllText(frame.Source.Scene.Programs[0].RootNodeId.Value);", "MKV2001")]
    [InlineData("_ = new FrameWriter([], EffectCapabilities.None);", "MKV2006")]
    public async Task 场景目录白名单不开放进程哈希外部IO或旧写者(string body, string code)
    {
        var result = await Compile(body);

        Assert.False(result.Succeeded);
        Assert.Empty(result.PeImage);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError
            && diagnostic.Code.StartsWith("CS", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Code == code
            && diagnostic.Path == "src/SceneProbe.cs" && diagnostic.Line > 0 && diagnostic.Column > 0);
    }

    private static Task<ScriptCompilationResult> Compile(string body)
    {
        string source = $$"""
            using System.Collections.Immutable;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;

            public sealed record SceneData(ImmutableArray<SourceNode> Nodes,
                ImmutableArray<SourceProgram> Programs, ImmutableArray<SourceClip> Clips,
                ImmutableArray<SourceCue> Cues) : PreparedEffect;

            public sealed class SceneProbe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) =>
                    new SceneData(source.Scene.Nodes, source.Scene.Programs, source.Scene.Clips, source.Scene.LeadIns);

                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var scene = frame.Source.Scene;
                    foreach (var program in scene.Programs)
                    {
                        _ = program.Id; _ = program.BranchId; _ = program.DefaultNodeId;
                        var root = scene.GetNode(program.RootNodeId);
                        _ = root.Id; _ = root.ParentId; _ = root.Kind; _ = root.Layout;
                        _ = root.ReferenceRatio; _ = root.BranchId; _ = root.LineId; _ = root.RubyId; _ = root.Order;
                        foreach (var group in frame.GroupGeometry)
                            if (group.Target.NodeId == program.RootNodeId)
                                output.MultiplyOpacity(new TextTarget.Group(group.Target), .75);
                    }
                    foreach (var clip in scene.Clips)
                    {
                        _ = clip.Id; _ = clip.ProgramId; _ = clip.ContainerId;
                        _ = clip.LineIds.Length; _ = clip.TimeRange;
                    }
                    foreach (var cue in scene.LeadIns)
                    {
                        _ = cue.Id; _ = cue.ProgramId; _ = cue.TargetLineId;
                        _ = cue.ContainerId; _ = cue.Enabled; _ = cue.TimeRange;
                    }
                    _ = ((SceneData)data).Programs.Length;
                    {{body}}
                }
            }
            """;
        return Compiler.Value.CompileAsync([new("src/SceneProbe.cs", source)], ["SceneProbe"]);
    }
}
