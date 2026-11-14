using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

internal sealed record Fixture(ProjectSnapshot Snapshot, object Description, string Sha256);

internal static class Fixtures
{
    internal static async Task<Fixture> CreateAsync(string root, string scenario, string counterfactual, bool longTimeline, string? video)
    {
        string[] texts = ["星光落在夜色之中", "歌声穿过漫长旅途", "明天依然闪耀如初", "让我们一起向前走"];
        long[] starts = longTimeline ? [0, 180_000_000, 360_000_000] : [0];
        var lines = starts.SelectMany((offset, block) => texts.Select((text, row) =>
        {
            long start = offset + row * 2_000_000L;
            string prefix = longTimeline ? $"b{block}r{row}" : $"r{row}";
            var tokens = text.Select((character, index) => LyricToken.Create($"{prefix}t{index}", character.ToString(),
                new(start + index * 250_000L, start + (index + 1) * 250_000L), TimingStatus.Edited)).ToArray();
            var line = LyricLine.Create(longTimeline ? $"line-{block}-{row}" : $"line-{row}", text, new(start, start + 2_000_000), TimingStatus.Edited, tokens);
            return scenario == "ruby" ? line with { RubyAnnotations = [RubyAnnotation.Create($"{prefix}-ruby", tokens[0].Id, "ほし")] } : line;
        })).ToArray();
        long duration = longTimeline ? 370_000_000 : 8_000_000;
        if (scenario == "video" && (video is null || !File.Exists(video))) throw new ArgumentException("video fixture is required");
        var project = Project.Create("Performance fixture", Composition.Create(1920, 1080, FrameRate.Fps30, outputDurationUs: duration),
            LyricsDocument.Create(lines), lyricStyle: LyricStyle.Default with
            {
                FontFamilyName = "Noto Sans CJK SC", FontSizePx = 88,
                UnsungFill = new() { Color = new(230, 230, 230, 255) },
                SungFill = new() { Color = new(35, 180, 235, 255) },
            }, background: scenario == "video" ? BackgroundSource.ForVideo(Path.GetFullPath(video!), BackgroundFit.Cover) : null);
        bool Use(string feature) => (scenario == feature || scenario == "combined") && counterfactual != feature && counterfactual != "all";
        List<EffectPackageContents> packages = [];
        List<ProjectEffectInstance> instances = [];
        async Task<EffectPackageContents> Package(string name)
        {
            var package = await EffectPackageStorage.ReadDirectoryAsync(Path.Combine(root, "samples", "ScriptEffectsV2", name), EffectPackageProfiles.V2);
            packages.Add(package);
            return package;
        }
        void Add(EffectPackageContents package, string name, string export) => instances.Add(new()
        {
            Id = name, Name = name, PackageId = package.PackageId, Revision = package.Revision,
            ExportId = export, Binding = new(ProjectEffectBindingKind.Project), Subscription = new("project"), Seed = 7,
        });
        if (Use("csharp")) Add(await Package("WordEmphasis"), "word-emphasis", "word-emphasis");
        if (scenario == "multi")
        {
            var package = await Package("WordEmphasis");
            for (int index = 0; index < 3; index++) Add(package, $"word-emphasis-{index}", "word-emphasis");
        }
        if (Use("graph")) Add(await Package("LineGlitch"), "line-glitch", "line-glitch");
        if (Use("material"))
        {
            var package = await Package("CustomInk");
            var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "custom-ink", EffectParameterOverrides.Empty);
            project = project with { LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = material } } };
        }
        if (Use("particle")) Add(await Package("CursorTrail"), "cursor-trail", "cursor-trail");
        project = project with { Effects = new(packages, instances) };
        var snapshot = ProjectSnapshot.FromProject(project);
        var description = new
        {
            version = 1, scenario, counterfactual, canvasWidth = 1920, canvasHeight = 1080, fps = 30,
            durationUs = duration, font = "Noto Sans CJK SC", fontSizePx = 88,
            texts, lineDurationUs = 2_000_000, tokenDurationUs = 250_000, presentation = "Project.Create default", seed = 7,
            timelineOffsetsUs = starts, ruby = scenario == "ruby" ? "ほし on first token" : null,
            videoSha256 = video is null ? null : Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(video))),
            packages = packages.Select(p => new { p.PackageId, p.Revision }).ToArray(),
            instances = instances.Select(i => new { i.Id, i.ExportId, i.Seed }).ToArray(),
            snapshot.EffectInputsFingerprint,
        };
        string hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(description)));
        return new(snapshot, description, hash);
    }
}
