using System.Collections.Immutable;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>共享源采样与最终文字几何产生的播放头，不读取 UI 播放设备。</summary>
public static class PlaybackProjection
{
    /// <summary>返回全部真实 Current 装载；后端未提供行几何时明确不可用。</summary>
    public static Fx.PlaybackFrame Create(ProjectSnapshot snapshot, SceneFrame scene,
        ImmutableArray<Fx.TokenGeometrySample> finalGeometry)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(scene);
        if (finalGeometry.IsDefault) throw new ArgumentException("最终几何不能是 default。", nameof(finalGeometry));
        var lines = scene.LineGeometries.ToDictionary(static item => item.Target);
        if (scene.Lines.Any(sample => !lines.ContainsKey(new(new(sample.Frame.Line!.Id), new(sample.BindingId!)))))
            return Fx.PlaybackFrame.Unavailable;
        var tokens = finalGeometry.ToDictionary(static item => item.WithRuby);
        List<Fx.CursorGeometry> cursors = [];
        foreach (var source in Sources(snapshot, scene))
        {
            Fx.TextProgressPath path; Fx.Affine2 world;
            if (source.Identity.TokenId is { } tokenId)
            {
                if (!tokens.TryGetValue(new(tokenId, source.Identity.BindingId), out var geometry) || geometry.ProgressPath is not { } tokenPath)
                    throw new PreparedFrameException("MissingCursorGeometry", "当前逐字装载缺少真实进度几何。");
                path = tokenPath; world = geometry.BaseGeometry.BaseWorldMatrix;
            }
            else
            {
                var geometry = lines[new(source.Identity.LineId, source.Identity.BindingId)];
                path = geometry.ProgressPath; world = geometry.BaseGeometry.BaseWorldMatrix;
            }
            cursors.Add(new() { TrackId = source.Track, Identity = source.Identity, TimeUs = scene.TimeUs,
                Progress = source.Progress, Path = path, WorldMatrix = world });
        }
        return new([.. cursors]);
    }

    /// <summary>与正式播放头共用的真实 Current 源／装载集合；不整形、不执行作者或推测几何。</summary>
    public static ImmutableArray<(Fx.CursorTrackId TrackId, Fx.CursorIdentity Identity)> GetSources(ProjectSnapshot snapshot, SceneFrame scene)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(scene);
        return [.. Sources(snapshot, scene).Select(static item => (item.Track, item.Identity))];
    }
    private static IEnumerable<(Fx.CursorTrackId Track, Fx.CursorIdentity Identity, double Progress)> Sources(ProjectSnapshot snapshot, SceneFrame scene)
    {
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        foreach (var branch in snapshot.Lyrics.Branches)
        {
            Fx.BranchId branchId = new(branch.Id);
            var track = Fx.StableIdentities.CursorTrack(branchId, new(tree.FindProgram(branch.Id)!.Id));
            foreach (var line in branch.Lines)
            {
                var performance = LyricSampler.SampleLine(line, scene.TimeUs);
                foreach (var visible in scene.Lines.Where(sample => sample.Branch.Id == branch.Id && sample.Frame.Line!.Id == line.Id)
                    .OrderBy(static sample => sample.BindingId, StringComparer.Ordinal))
                {
                    Fx.LineId lineId = new(line.Id); Fx.BindingId bindingId = new(visible.BindingId!);
                    if (!line.Tokens.Any(static token => token.Time is not null))
                    {
                        if (line.Time is not { IsValid: true } range || !range.Contains(scene.TimeUs)) continue;
                        yield return (track, new(branchId, lineId, bindingId, new(range.StartUs, range.EndUs), Fx.ProgressSource.Line), performance.LineProgress);
                        continue;
                    }
                    if (!performance.TokensMatchLineText) continue;
                    foreach (var sampled in performance.Tokens.Where(static item => item.IsCurrent && item.Source == ProgressSource.FromToken))
                    {
                        var source = line.Tokens[sampled.Index]; Fx.TokenId tokenId = new(source.Id);
                        var range = new Fx.TimeRange(source.Time!.Value.StartUs, source.Time.Value.EndUs);
                        yield return (track, new(branchId, lineId, bindingId, range, Fx.ProgressSource.Token, tokenId,
                            Fx.StableIdentities.Occurrence(branchId, lineId, tokenId, range)), sampled.Progress);
                    }
                }
            }
        }
    }
}
