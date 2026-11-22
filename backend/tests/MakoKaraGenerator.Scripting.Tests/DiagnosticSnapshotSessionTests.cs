using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class DiagnosticSnapshotSessionTests
{
    private const string Fingerprint = "diagnostic-snapshot-session";
    private const string ProbeSource = """
        using System;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;

        public sealed class PureNoOpProbe : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source)
            {
                if (source.Parameters.GetEnum("fail") == "Declare")
                    throw new InvalidOperationException("declare stopped");
                return PreparedEffect.Empty;
            }
            public void Evaluate(in TextFrame frame, PreparedEffect prepared, TextOutput output)
            {
                if (frame.Parameters.GetEnum("fail") == "Evaluate")
                    throw new InvalidOperationException("evaluate stopped");
            }
        }

        public sealed class PureVisualFailureProbe : IPureKaraokeEffect, IPureVisualEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect prepared, TextOutput output) { }
            public void EvaluateVisuals(in VisualFrame frame, PreparedEffect prepared, VisualOutput output)
            {
                if (frame.Parameters.GetEnum("fail") == "EvaluateVisuals")
                    throw new InvalidOperationException("visual stopped");
            }
        }
        """;

    private static EffectPackageDefinition Definition(string entryType)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["src/Probe.cs"] = System.Text.Encoding.UTF8.GetBytes(ProbeSource),
        };
        var manifest = V2PackageTestHelper.Manifest(files, ["token.affine", "visuals.dots"], entryType);
        manifest["parameters"] = new JsonArray(new JsonObject
        {
            ["id"] = "fail",
            ["type"] = "enum",
            ["label"] = "Failure Stage",
            ["default"] = "none",
            ["values"] = new JsonArray(
                new JsonObject { ["value"] = "none", ["label"] = "None" },
                new JsonObject { ["value"] = "Declare", ["label"] = "Declare" },
                new JsonObject { ["value"] = "Evaluate", ["label"] = "Evaluate" },
                new JsonObject { ["value"] = "EvaluateVisuals", ["label"] = "EvaluateVisuals" }),
        });
        var export = manifest["exports"]![0]!.AsObject();
        export["id"] = "probe";
        export["name"] = "probe";
        export["parameters"] = new JsonArray("fail");
        export["bindingKinds"] = new JsonArray("project");
        return EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(V2PackageTestHelper.Bytes(manifest), V2PackageTestHelper.Inputs(files)));
    }

    private static readonly Lazy<Task<CompiledEffectPackage>> PurePackage = new(async () =>
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledEffectPackage.CompileAsync(Definition("PureNoOpProbe"), compiler);
        Assert.True(compiled.Artifact.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        return compiled;
    });

    private static ProjectSnapshot Snapshot(bool lyrics = false) => ProjectSnapshot.FromProject(Project.Create("diagnostics", lyrics: lyrics
        ? LyricsDocument.Create([LyricLine.Create("line", "甲", new(1000, 50000), TimingStatus.Edited,
            [LyricToken.Create("token", "甲", new(1000, 50000), TimingStatus.Edited)])]) : LyricsDocument.Create([])))
        with { EffectInputsFingerprint = Fingerprint };

    private static async Task<PureFrameSourceFactory> Factory(string entry = "PureNoOpProbe", string fail = "none", Action<long>? beforeSample = null)
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = entry == "PureNoOpProbe"
            ? await PurePackage.Value
            : await CompiledEffectPackage.CompileAsync(Definition(entry), compiler);
        Assert.True(compiled.Artifact.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        string json = $"{{\"fail\":\"{fail}\"}}";
        return new([compiled.CreateEffect("probe", new("instance"), new EffectBinding.Project(), 0, System.Text.Encoding.UTF8.GetBytes(json))],
            Fingerprint, () => new Geometry(beforeSample));
    }

    private sealed class Geometry(Action<long>? beforeSample = null) : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        { beforeSample?.Invoke(timeUs); return LyricSceneSampler.SampleFrame(snapshot, timeUs); }
    }

    [Fact]
    public async Task 纯帧会话与诊断游标保持安全消费模式切换及释放()
    {
        var snapshot = Snapshot(lyrics: true);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await (await Factory()).CreateAsync(snapshot);
        var cursor = new RuntimeDiagnosticCursor();
        var at = await session.PrepareAsync(1000, OutputMode.Opaque, target);
        Assert.Empty(cursor.ReadNew(at.DiagnosticSnapshot));
        Assert.Empty(at.Diagnostics);
        var mode = await session.PrepareAsync(1000, OutputMode.Transparent, target);
        Assert.Empty(cursor.ReadNew(mode.DiagnosticSnapshot));
        var later = await session.PrepareAsync(33333, OutputMode.Opaque, target);
        Assert.Empty(later.Diagnostics);
        await session.DisposeAsync();
    }

    [Theory]
    [InlineData("PureNoOpProbe", "Declare", false)]
    [InlineData("PureNoOpProbe", "Evaluate", false)]
    [InlineData("PureVisualFailureProbe", "EvaluateVisuals", true)]
    public async Task 纯执行各阶段失败保留归属与诊断快照(string entry, string stage, bool expectsErrorDiagnostic)
    {
        var snapshot = Snapshot(lyrics: true);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var factory = await Factory(entry, stage);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () =>
        { await using var session = await factory.CreateAsync(snapshot); await session.PrepareAsync(33333, OutputMode.Opaque, target); });
        Assert.Equal(stage, failure.Stage);
        Assert.Equal("ScriptExecutionFailed", failure.Code);
        Assert.NotNull(failure.DiagnosticSnapshot);
        Assert.Equal(expectsErrorDiagnostic, failure.DiagnosticSnapshot.HasError);
        if (expectsErrorDiagnostic)
            Assert.Equal(failure.Diagnostic, failure.DiagnosticSnapshot.ToImmutableArray()[^1]);
    }

    [Fact]
    public async Task 取消准备后不污染会话并允许后续帧成功准备()
    {
        var snapshot = Snapshot();
        var target = RenderTarget.FromComposition(snapshot.Composition);
        using var cancel = new CancellationTokenSource();
        bool cancelled = false;
        await using var session = await (await Factory(beforeSample: time =>
        { if (time > 1_000_000 && !cancelled) { cancelled = true; cancel.Cancel(); } })).CreateAsync(snapshot);
        var initial = await session.PrepareAsync(1_000_000, OutputMode.Opaque, target);
        Assert.Empty(initial.DiagnosticSnapshot.ReadAll());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.PrepareAsync(5_000_000, OutputMode.Opaque, target, cancel.Token));
        Assert.True(cancelled);
        var after = await session.PrepareAsync(5_000_000, OutputMode.Opaque, target);
        var cursor = new RuntimeDiagnosticCursor();
        Assert.Empty(cursor.ReadNew(after.DiagnosticSnapshot));
    }

    [Fact]
    public async Task 早期宿主门禁保留完整实例归属及空错误日志()
    {
        var compiled = await PurePackage.Value;
        var first = compiled.CreateEffect("probe", new("first"), new EffectBinding.Project(), 0);
        var second = first with { Id = new("second"), EntryType = "MissingEntry" };
        var factory = new PureFrameSourceFactory([first, second], Fingerprint, static () => new Geometry());
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await factory.CreateAsync(Snapshot()));
        Assert.Equal("Declare", failure.Stage);
        Assert.Equal(new EffectInstanceId("second"), failure.InstanceId);
        Assert.NotNull(failure.DiagnosticSnapshot);
        Assert.Empty(failure.DiagnosticSnapshot.ReadAll());
        Assert.Null(failure.Diagnostic);
        Assert.False(failure.DiagnosticSnapshot.HasError);
    }
}
