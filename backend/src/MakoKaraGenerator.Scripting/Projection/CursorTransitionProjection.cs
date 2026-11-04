using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

// 与 Current 共用源／装载投影；不整形，不创建另一份显示计划或作者状态。
internal static class CursorTransitionProjection
{
    private sealed record Segment(SourceLine Line, TimeRange Range, TokenId? Token);
    public static void Add(ProjectSnapshot snapshot, ContentCatalog content, ReplayReason reason,
        List<TimelineEvent> events, CancellationToken cancellationToken)
    {
        Dictionary<long, (CursorTrackId TrackId, CursorIdentity Identity)[]> sources = [];
        foreach (var branch in content.Branches)
        {
            List<Segment[]> previousGroup = [], group = [];
            foreach (var line in content.Lines.Where(line => line.BranchId == branch.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tokens = line.TokenIds.Select(content.GetToken).Where(static token => token.TimeRange is not null).ToArray();
                Segment[] segments = tokens.Length == 0
                    ? line.TimeRange is { } range ? [new(line, range, null)] : []
                    : [.. tokens.Select(token => new Segment(line, token.TimeRange!.Value, token.Id))];
                if (segments.Length == 0) { FlushGroup(); previousGroup = []; continue; }
                if (group.Count != 0 && group[0][0].Range.StartUs != segments[0].Range.StartUs) FlushGroup();
                group.Add(segments);
                for (int index = 1; index < segments.Length; index++) Connect(segments[index - 1], segments[index]);
            }
            FlushGroup();
            void FlushGroup()
            {
                foreach (var before in previousGroup) foreach (var after in group) Connect(before[^1], after[0]);
                previousGroup = group; group = [];
            }
        }
        void Connect(Segment from, Segment to)
        {
            if (to.Range.StartUs < from.Range.StartUs) return; // 源顺序不授予逆时间接续。
            var before = At(from.Range.EndUs - 1).Where(item => Matches(item.Identity, from));
            var after = At(to.Range.StartUs).Where(item => Matches(item.Identity, to)).ToArray();
            foreach (var left in before) foreach (var right in after)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (left.TrackId != right.TrackId || from.Line.Id == to.Line.Id && left.Identity.BindingId != right.Identity.BindingId) continue;
                var order = new EventSourceOrder(content.GetBranch(to.Line.BranchId).SourceOrder, to.Line.SourceOrder,
                    to.Token is { } token ? content.GetToken(token).SourceOrder : -1);
                events.Add(new CursorTransitionEvent(left.TrackId, left.Identity, right.Identity, to.Range.StartUs,
                    from.Line.Id == to.Line.Id ? CursorTransitionReason.TokenChange : CursorTransitionReason.LineChange, order, reason));
            }
        }
        (CursorTrackId TrackId, CursorIdentity Identity)[] At(long timeUs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(timeUs, out var result))
                sources.Add(timeUs, result = [.. PlaybackProjection.GetSources(snapshot, LyricSceneSampler.SampleFrame(snapshot, timeUs, cancellationToken))]);
            return result;
        }
    }
    private static bool Matches(CursorIdentity identity, Segment segment)
        => identity.LineId == segment.Line.Id && identity.TokenId == segment.Token && identity.TimeRange == segment.Range;
}
