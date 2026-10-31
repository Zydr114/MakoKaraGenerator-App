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
    internal static async Task<Fixture> CreateAsync(string root, string scenario, string counterfactual)
    {
        string[] texts = ["星光落在夜色之中", "歌声穿过漫长旅途", "明天依然闪耀如初", "让我们一起向前走"];
        var lines = texts.Select((text, row) =>
        {
            long start = row * 2_000_000L;
            var tokens = text.Select((character, index) => LyricToken.Create($"r{row}t{index}", character.ToString(),
                new(start + index * 250_000L, start + (index + 1) * 250_000L), TimingStatus.Edited)).ToArray();
            return LyricLine.Create($"line-{row}", text, new(start, start + 2_000_000), TimingStatus.Edited, tokens);
        }).ToArray();
        var project = Project.Create("Performance fixture", Composition.Create(1920, 1080, FrameRate.Fps30, outputDurationUs: 8_000_000),
            LyricsDocument.Create(lines), lyricStyle: LyricStyle.Default with
            {
                FontFamilyName = "Noto Sans CJK SC", FontSizePx = 88,
                UnsungFill = new() { Color = new(230, 230, 230, 255) },
                SungFill = new() { Color = new(35, 180, 235, 255) },
            });
        bool Use(string feature) => (scenario == feature || scenario == "combined") && counterfactual != feature && counterfactual != "all";
        List<EffectPackageContents> packages = [];
        List<ProjectEffectInstance> instances = [];
        async Task<EffectPackageContents> Package(string name)
        {
            var package = await EffectPackageStorage.ReadDirectoryAsync(Path.Combine(root, "samples", "ScriptEffects", name), ProjectEffectsJson.PackageProfile);
            packages.Add(package);
            return package;
        }
        void Add(EffectPackageContents package, string name, string export) => instances.Add(new()
        {
            Id = name, Name = name, PackageId = package.PackageId, Revision = package.Revision,
            ExportId = export, Binding = new(ProjectEffectBindingKind.Project), Subscription = new("project"), Seed = 7,
        });
        if (Use("csharp")) Add(await Package("WordEmphasis"), "word-emphasis", "word-emphasis");
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
            durationUs = 8_000_000, font = "Noto Sans CJK SC", fontSizePx = 88,
            texts, lineDurationUs = 2_000_000, tokenDurationUs = 250_000, presentation = "Project.Create default", seed = 7,
            packages = packages.Select(p => new { p.PackageId, p.Revision }).ToArray(),
            instances = instances.Select(i => new { i.Id, i.ExportId, i.Seed }).ToArray(),
            snapshot.EffectInputsFingerprint,
        };
        string hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(description)));
        return new(snapshot, description, hash);
    }
}
