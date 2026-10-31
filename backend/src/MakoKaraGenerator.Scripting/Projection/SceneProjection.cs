using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>将同一显示树与源区间投影为只读 SDK 目录，不创建第二份调度。</summary>
public static class SceneProjection
{
    /// <summary>投影全部配置，包含当前不可见片段及禁用前奏。</summary>
    public static SceneCatalog Create(ProjectSnapshot snapshot, ContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(content);
        var definition = SceneDefinitionBuilder.Build(snapshot);
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        List<SourceClip> clips = []; List<SourceCue> cues = [];
        foreach (var program in tree.Programs)
        {
            var branch = snapshot.Lyrics.Branches.Single(item => item.Id == program.BranchId);
            foreach (var clip in program.Clips)
            {
                var range = LyricDisplaySegmentResolver.Range(branch, clip);
                clips.Add(new(new(clip.Id), new(program.Id), new(clip.ContainerId), [.. clip.LineIds.Select(static id => new LineId(id))], new(range.StartUs, range.EndUs)));
            }
            foreach (var cue in program.LeadIns)
            {
                var line = branch.Lines.Single(item => item.Id == cue.TargetLineId);
                var range = LeadInTiming.Range(line, cue.DurationUs);
                cues.Add(new(new(cue.Id), new(program.Id), new(cue.TargetLineId), new(cue.IndicatorContainerId), cue.Enabled,
                    range is { } known ? new TimeRange(known.StartUs, known.EndUs) : null));
            }
        }
        return new(content, [.. definition.Elements.Select(node => new SourceNode(new(node.Id), node.ParentId is { } parent ? new(parent) : null,
            node.Kind == SceneElementKind.Group ? SceneNodeKind.Group : SceneNodeKind.Text,
            node.LocalLayout switch { SceneLocalLayout.None => NodeLayoutKind.None, SceneLocalLayout.FreeLine => NodeLayoutKind.FreeLine,
                SceneLocalLayout.SlotSequence => NodeLayoutKind.SlotSequence, SceneLocalLayout.TranslationFlow => NodeLayoutKind.TranslationFlow,
                SceneLocalLayout.Ruby => NodeLayoutKind.Ruby, _ => throw new ArgumentOutOfRangeException(nameof(snapshot)) },
            new(node.ReferenceRect.WidthRatio, node.ReferenceRect.HeightRatio),
            node.SourceBinding.BranchId is { } branch ? new(branch) : null, node.SourceBinding.LineId is { } line ? new(line) : null,
            node.SourceBinding.RubyId is { } ruby ? new(ruby) : null, node.Order))],
            [.. tree.Programs.Select(static program => new SourceProgram(new(program.Id), new(program.BranchId), new(program.RootContainerId), new(program.DefaultContainerId)))],
            [.. clips], [.. cues]);
    }
}
