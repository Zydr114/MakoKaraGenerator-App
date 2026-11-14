using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>模板逐行应用时，SDK2 宿主逐帧只处理本时刻在场行，不随全曲行数线性放大。</summary>
[Collection("Skia telemetry")]
public sealed class V2PresentInstanceTests
{
    private const string Fingerprint = "v2-present-instances";

    [Fact]
    public async Task 逐行应用的纯文字实例正式帧只调用本时刻绑定范围内有目标的实例()
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "WaveWarp"));
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(package),
            new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        List<LyricLine> lines = [];
        for (int index = 0; index < 60; index++)
        {
            long start = index * 3_000_000L;
            lines.Add(LyricLine.Create($"line-{index}", "ABC", new(start, start + 2_600_000), TimingStatus.Edited,
                [.. "ABC".Select((letter, ordinal) => LyricToken.Create($"token-{index}-{ordinal}", letter.ToString(),
                    new(start + ordinal * 866_666, start + (ordinal + 1) * 866_666), TimingStatus.Edited))]));
        }
        var project = Project.Create("present instances", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 181_000_000),
            LyricsDocument.Create(lines), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 40,
                Stroke = null, Shadow = null, Glow = null });
        var snapshot = ProjectSnapshot.FromProject(project with { EnterAnimation = null }) with { EffectInputsFingerprint = Fingerprint };
        var effects = lines.Select((line, index) => compiled.CreateEffect("wave-warp", new($"warp-{index}"),
            new EffectBinding.Lines([new LineId(line.Id)]), (ulong)index)).ToImmutableArray();
        await using var session = await new PureFrameSourceFactory(effects, Fingerprint,
            static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        foreach (long time in new long[] { 1_300_000, 90_400_000, 177_500_000 })
        {
            using var scope = PerformanceTelemetry.BeginFrame("present-instances", "cpu", time, 320, 180, capture: true);
            var frame = await session.PrepareAsync(time, OutputMode.Transparent, target);
            var capture = scope.Capture() ?? throw new InvalidOperationException("本帧未采集。");
            int present = frame.HierarchyScene.Lines.Select(static line => line.Frame.Line!.Id).Distinct().Count();
            long calls = capture.Scripts.Where(static script => script.Stage == "Evaluate").Sum(static script => script.Calls);
            // 旧实现对全部 60 个实例都调用作者 Evaluate；只有在场行的实例能写文字。
            Assert.InRange(present, 1, 3);
            Assert.InRange(calls, 1, present);
            Assert.NotEmpty(frame.TextWarps);
        }
    }
}
