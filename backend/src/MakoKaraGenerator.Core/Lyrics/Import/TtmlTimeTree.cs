using System.Globalization;
using System.Xml.Linq;

namespace MakoKaraGenerator.Core.Lyrics.Import;

// TTML2 §12.4 and Appendix I.2: both media clock-time and offset-time are
// relative to referenceBegin. AMLL's absolute timestamps are a distinct profile.
internal sealed class TtmlTimeTree
{
    private readonly decimal _frameRate;
    private readonly decimal _nominalFrameRate;
    private readonly decimal _subFrameRate;
    private readonly decimal _tickRate;
    private readonly bool _absolute;
    private readonly Dictionary<XElement, Interval> _intervals = [];

    internal readonly record struct Interval(decimal Start, decimal? End, bool Explicit, bool Valid)
    {
        internal long StartUs => Microseconds(Start);
        internal long? EndUs => End is { } end ? Microseconds(end) : null;
    }

    internal TtmlTimeTree(XElement root, bool absolute)
    {
        _absolute = absolute;
        string? timeBase = Parameter(root, "timeBase");
        // SMPTE non-drop 帧时钟可由 frameRate 确定；clock 与 drop-frame 需要外部同步，不能猜。
        if (timeBase is not null and not "media" and not "smpte")
            throw new FormatException("仅支持 TTML media 与 non-drop SMPTE 时间基；clock 不作猜测。");
        string? dropMode = Parameter(root, "dropMode");
        if (dropMode is not null and not "nonDrop") throw new FormatException("TTML drop-frame 时间语义未支持。");
        string? markerMode = Parameter(root, "markerMode");
        if (timeBase == "smpte" && markerMode is not null and not "continuous")
            throw new FormatException("TTML 非连续 SMPTE markerMode 需要外部同步，不支持确定时间导入。");
        _nominalFrameRate = PositiveInteger(Parameter(root, "frameRate"), 30);
        _subFrameRate = PositiveInteger(Parameter(root, "subFrameRate"), 1);
        string? multiplier = Parameter(root, "frameRateMultiplier");
        decimal ratio = 1;
        if (multiplier is not null)
        {
            string[] fields = multiplier.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2) throw new FormatException("frameRateMultiplier 必须为两个正整数。");
            ratio = PositiveInteger(fields[0], 1) / PositiveInteger(fields[1], 1);
        }
        _frameRate = _nominalFrameRate * ratio;
        _tickRate = PositiveInteger(Parameter(root, "tickRate"),
            Parameter(root, "frameRate") is null ? 1 : _frameRate * _subFrameRate);
    }

    internal Interval this[XElement element] => _intervals[element];
    internal bool TryGet(XElement element, out Interval interval) => _intervals.TryGetValue(element, out interval);

    internal void Resolve(XElement body)
    {
        ResolveNode(body, 0, null, false);
        Clip(body, 0, null);
    }

    private Interval ResolveNode(XElement element, decimal reference, decimal? parentEnd, bool parentSeq)
    {
        string? beginText = (string?)element.Attribute("begin");
        string? endText = (string?)element.Attribute("end");
        string? durationText = (string?)element.Attribute("dur");
        decimal begin = beginText is null ? reference : (_absolute ? 0 : reference) + Parse(beginText);
        decimal? end = endText is null ? null : (_absolute ? 0 : reference) + Parse(endText);
        if (durationText is not null)
        {
            decimal durationEnd = begin + Parse(durationText);
            end = end is { } specified ? Math.Min(specified, durationEnd) : durationEnd;
        }
        if (end is { } explicitEnd && explicitEnd < begin)
            throw new FormatException("TTML end 不能早于 begin。");
        bool seq = (string?)element.Attribute("timeContainer") == "seq";
        string? container = (string?)element.Attribute("timeContainer");
        if (container is not null and not "seq" and not "par")
            throw new FormatException("未知 TTML timeContainer。");
        decimal cursor = begin;
        decimal maximum = begin;
        bool indefinite = false;
        bool hasContent = false;
        foreach (XNode node in ContentNodes(element))
        {
            if (node is XText text && text.Value.Length > 0
                && (element.Name.LocalName is "p" or "span" || !string.IsNullOrWhiteSpace(text.Value)))
            {
                hasContent = true;
                if (!seq) indefinite = true;
            }
            if (node is not XElement child || !IsContent(child)) continue;
            hasContent = true;
            Interval interval = ResolveNode(child, seq ? cursor : begin, end ?? parentEnd, seq);
            if (interval.End is { } childEnd)
            {
                maximum = Math.Max(maximum, childEnd);
                if (seq) cursor = childEnd;
            }
            else
            {
                indefinite = true;
                if (seq && ContentNodes(element).OfType<XElement>().SkipWhile(sibling => sibling != child).Skip(1).Any())
                    throw new FormatException("seq 前项时长不确定，无法安全解析后项时间。");
            }
        }
        if (end is null)
        {
            if (!hasContent || (!indefinite && ContentNodes(element).OfType<XElement>().Any())) end = maximum;
            else if (parentSeq && element.Name.LocalName is "span" or "br" && !ContentNodes(element).OfType<XElement>().Any()) end = begin;
            else end = parentEnd;
        }
        Interval result = new(begin, end, beginText is not null || endText is not null || durationText is not null, true);
        // Check range before retaining the IR. No floating point conversion is used.
        _ = result.StartUs;
        _ = result.EndUs;
        _intervals.Add(element, result);
        return result;
    }

    private void Clip(XElement element, decimal parentStart, decimal? parentEnd)
    {
        Interval interval = _intervals[element];
        decimal start = Math.Max(parentStart, interval.Start);
        decimal? end = interval.End;
        if (parentEnd is { } limit) end = end is { } own ? Math.Min(own, limit) : limit;
        _intervals[element] = interval with { Start = start, End = end, Valid = end is null || end > start };
        foreach (XElement child in ContentNodes(element).OfType<XElement>()) Clip(child, start, end);
    }

    internal decimal Parse(string value)
    {
        if (value.Length > 128) throw new FormatException("TTML 时间表达式过长。");
        string text = value.Trim();
        if (text.Contains(':', StringComparison.Ordinal))
        {
            string[] parts = text.Split(':');
            if (_absolute && parts.Length == 2) parts = ["00", .. parts];
            if (parts.Length is not (3 or 4)) throw new FormatException("TTML 时钟格式无效。");
            decimal hours = Integer(parts[0]);
            decimal minutes = Integer(parts[1]);
            decimal seconds = Number(parts[2]);
            if (minutes >= 60 || seconds >= 60) throw new FormatException("TTML 分钟或秒超出范围。");
            decimal result = hours * 3600 + minutes * 60 + seconds;
            if (parts.Length == 4)
            {
                if (seconds != decimal.Truncate(seconds)) throw new FormatException("帧时钟秒必须为整数。");
                string[] frame = parts[3].Split('.');
                decimal frames = Integer(frame[0]);
                decimal subFrames = frame.Length == 2 ? Integer(frame[1]) : 0;
                if (frame.Length > 2 || frames >= _nominalFrameRate || subFrames >= _subFrameRate)
                    throw new FormatException("TTML 帧或子帧超出时间基范围。");
                result += (frames + subFrames / _subFrameRate) / _frameRate;
            }
            return result;
        }
        string unit = text.EndsWith("ms", StringComparison.Ordinal) ? "ms" : text.Length > 0 ? text[^1..] : "";
        decimal factor = unit switch { "h" => 3600, "m" => 60, "s" => 1, "ms" => 0.001m,
            "f" => 1 / _frameRate, "t" => 1 / _tickRate, _ => 0 };
        if (factor != 0) return Number(text[..^unit.Length]) * factor;
        if (_absolute) return Number(text); // AMLL commonly emits bare seconds.
        throw new FormatException("TTML 时间缺少合法单位。");
    }

    private static decimal Number(string value) =>
        value.Length > 0 && value.Any(char.IsAsciiDigit)
        && decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number)
        && number >= 0 ? number : throw new FormatException("TTML 时间必须为非负十进制数。");
    private static decimal Integer(string value)
    {
        if (value.Length == 0 || !value.All(char.IsAsciiDigit)) throw new FormatException("TTML 时间分量必须为整数。");
        return Number(value);
    }
    private static decimal PositiveInteger(string? value, decimal fallback)
    {
        decimal result = value is null ? fallback : Integer(value);
        return result > 0 ? result : throw new FormatException("TTML 时间基必须为正数。");
    }
    private static long Microseconds(decimal seconds) => checked((long)decimal.Round(seconds * 1_000_000m, 0, MidpointRounding.AwayFromZero));
    private static string? Parameter(XElement element, string name) =>
        (string?)element.Attribute(XName.Get(name, "http://www.w3.org/ns/ttml#parameter")) ?? (string?)element.Attribute(name);
    private static IEnumerable<XNode> ContentNodes(XElement element)
    {
        foreach (XNode node in element.Nodes())
        {
            if (node is not XElement child || IsContent(child)) { yield return node; continue; }
            if (child.Name.NamespaceName is "http://www.w3.org/ns/ttml" or "http://www.w3.org/ns/ttml#metadata"
                or "http://music.apple.com/lyric-ttml-internal" or "http://www.example.com/ns/amll"
                || child.Name.LocalName is "metadata" or "head") continue;
            foreach (XNode descendant in ContentNodes(child)) yield return descendant;
        }
    }
    internal static bool IsContent(XElement element) =>
        element.Name.NamespaceName == "http://www.w3.org/ns/ttml" && element.Name.LocalName is "body" or "div" or "p" or "span" or "br";
}
