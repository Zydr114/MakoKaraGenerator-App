using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

internal static class PresetSnapshotFixture
{
    internal static ProjectSnapshot Create(string? analysis = null)
    {
        var source = StaticSnapshotFixture.Create();
        var program = source.LyricPresentation.FindProgram("primary")!;
        var slot = program.RootContainerId;
        var mark = source.LyricPresentation.Nodes.First(node => node.Content is TextMarkContent);
        PresetApplication App(string id, ProjectEffectBinding binding, LyricStyle style, params LyricStyleProperty[] properties)
            => new(id, binding, StyleTemplate.Create("template-" + id, "完整固定🎵 " + id, StyleTemplateKind.Line, style, properties));
        var style = source.LyricStyle;
        PresetApplication[] items =
        [
            App("project", new(ProjectEffectBindingKind.Project), style with { Opacity = .8f }, LyricStyleProperty.Opacity),
            App("branch", new(ProjectEffectBindingKind.Branch, "primary"), style with { Scale = 1.1f }, LyricStyleProperty.Scale),
            App("line", new(ProjectEffectBindingKind.Lines, ids: ["first"]), style with { UnsungColor = new(30, 220, 70, 190) }, LyricStyleProperty.UnsungColor),
            App("token", new(ProjectEffectBindingKind.Tokens, ids: ["first-a"]), style with { SungColor = new(20, 100, 240, 170) }, LyricStyleProperty.SungColor),
            App("clip", new(ProjectEffectBindingKind.Clips, ids: ["chorus-clip"]), style with { FontSizePx = 64 }, LyricStyleProperty.FontSizePx),
            App("node", new(ProjectEffectBindingKind.Node, slot, includeDescendants: true), style with { Opacity = .3f }, LyricStyleProperty.Opacity),
            App("mark", new(ProjectEffectBindingKind.Node, mark.Id), style with { Opacity = .35f }, LyricStyleProperty.Opacity),
            App("disabled-stale", new(ProjectEffectBindingKind.Lines, ids: ["missing-row"]), style with { FontSizePx = 100 }, LyricStyleProperty.FontSizePx) with { Enabled = false },
        ];
        source = source with { PresetApplications = new(items) };
        if (analysis is not null) source = source with { AudioPath = "/never-read/音频.wav",
            AudioMedia = new(PackageSnapshotContractFixture.AudioHash, 4096, "音频.wav"),
            AudioAnalysis = PackageSnapshotContractFixture.Analysis(analysis) };
        return source;
    }
}
