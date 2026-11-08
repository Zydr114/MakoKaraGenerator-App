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

public sealed class V2LongTimelineParticleTests
{
    private const string Fingerprint = "v2-long-timeline-particles";

    [Fact]
    public async Task 默认CursorTrail逐行应用到三分钟六十行时可准备远端帧且锚点补算只调用在场实例()
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "CursorTrail"));
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
        var project = Project.Create("long particles", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 181_000_000),
            LyricsDocument.Create(lines), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 40,
                Stroke = null, Shadow = null, Glow = null });
        var snapshot = ProjectSnapshot.FromProject(project with { EnterAnimation = null }) with { EffectInputsFingerprint = Fingerprint };
        // 模板逐行应用：每行一个实例，60 个实例共享同一宿主准备预算。
        var effects = lines.Select((line, index) => compiled.CreateEffect("cursor-trail", new($"emitter-{index}"),
            new EffectBinding.Lines([new LineId(line.Id)]), (ulong)index)).ToImmutableArray();
        await using var session = await new TimelineFrameSourceFactory(effects, Fingerprint,
            static () => new SkiaRenderPipeline(), ReplayReason.Initial).CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        int supplemented = 0;
        foreach (long time in new long[] { 500_000, 180_000_000, 1_000_000, 180_000_000 })
        {
            var frame = await session.PrepareAsync(time, OutputMode.Transparent, target);
            Assert.NotEmpty(frame.V2Particles);
            // 锚点补算只调用该时刻绑定范围内有目标的实例；远处行的实例不能写文字，不随总行数线性计费。
            var anchors = frame.V2AnchorStatistics;
            Assert.True(anchors.TextCalls <= anchors.UniqueTimes * 3, $"t={time} times={anchors.UniqueTimes} textCalls={anchors.TextCalls}");
            supplemented += anchors.UniqueTimes;
        }
        Assert.True(supplemented > 0);
    }
}
