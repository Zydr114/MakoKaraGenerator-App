using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

public static partial class LyricSceneSampler
{
    private static readonly ConditionalWeakTable<ProjectSnapshot, LoadIdentityIndex> LoadIdentityCache = new();
    private static bool NeedsLoadIdentities(ProjectSnapshot snapshot) => snapshot.Effects.Instances.Any(instance =>
    {
        if (!instance.Enabled) return false;
        EffectPackageProfiles.RequireSupported(snapshot.Effects.GetPackage(instance.PackageId, instance.Revision).Profile);
        return true;
    });
    private static LoadIdentityIndex LoadIdentities(ProjectSnapshot snapshot, CancellationToken token)
        => LoadIdentityCache.GetValue(snapshot, value => new(value, token));

    /// <summary>取得真实装载身份对应的固定源调度键；重入后仍沿用相同源行与布局计划。</summary>
    public static string SourceBindingKey(string bindingId)
    {
        ArgumentException.ThrowIfNullOrEmpty(bindingId);
        int position = bindingId.LastIndexOf("/reload/", StringComparison.Ordinal);
        return position < 0 ? bindingId : bindingId[..position];
    }

    // Re-entering after another visible row takes over is a new load, even when its
    // source line, parent and performance occurrence are unchanged. Build once from
    // exact source boundaries; never infer load identity from previous render calls.
    private sealed class LoadIdentityIndex
    {
        private const int MaximumBoundaries = 200_000;
        private readonly ImmutableDictionary<string, ImmutableArray<long>> _entries;
        internal LoadIdentityIndex(ProjectSnapshot snapshot, CancellationToken token)
        {
            snapshot = snapshot with { LyricPresentation = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics) };
            SortedSet<long> boundaries = [0];
            void Add(long value)
            {
                token.ThrowIfCancellationRequested();
                if (value < 0) return;
                boundaries.Add(value);
                if (boundaries.Count > MaximumBoundaries)
                    throw new PreparedFrameException("PresentationIndexBudgetExceeded", "装载身份边界超过二十万个。");
            }
            foreach (var line in snapshot.Lyrics.AllLines)
                if (line.Time is { } time) { Add(time.StartUs); Add(time.EndUs); }
            foreach (var branch in snapshot.Lyrics.Branches)
            {
                var program = snapshot.LyricPresentation.FindProgram(branch.Id)!;
                foreach (var occurrence in LyricDisplayScheduleBuilder.Build(branch, program, snapshot.LyricPresentation))
                { Add(occurrence.RevealTimeUs); Add(occurrence.HideTimeUs); }
                foreach (var clip in program.Clips)
                { var range = LyricDisplaySegmentResolver.Range(branch, clip); Add(range.StartUs); Add(range.EndUs); }
            }
            Dictionary<string, List<long>> entries = new(StringComparer.Ordinal);
            HashSet<string> previous = new(StringComparer.Ordinal);
            int loads = 0;
            foreach (long boundary in boundaries)
            {
                token.ThrowIfCancellationRequested();
                var current = SampleLines(snapshot, boundary).Select(static line => line.BindingId!).ToHashSet(StringComparer.Ordinal);
                foreach (string key in current.Where(key => !previous.Contains(key)))
                {
                    if (++loads > MaximumBoundaries)
                        throw new PreparedFrameException("PresentationIndexBudgetExceeded", "真实装载身份超过二十万个。");
                    if (!entries.TryGetValue(key, out var starts)) entries.Add(key, starts = []);
                    starts.Add(boundary);
                }
                previous = current;
            }
            _entries = entries.ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal);
        }

        internal ImmutableArray<SceneLineSample> Apply(ImmutableArray<SceneLineSample> lines, long timeUs) =>
            [.. lines.Select(line =>
            {
                if (!_entries.TryGetValue(line.BindingId!, out var starts))
                    throw new PreparedFrameException("InvalidPresentationIndex", "当前源装载不属于固定边界索引。");
                int low = 0, high = starts.Length;
                while (low < high)
                { int mid = low + (high - low) / 2; if (starts[mid] <= timeUs) low = mid + 1; else high = mid; }
                if (low == 0) throw new PreparedFrameException("InvalidPresentationIndex", "源装载在首次揭示之前出现。");
                return low == 1 ? line : line with { BindingId = line.BindingId + "/reload/" + starts[low - 1].ToString(CultureInfo.InvariantCulture) };
            })];
    }
}
