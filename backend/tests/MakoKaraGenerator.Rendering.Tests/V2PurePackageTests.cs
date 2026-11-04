using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2PurePackageTests
{
    [Theory]
    [InlineData("WordEmphasis")]
    [InlineData("RepeatEmphasis")]
    public async Task 真实v2样例编译后可直接任意时刻求值且重复乱序与重建一致(string sample)
    {
        var snapshot = await Snapshot(sample);
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler);
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(package => package.Artifact.Diagnostics)));
        int compilationsBeforeEvaluation = compiler.CompilationCount;
        var measured = new CountingGeometry();
        await using var session = await compiled.CreateFactory(() => measured, ReplayReason.Initial).CreateAsync(snapshot);
        long[] times = [250_000, 750_000, 1_250_000, 180_000_000, 600_000_000, 1_250_000, 250_000, 1_250_000];
        var frames = new List<PreparedRenderFrame>();
        foreach (long time in times)
            frames.Add(await session.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal(times.Distinct(), measured.Calls);
        Assert.NotEmpty(frames[2].TokenTransforms);
        foreach (int position in new[] { 5, 7 })
        {
            Assert.Equal(frames[2].TokenTransforms.ToArray(), frames[position].TokenTransforms.ToArray());
            Assert.Equal(frames[2].TokenStyles.ToArray(), frames[position].TokenStyles.ToArray());
        }
        await using var reopened = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        var direct = await reopened.PrepareAsync(1_250_000, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(frames[2].TokenTransforms.ToArray(), direct.TokenTransforms.ToArray());
        Assert.Equal(frames[2].TokenStyles.ToArray(), direct.TokenStyles.ToArray());
        Assert.Equal(compilationsBeforeEvaluation, compiler.CompilationCount);
    }

    [Fact]
    public async Task CPU准入只拒绝实际启用v2而保留无效果与未使用包()
    {
        var snapshot = await Snapshot("WordEmphasis");
        Assert.True(ScriptBackendRequirement.RequiresVulkan(snapshot));
        Assert.Equal("PackageBackendUnsupported", Assert.Throws<EffectPackageException>(() => ScriptBackendRequirement.RequireSupported(snapshot, false)).Code);
        ScriptBackendRequirement.RequireSupported(snapshot, true);
        var disabled = snapshot with { Effects = new(snapshot.Effects.Packages,
            snapshot.Effects.Instances.Select(instance => instance with { Enabled = false })) };
        Assert.False(ScriptBackendRequirement.RequiresVulkan(disabled));
        ScriptBackendRequirement.RequireSupported(disabled, false);
        Assert.False(ScriptBackendRequirement.RequiresVulkan(snapshot with { Effects = ProjectEffects.Empty }));
    }

    [Fact]
    public async Task 真实纯求值取消后可从同一固定输入恢复且不重放()
    {
        var snapshot = await Snapshot("WordEmphasis");
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler);
        Assert.True(compiled.Succeeded);
        var geometry = new CountingGeometry();
        await using var session = await compiled.CreateFactory(() => geometry, ReplayReason.Initial).CreateAsync(snapshot);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await session.PrepareAsync(180_000_000,
            OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), cancellation.Token));
        await session.PrepareAsync(750_000, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal([750_000L], geometry.Calls);
    }

    private static async Task<ProjectSnapshot> Snapshot(string name)
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", name));
        var export = EffectPackageDefinition.Read(package).Controllers.Single();
        var rows = Enumerable.Range(0, 3).Select(index => LyricLine.Create("row-" + index, "田", new(index * 500_000, index * 500_000 + 400_000),
            TimingStatus.Edited, [LyricToken.Create("word-" + index, "田", new(index * 500_000, index * 500_000 + 400_000), TimingStatus.Edited)])
            with { RubyAnnotations = [RubyAnnotation.Create("ruby-" + index, "word-" + index, "た")] });
        var project = Project.Create("SDK v2 production", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 601_000_000),
            LyricsDocument.Create(rows), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64 });
        return ProjectSnapshot.FromProject(project with { Effects = new([package], [new() { Id = "v2", Name = "v2",
            PackageId = package.PackageId, Revision = package.Revision, ExportId = export.Id, Subscription = new("project") }]) });
    }

    private sealed class CountingGeometry : ILineGeometryProvider, IDisposable
    {
        private readonly SkiaRenderPipeline _renderer = new();
        internal List<long> Calls { get; } = [];
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        { Calls.Add(timeUs); return _renderer.GetSceneFrame(snapshot, timeUs, editingLineId); }
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => _renderer.GetLineGeometry(snapshot, lineId);
        public void Dispose() => _renderer.Dispose();
    }
}
