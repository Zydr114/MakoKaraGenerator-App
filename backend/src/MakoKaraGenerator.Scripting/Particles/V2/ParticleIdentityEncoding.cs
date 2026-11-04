using System.Globalization;
using System.Text;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Particles.V2;

// 编码完整字段并加长度前缀；既不截断哈希，也不使用字典枚举顺序产生身份或随机流。
internal static class ParticleIdentityEncoding
{
    internal static long HeadCharacters(ParticleHead head) => head switch
    {
        ParticleHead.Normal normal => 64L + normal.Track.Value.Length + normal.Binding.Value.Length,
        ParticleHead.Bridge bridge => 64L + SourceCharacters(bridge.Source),
        _ => throw new ArgumentException("未知粒子 head。", nameof(head)),
    };
    internal static long SourceCharacters(ParticleSource source) => source switch
    {
        ParticleSource.Cursor cursor => 128L + cursor.Track.Value.Length + CursorCharacters(cursor.Identity),
        ParticleSource.Bridge bridge => 256L + bridge.Track.Value.Length + bridge.TransitionId.Value.Length
            + CursorCharacters(bridge.From) + CursorCharacters(bridge.To),
        ParticleSource.Token token => 128L + token.Target.TokenId.Value.Length + token.Target.BindingId.Value.Length,
        ParticleSource.Line line => 128L + line.Target.LineId.Value.Length + line.Target.BindingId.Value.Length,
        ParticleSource.TextMark mark => 128L + mark.Target.NodeId.Value.Length + mark.Target.CueId.Value.Length + mark.Target.BindingId.Value.Length,
        _ => throw new ArgumentException("未知粒子来源。", nameof(source)),
    };
    internal static string Head(ParticleHead head) => head switch
    {
        ParticleHead.Normal normal => Encode("normal", normal.Track.Value, normal.Binding.Value),
        ParticleHead.Bridge bridge => Encode("bridge", Source(bridge.Source)),
        _ => throw new ArgumentException("未知粒子 head。", nameof(head)),
    };
    internal static string Source(ParticleSource source) => source switch
    {
        ParticleSource.Cursor cursor => Encode("cursor", cursor.Track.Value, Cursor(cursor.Identity)),
        ParticleSource.Bridge bridge => Encode("bridge", bridge.Track.Value, Cursor(bridge.From), Cursor(bridge.To),
            bridge.TransitionId.Value, Number(bridge.FlightRange.StartUs), Number(bridge.FlightRange.EndUs)),
        ParticleSource.Token token => Encode("token", token.Target.TokenId.Value, token.Target.BindingId.Value),
        ParticleSource.Line line => Encode("line", line.Target.LineId.Value, line.Target.BindingId.Value),
        ParticleSource.TextMark mark => Encode("mark", mark.Target.NodeId.Value, mark.Target.CueId.Value, mark.Target.BindingId.Value),
        _ => throw new ArgumentException("未知粒子来源。", nameof(source)),
    };
    internal static string Stream(ParticleIdentity id, string streamKey) => id.Origin switch
    {
        ParticleOrigin.Continuous continuous => Encode("mako-particle-v2-random-1", id.EmitterId.Value, streamKey,
            "continuous", Head(continuous.Head), Number(continuous.EpisodeStartUs)),
        ParticleOrigin.Burst burst => Encode("mako-particle-v2-random-1", id.EmitterId.Value, streamKey,
            "burst", burst.EventId.Value, Number(burst.PlanOrdinal), Source(burst.Anchor)),
        _ => throw new ArgumentException("未知出生来源。", nameof(id)),
    };
    internal static ulong RandomIndex(ParticleIdentity id) => id.Origin switch
    {
        ParticleOrigin.Continuous continuous => continuous.SequenceStartingAt1,
        ParticleOrigin.Burst burst => checked((ulong)burst.ParticleOrdinal),
        _ => throw new ArgumentException("未知出生来源。", nameof(id)),
    };
    private static long CursorCharacters(CursorIdentity cursor) => 128L + cursor.BranchId.Value.Length + cursor.LineId.Value.Length
        + cursor.BindingId.Value.Length + (cursor.TokenId?.Value.Length ?? 0) + (cursor.OccurrenceId?.Value.Length ?? 0);
    private static string Cursor(CursorIdentity cursor) => Encode(cursor.BranchId.Value, cursor.LineId.Value,
        cursor.BindingId.Value, Number(cursor.TimeRange.StartUs), Number(cursor.TimeRange.EndUs), cursor.ProgressSource.ToString(),
        cursor.TokenId?.Value ?? "", cursor.OccurrenceId?.Value ?? "");
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Encode(params string[] fields)
    {
        StringBuilder builder = new();
        foreach (string field in fields) builder.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
        return builder.ToString();
    }
}
