using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Runtime.Hosting.Authoring;

/// <summary>自有媒体路径和已接受分析，不读取当前工程。</summary>
public sealed record TemplateSampleResources(string AudioPath, string BackgroundPath, ProjectAudioAnalysis? AudioAnalysis);
/// <summary>稳定试样身份与绑定，显示文本由UI提供。</summary>
public sealed record TemplateSampleTarget(string Key, ProjectEffectBinding Binding);

public static partial class TemplateSampleScene
{
    /// <summary>固定作者试样时长，单位微秒。</summary>
    public const long DurationUs = 12_000_000;

    /// <summary>固定歌词、前奏、布局和自有媒体的试样工程。</summary>
    public static Project WorkshopScene(bool multiLine, TemplateSampleResources? resources)
    {
        static LyricLine Line(string id, string text, long start, long end, string prefix)
        {
            var tokens = text.Select((character, index) => LyricToken.Create(prefix + index, character.ToString(),
                new(start + (end - start) * index / text.Length, start + (end - start) * (index + 1) / text.Length), TimingStatus.Edited));
            return LyricLine.Create(id, text, new(start, end), TimingStatus.Edited, tokens);
        }
        var first = Line("sample", "模板预览", 0, 4_000_000, "s") with
        { RubyAnnotations = [RubyAnnotation.Create("sample-ruby", "s0", "mó")] };
        LyricLine[] lines = multiLine ? [first, Line("repeat", "喜欢喜欢", 6_000_000, 9_000_000, "r"),
            Line("last", "光在远方", 9_000_000, 11_000_000, "l")] : [first];
        var project = Project.Create("template-preview", Composition.Create(640, 360, FrameRate.Fps30,
            backgroundColor: new(24, 31, 48), outputDurationUs: DurationUs), LyricsDocument.Create(lines));
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        string root = tree.Programs[0].RootContainerId;
        tree = tree with { Programs = [tree.Programs[0] with
        { Clips = [new LyricDisplayClip { Id = "sample-clip", ContainerId = tree.Programs[0].DefaultContainerId,
            LineIds = [.. lines.Select(line => line.Id)] }] }] };
        if (multiLine)
        {
            tree = tree with { Nodes = [.. tree.Nodes,
                new SceneContainerDefinition { Id = "sample-countdown", ParentId = root,
                    Placement = new() { Position = new(.5, .23) },
                    Content = new CountdownSequenceContent { MarkNodeIds = ["sample-mark", "sample-mark2", "sample-mark3"] } },
                .. Enumerable.Range(0, 3).Select(index => new SceneContainerDefinition
                { Id = index == 0 ? "sample-mark" : "sample-mark" + (index + 1), ParentId = "sample-countdown",
                    Placement = new() { Position = new(.4 + index * .1, .5) }, Content = new TextMarkContent() })],
                Programs = [tree.Programs[0] with { LeadIns = [new LyricLeadIn { Id = "sample-cue", TargetLineId = "repeat",
                    IndicatorContainerId = "sample-countdown", DurationUs = 2_000_000 }] }] };
        }
        return project with { LyricPresentation = tree, AudioPath = resources?.AudioPath, AudioAnalysis = resources?.AudioAnalysis,
            Background = resources is null ? BackgroundSource.Color : BackgroundSource.ForImage(resources.BackgroundPath, BackgroundFit.Cover) };
    }

    /// <summary>按正式预设写权筛选真实试样对象。</summary>
    public static TemplateSampleTarget[] Targets(Project scene, StyleTemplate template)
    {
        string branch = scene.Lyrics.Branches[0].Id;
        TemplateSampleTarget[] candidates = [
            new("line", new(ProjectEffectBindingKind.Lines, ids: ["sample"])),
            new("project", new(ProjectEffectBindingKind.Project)),
            new("branch", new(ProjectEffectBindingKind.Branch, branch)),
            new("token", new(ProjectEffectBindingKind.Tokens, ids: ["s1"])),
            new("clip", new(ProjectEffectBindingKind.Clips, ids: ["sample-clip"])),
            new("group", new(ProjectEffectBindingKind.Node, scene.LyricPresentation.Programs[0].RootContainerId, includeDescendants: true)),
            new("mark", new(ProjectEffectBindingKind.Node, "sample-mark")),
        ];
        return [.. candidates.Where(target => target.Key != "mark" || scene.LyricPresentation.FindNode("sample-mark") is not null)
            .Where(target => PresetApplicationTargets.CanApply(scene, template, target.Binding, out _))];
    }

    /// <summary>正式应用投影，重应用保留试样身份及种子。</summary>
    public static Project ApplyWorkshop(Project scene, StyleTemplate template, TemplateSampleTarget target, Project? previous)
    {
        // 工坊身份与种子仅用于固定试样；不复制工程应用身份，也不在每次输入时重新抽取随机数。
        if (previous is not null) scene = scene with
        { PresetApplications = new(previous.PresetApplications.Items.Where(item => item.Target.Equals(target.Binding))) };
        var inputs = template.EffectRecipes.Where(recipe => recipe.BranchInput is not null)
            .Select(recipe => recipe.BranchInput!).Distinct().ToDictionary(name => name, _ => scene.Lyrics.Branches[0].Id);
        if (!PresetApplicationsEditor.TryApply(scene, template, [target.Binding], inputs, out Project applied, out string reason))
            throw new ArgumentException(reason);
        return applied;
    }
}
