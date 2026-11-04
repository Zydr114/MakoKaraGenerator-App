using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class V2ParticleBoundaryTests
{
    private static readonly string[] ProjectBindingKinds = ["project"];

    [Theory]
    [InlineData("token")]
    [InlineData("line")]
    [InlineData("cursor")]
    [InlineData("mark")]
    public async Task 四类Burst锚点经过完整文字合成并在真实渲染中出现(string mode)
    {
        var burst = await LoadPackage("EventBurst");
        var initial = mode == "mark" ? LeadInFixture.Create() : BoundaryProject();
        string parent = initial.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var text = OffsetPackage(parent);
        var snapshot = ProjectSnapshot.FromProject(initial with { Effects = new([burst, text],
            [Instance(burst, "burst", "{\"anchor\":\"" + mode + "\",\"count\":1,\"sizePx\":16,\"lifeUs\":5000000}"),
                Instance(text, "text-a"), Instance(text, "text-b")]) });
        var baseline = ProjectSnapshot.FromProject(initial with { Effects = new([text], [Instance(text, "text-a"), Instance(text, "text-b")]) });
        var compiled = await Compile(snapshot);
        var plainCompiled = await Compile(baseline);
        await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
        await using var plain = await Factory(baseline, plainCompiled).CreateAsync(baseline);
        long requestUs = mode == "mark" ? 9_500_000 : 2_250_000;
        var frame = await Prepare(session, snapshot, requestUs);
        var births = Assert.Single(frame.V2ParticleSnapshots, item => item.InstanceId.Value == "burst").Births.Births;
        Assert.NotEmpty(births);
        var events = TimelineProjection.Create(snapshot, ReplayReason.Initial).Events.ToDictionary(item => item.EventId);
        foreach (var birth in births)
        {
            Assert.NotNull(birth.EventId);
            var evt = events[birth.EventId!.Value];
            Assert.Equal(evt.TimeUs, birth.BirthUs);
            var direct = await Prepare(plain, baseline, birth.BirthUs);
            Point2 expected = mode switch
            {
                "token" => FinalToken(direct, Assert.IsType<ParticleSource.Token>(birth.Source).Target),
                "line" => FinalLine(direct, Assert.IsType<ParticleSource.Line>(birth.Source).Target),
                "mark" => FinalMark(direct, Assert.IsType<ParticleSource.TextMark>(birth.Source).Target),
                _ => FinalCursor(direct, Assert.IsType<ParticleSource.Cursor>(birth.Source)),
            };
            Close(expected, birth.BirthPosition);
        }
        if (mode == "cursor")
        {
            var fallback = Assert.Single(births, item => item.Source is ParticleSource.Cursor cursor
                && cursor.Identity.LineId.Value == "fallback");
            var identity = Assert.IsType<ParticleSource.Cursor>(fallback.Source).Identity;
            Assert.Equal(Scripting.ProgressSource.Line, identity.ProgressSource);
            Assert.Null(identity.TokenId); Assert.Null(identity.OccurrenceId);
            Assert.IsType<LinePerformanceStarted>(events[fallback.EventId!.Value]);
            Assert.Contains(births, item => item.Source is ParticleSource.Cursor { Identity.ProgressSource: Scripting.ProgressSource.Token });
        }
        if (mode == "mark")
            Assert.All(births, birth =>
            {
                Assert.Null(birth.Source.TrackId);
                Assert.IsType<SceneElementIdentity.Mark>(Assert.IsType<ElementVisible>(events[birth.EventId!.Value]).Element);
            });
        Assert.NotEmpty(frame.OverlayBatches);
        var without = frame.WithOverlayOutput([], []);
        Assert.False(Pixels(snapshot, frame).AsSpan().SequenceEqual(Pixels(snapshot, without)));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("line")]
    [InlineData("mark")]
    [InlineData("cursor")]
    public async Task 真实事件在没有实际装载锚点时具名失败而非静默丢失出生(string mode)
    {
        var package = await ReplacePackageSource(await LoadPackage("EventBurst"), "src/EventBurst.cs", MissingAnchorSource(mode));
        var initial = mode == "mark" ? LeadInFixture.Create() : BoundaryProject();
        var snapshot = ProjectSnapshot.FromProject(initial with { Effects = new([package], [Instance(package, "missing-anchor")]) });
        var compiled = await Compile(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () =>
        {
            await using var session = await Factory(snapshot, compiled).CreateAsync(snapshot);
            await Prepare(session, snapshot, mode == "mark" ? 12_000_000 : 2_250_000);
        });
        Assert.True(error.Code == "BurstAnchorUnavailable", error.ToString());
        Assert.Equal("PrepareParticles", error.Stage);
        Assert.Equal("missing-anchor", error.InstanceId.Value);
        Assert.Equal(package.PackageId, error.PackageIdentity!.PackageId);
        Assert.Equal(package.Revision, error.PackageIdentity.Revision);
    }

    private static string MissingAnchorSource(string mode)
    {
        string selection = mode == "mark" ? """
            TimelineSelector selector = new(TimelineEventKind.ElementHidden);
            for (int index = 0; index < source.Timeline.Count(selector); index++)
                if (source.Timeline.Nth(selector, index) is ElementHidden { Element: SceneElementIdentity.Mark mark } item)
                    return Build(selector, item.EventId, new BurstAnchor.TextMark(mark.NodeId));
            return PreparedEffect.Empty;
            """ : """
            TimelineSelector selector = new(TimelineEventKind.TokenCompleted);
            var item = (TokenCompleted)source.Timeline.Nth(selector, 1)!;
            return Build(selector, item.EventId, ANCHOR);
            """.Replace("ANCHOR", mode switch
            {
                "token" => "new BurstAnchor.SourceToken(item.TokenId)",
                "line" => "new BurstAnchor.SourceLine(item.LineId)",
                _ => "new BurstAnchor.BindingCursors(CursorAnchorMode.AdvanceCenter)",
            }, StringComparison.Ordinal);
        return """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record MissingData : PreparedEffect;
            public sealed class EventBurst : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    SELECTION
                }
                private static PreparedEffect Build(TimelineSelector selector, EventId id, BurstAnchor anchor)
                    => new MissingData { Particles = new([], [new(new("token-burst"), selector,
                        [new(id, 1, anchor, new ParticleSpec { LifetimeUs = 100000, SizePx = 8,
                            Color = new(1, 0, 0, 1), SeedStreamKey = "missing" })])]) };
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """.Replace("SELECTION", selection, StringComparison.Ordinal);
    }

    private static Project BoundaryProject()
    {
        var timed = LyricLine.Create("timed", "AB", new(0, 1_000_000), TimingStatus.Edited,
            [LyricToken.Create("a", "A", new(0, 500_000), TimingStatus.Edited),
                LyricToken.Create("b", "B", new(500_000, 1_000_000), TimingStatus.Edited)]);
        var fallback = LyricLine.Create("fallback", "C", new(2_000_000, 3_000_000), TimingStatus.Edited,
            [LyricToken.Create("untimed", "C")]);
        return Project.Create("M08 boundary fixtures",
            Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 4_000_000),
            LyricsDocument.Create([timed, fallback]), lyricStyle: LyricStyle.Default with
            { FontFamilyName = "DejaVu Sans", FontSizePx = 40, Stroke = null, Shadow = null, Glow = null }) with { EnterAnimation = null };
    }

    private static EffectPackageContents OffsetPackage(string parent)
    {
        string code = """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class BoundaryText : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    foreach (var token in frame.Geometry) output.AppendTransform(token.WithRuby, Affine2.Translation(11, 7));
                    foreach (var line in frame.LineGeometry) output.AppendTransform(line.Target, Affine2.Translation(13, 17));
                    foreach (var group in frame.GroupGeometry)
                        if (group.Target.NodeId.Value == "PARENT") output.AppendTransform(group.Target, Affine2.Translation(19, 23));
                }
            }
            """.Replace("PARENT", parent, StringComparison.Ordinal);
        return TextPackage(code, ["token.affine", "line.affine", "group.affine"], ["tokenFamily", "line", "group"]);
    }

    private static EffectPackageContents TextPackage(string code, string[] capabilities, string[] targets)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(code);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            packageVersion = 3, packageId = "makokara.m08.boundary-text", name = "M08 boundary text fixture", version = "2.0.0",
            sdkVersion = "2.0.0", shaderProfile = "skia-ganesh-3.119.4-v2", requires = capabilities,
            lineExit = new { durationUs = 0 }, emitters = Array.Empty<object>(), parameters = Array.Empty<object>(),
            files = new[] { new { path = "src/BoundaryText.cs", role = "csharp", sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } },
            exports = new[] { new { id = "text", kind = "controller", name = "Boundary text", entryType = "BoundaryText", parameters = Array.Empty<string>(),
                bindingKinds = ProjectBindingKinds, outputs = targets.Select(target => new { id = target,
                    kind = "VisualTransform", target, requires = Array.Empty<string>(), dependsOn = Array.Empty<string>() }).ToArray() } },
        });
        return EffectPackageContents.CreateSupported(manifest, [KeyValuePair.Create("src/BoundaryText.cs", (ReadOnlyMemory<byte>)bytes)]);
    }

    private static Task<EffectPackageContents> LoadPackage(string name) => EffectPackageStorage.ReadSupportedDirectoryAsync(
        Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", name));
    private static Task<EffectPackageContents> ReplacePackageSource(EffectPackageContents package, string path, string code)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(code);
        var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == path)!["sha256"]
            = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return Task.FromResult(EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)(file.Path == path ? bytes : file.Content.ToArray())))));
    }
    private static ProjectEffectInstance Instance(EffectPackageContents package, string id, string parameters = "{}") => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision,
        ExportId = Assert.Single(EffectPackageDefinition.Read(package).Controllers).Id, Seed = 17,
        Parameters = EffectParameterOverrides.Parse(parameters), Subscription = new("project"),
    };
    private static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot)
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler, new PackageImageValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(package => package.Artifact.Diagnostics)));
        return compiled;
    }
    private static PureFrameSourceFactory Factory(ProjectSnapshot snapshot, CompiledProjectEffects compiled)
        => new(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline());
    private static ValueTask<PreparedRenderFrame> Prepare(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long timeUs)
        => session.PrepareAsync(timeUs, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    private static Point2 FinalToken(PreparedRenderFrame frame, TokenFamilyTarget target)
        => Center(Assert.Single(frame.FinalGeometry, item => item.WithRuby == target).BaseGeometry);
    private static Point2 FinalLine(PreparedRenderFrame frame, LineTarget target)
        => Center(Assert.Single(frame.FinalLineGeometry, item => item.Target == target).BaseGeometry);
    private static Point2 FinalMark(PreparedRenderFrame frame, TextMarkTarget target)
        => Center(Assert.Single(frame.FinalMarkGeometry, item => item.Target == target).Geometry);
    private static Point2 FinalCursor(PreparedRenderFrame frame, ParticleSource.Cursor source)
        => Assert.Single(frame.Playback.GetCursors(CursorAnchorMode.AdvanceCenter), item => item.Identity == source.Identity && item.TrackId == source.Track).Position;
    private static Point2 Center(GeometrySample geometry) => geometry.BaseWorldMatrix.Map(geometry.AdvanceBounds.Center);
    private static void Close(Point2 expected, Point2 actual)
    { Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1e-7); Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1e-7); }
    private static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame frame)
    {
        using SkiaRenderPipeline pipeline = new();
        using var pixels = pipeline.Render(RenderRequest.At(snapshot, frame.TimeUs) with { TransparentBackground = true, PreparedFrame = frame });
        return pixels.PixelMemory.ToArray();
    }
}
