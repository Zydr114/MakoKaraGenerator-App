using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class V2ParticleSampleContractTests
{
    [Theory]
    [InlineData("CursorTrail")]
    [InlineData("VectorTrail")]
    [InlineData("EventBurst")]
    public async Task 三个真实包的确切文件哈希和实际DLL入口均有效(string sample)
    {
        using var loaded = await Sample.Load(sample);
        string directory = Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", sample);
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(directory, file.GetProperty("path").GetString()!));
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        Assert.IsAssignableFrom<IPureKaraokeEffect>(loaded.Effect);
        Assert.Equal(sample == "VectorTrail", loaded.Effect is IPureVisualEffect);
        foreach (long rejected in new[] { 0L, 5_000_001L })
            Assert.Throws<EffectPackageException>(() => loaded.Definition.ResolveParameters(loaded.ExportId,
                Encoding.UTF8.GetBytes("{\"lifeUs\":" + rejected + "}")));
        loaded.Definition.ResolveParameters(loaded.ExportId, "{\"lifeUs\":5000000}"u8.ToArray());
    }

    [Theory]
    [InlineData("fixed", 1, 3, new long[] { 1_000_000 })]
    [InlineData("steps", 1, 2, new long[] { 1_000_000, 1_500_000 })]
    [InlineData("linear", 0, 4, new long[] { 1_000_000, 1_414_214, 1_732_051 })]
    public async Task 真实Cursor计划的三种速率使用手算微秒阈值且末端不出生(string shape, double rate,
        double finalRate, long[] expected)
    {
        using var loaded = await Sample.Load("CursorTrail");
        var timeline = TimelineProjection.Create(Snapshot(), ReplayReason.Initial);
        var data = loaded.Declare(timeline, "{\"rateShape\":\"" + shape + "\",\"rate\":" + rate
            + ",\"finalRate\":" + finalRate + ",\"splitUs\":1000000,\"lifeUs\":1,\"trailUs\":0}");
        var compiled = Compile(loaded, timeline, data.Particles);
        // Literal values above come from 1/s, two 1s rectangles, and Q(t)=(t/1e6)^2.
        // No production integral, inverse, or random helper calculates the expected births.
        Assert.Equal(expected, compiled.ReadBirths(new(0, 2_000_000), new()).Select(item => item.BirthUs));
        var boundary = Assert.Single(compiled.ReadBirths(new(1_000_000, 1_000_000), new()));
        Assert.Equal(new TokenId("second"), Assert.IsType<ParticleSource.Cursor>(boundary.Source).Identity.TokenId);
        Assert.Equal((ulong)1, Assert.IsType<ParticleOrigin.Continuous>(boundary.Id.Origin).SequenceStartingAt1);
        Assert.Empty(compiled.ReadAlive(1_000_001, new()));
        Assert.Contains(compiled.ReadBirths(new(0, 2_000_000), new()), item => item.Id == boundary.Id);
    }

    [Fact]
    public async Task 六十每秒首次出生和死亡微秒及取消预算均保留真实包owner()
    {
        using var loaded = await Sample.Load("CursorTrail");
        var timeline = TimelineProjection.Create(Snapshot(), ReplayReason.Initial);
        var data = loaded.Declare(timeline);
        var compiled = Compile(loaded, timeline, data.Particles);
        Assert.Empty(compiled.ReadBirths(new(0, 16_666), new()));
        var first = Assert.Single(compiled.ReadBirths(new(16_667, 16_667), new()));
        Assert.Equal(616_667, first.DeathUs);
        Assert.Contains(compiled.ReadAlive(616_666, new()), item => item.Id == first.Id);
        Assert.DoesNotContain(compiled.ReadAlive(616_667, new()), item => item.Id == first.Id);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => compiled.ReadBirths(new(0, 2_000_000), new(), cancelled.Token));
        var error = Assert.Throws<EffectFailureException>(() => compiled.ReadBirths(new(0, 2_000_000), new(birthLimit: 1)));
        Assert.Equal("EmissionRecordBudgetExceeded", error.Code);
        Assert.Equal("sample", error.InstanceId.Value);
        Assert.Equal(loaded.Package.PackageId, error.PackageIdentity!.PackageId);
        Assert.Equal(119, compiled.ReadBirths(new(0, 2_000_000), new()).Length);
    }

    [Theory]
    [InlineData("token", typeof(BurstAnchor.SourceToken))]
    [InlineData("line", typeof(BurstAnchor.SourceLine))]
    [InlineData("cursor", typeof(BurstAnchor.BindingCursors))]
    public async Task 真实payload选择四锚点中的逐字行与播放头且行回退不伪造occurrence(string mode, Type anchorType)
    {
        using var loaded = await Sample.Load("EventBurst");
        var timeline = TimelineProjection.Create(Snapshot(fallback: true), ReplayReason.Initial);
        var data = loaded.Declare(timeline, "{\"anchor\":\"" + mode + "\"}");
        var rows = data.Particles.Bursts.SelectMany(plan => plan.Rows).ToArray();
        Assert.NotEmpty(rows); Assert.All(rows, row => Assert.IsType(anchorType, row.Anchor));
        var byId = timeline.Events.ToDictionary(item => item.EventId);
        Assert.All(rows, row => Assert.Contains(row.EventId, byId.Keys));
        if (mode == "token") Assert.DoesNotContain(rows, row => byId[row.EventId] is LinePerformanceStarted);
        if (mode == "line") Assert.Contains(rows, row => byId[row.EventId] is LinePerformanceStarted { ProgressSource: ProgressSource.Line });
        if (mode == "cursor")
        {
            Assert.Single(rows, row => byId[row.EventId] is LinePerformanceStarted);
            Assert.Contains(rows, row => byId[row.EventId] is TokenStarted);
            Assert.DoesNotContain(rows, row => byId[row.EventId] is LinePerformanceStarted { ProgressSource: ProgressSource.Token });
        }
    }

    [Fact]
    public async Task 标记锚点只采用真实Mark可见payload并保持无Token身份()
    {
        using var loaded = await Sample.Load("EventBurst");
        var timeline = TimelineProjection.Create(ProjectSnapshot.FromProject(LeadInFixture.Create()), ReplayReason.Initial);
        var rows = loaded.Declare(timeline, "{\"anchor\":\"mark\"}").Particles.Bursts.SelectMany(plan => plan.Rows).ToArray();
        var expected = timeline.Events.OfType<ElementVisible>().Where(item => item.Element is SceneElementIdentity.Mark).ToArray();
        Assert.NotEmpty(expected); Assert.Equal(expected.Select(item => item.EventId), rows.Select(row => row.EventId));
        for (int index = 0; index < rows.Length; index++)
            Assert.Equal(Assert.IsType<SceneElementIdentity.Mark>(expected[index].Element).NodeId,
                Assert.IsType<BurstAnchor.TextMark>(rows[index].Anchor).Id);
    }

    [Fact]
    public async Task scope原文真实occurrence与时长条件独立选择且重复装载不增加行()
    {
        using var loaded = await Sample.Load("EventBurst");
        var timeline = TimelineProjection.Create(Snapshot(), ReplayReason.Initial);
        var first = timeline.Events.OfType<TokenStarted>().Single(item => item.TokenId.Value == "first");
        var second = timeline.Events.OfType<TokenStarted>().Single(item => item.TokenId.Value == "second");
        var occurrence = loaded.Declare(timeline, "{\"scope\":\"firstOccurrence\"}").Particles.Bursts;
        Assert.Equal(first.EventId, Assert.Single(Assert.Single(occurrence).Rows).EventId);
        var ordinal = loaded.Declare(timeline, "{\"everyNth\":2,\"matchText\":\"A\"}").Particles.Bursts;
        Assert.Equal(second.EventId, Assert.Single(Assert.Single(ordinal).Rows).EventId);
        Assert.Empty(loaded.Declare(timeline, "{\"minimumDurationUs\":1000001}").Particles.Bursts.SelectMany(plan => plan.Rows));
        var duplicate = new TimelineIndex(timeline.Events.Concat([first.WithReplayReason(ReplayReason.Seek), second]));
        var repeated = loaded.Declare(timeline, "{\"everyNth\":2}", duplicate).Particles.Bursts;
        Assert.Equal(second.EventId, Assert.Single(Assert.Single(repeated).Rows).EventId);
    }

    [Fact]
    public async Task 首分支scope不会把同边界其他真实分支的payload混入()
    {
        using var loaded = await Sample.Load("EventBurst");
        var timeline = TimelineProjection.Create(Snapshot(twoBranches: true), ReplayReason.Initial);
        var project = loaded.Declare(timeline).Particles.Bursts.SelectMany(plan => plan.Rows).ToArray();
        var scoped = loaded.Declare(timeline, "{\"scope\":\"firstBranch\"}").Particles.Bursts.SelectMany(plan => plan.Rows).ToArray();
        Assert.Equal(3, project.Length); Assert.Equal(2, scoped.Length);
        var byId = timeline.Events.OfType<TokenStarted>().ToDictionary(item => item.EventId);
        Assert.All(scoped, row => Assert.Equal("primary", byId[row.EventId].BranchId.Value));
        Assert.Contains(project, row => byId[row.EventId].BranchId.Value == "other");
    }

    private static CompiledParticleSchedule Compile(Sample sample, TimelineProjection timeline, ParticleSchedule schedule)
    {
        var intervals = ImmutableArray.CreateBuilder<EmissionSourceInterval>();
        foreach (var item in timeline.Events.OfType<TokenStarted>())
        {
            var identity = new CursorIdentity(item.BranchId, item.LineId, new("sample-load"), item.Source.TimeRange!.Value,
                ProgressSource.Token, item.TokenId, item.OccurrenceId);
            intervals.Add(new(new("cursor"), new ParticleHead.Normal(new("sample-track"), identity.BindingId),
                new ParticleSource.Cursor(new("sample-track"), identity), identity.TimeRange));
        }
        var owner = new ParticleOwner(new("sample"), new(sample.Package.PackageId, sample.Package.Revision, sample.ExportId),
            sample.Artifact.ArtifactHash, 17, "sample-inputs", "sample-preparation");
        return CompiledParticleSchedule.Compile(schedule, new(intervals.ToImmutable(), []), owner);
    }

    private static ProjectSnapshot Snapshot(bool fallback = false, bool twoBranches = false)
    {
        var line = LyricLine.Create("row", "AA", new(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("first", "A", new(0, 1_000_000), TimingStatus.Edited),
                LyricToken.Create("second", "A", new(1_000_000, 2_000_000), TimingStatus.Edited)]);
        var rows = fallback ? new[] { line, LyricLine.Create("fallback", "B", new(2_000_000, 3_000_000), TimingStatus.Edited,
            [LyricToken.Create("untimed", "B")]) } : [line];
        var lyrics = LyricsDocument.Create(rows);
        if (twoBranches)
            lyrics = lyrics with { Branches = lyrics.Branches.Add(LyricBranch.Create("other", "Other", LyricBranchRole.Custom,
                [LyricLine.Create("other-row", "A", new(0, 2_000_000), TimingStatus.Edited,
                    [LyricToken.Create("other-token", "A", new(0, 2_000_000), TimingStatus.Edited)])])) };
        return ProjectSnapshot.FromProject(Project.Create("M08 sample declarations",
            Composition.Create(320, 180, Core.Time.FrameRate.Fps30, outputDurationUs: 4_000_000), lyrics));
    }

    private sealed class Sample : IDisposable
    {
        private readonly ScriptModule _module;
        private Sample(EffectPackageContents package, ScriptCompilationResult artifact, ScriptModule module, IPureKaraokeEffect effect)
        { Package = package; Definition = EffectPackageDefinition.Read(package); Artifact = artifact; _module = module; Effect = effect; }
        internal EffectPackageContents Package { get; }
        internal EffectPackageDefinition Definition { get; }
        internal ScriptCompilationResult Artifact { get; }
        internal IPureKaraokeEffect Effect { get; }
        internal string ExportId => Assert.Single(Definition.Controllers).Id;
        internal static async Task<Sample> Load(string name)
        {
            var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", name));
            var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
            var artifact = await compiler.CompileAsync(EffectPackageSources.Read(package), [name]);
            Assert.True(artifact.Succeeded, string.Join('\n', artifact.Diagnostics));
            var module = new ScriptModule(artifact.PeImage.AsMemory(), artifact.PdbImage.AsMemory());
            return new(package, artifact, module, module.CreateInstance<IPureKaraokeEffect>(name));
        }
        internal PreparedEffect Declare(TimelineProjection timeline, string overrides = "{}", TimelineIndex? index = null)
        {
            var values = Definition.ResolveParameters(ExportId, Encoding.UTF8.GetBytes(overrides));
            var parameters = new ParameterValues(values.Select(pair => KeyValuePair.Create(pair.Key, pair.Value switch
            {
                EffectParameterValue.Number item => (ParameterValue)new ParameterValue.Number(item.Value),
                EffectParameterValue.Whole item => new ParameterValue.Whole(item.Value),
                EffectParameterValue.Duration item => new ParameterValue.Duration(item.Value),
                EffectParameterValue.Toggle item => new ParameterValue.Toggle(item.Value),
                EffectParameterValue.Text item => new ParameterValue.Text(item.Value),
                EffectParameterValue.Choice item => new ParameterValue.Choice(item.Value),
                EffectParameterValue.Color item => new ParameterValue.Color(new(item.R, item.G, item.B, item.A)),
                _ => throw new InvalidOperationException("Unexpected sample parameter type."),
            })));
            var resources = new ResourceCatalog(Package.PackageId, Package.Revision, [], [], Definition.ImagePaths);
            return Effect.Declare(new(timeline.Content, timeline.Scene, parameters, AudioFeatures.NotAnalyzed,
                resources, 17, index ?? new(timeline.Events), new(320, 180), new("sample")));
        }
        public void Dispose() => _module.Dispose();
    }
}
