using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>注音组中的一个有序文本片段。</summary>
/// <remarks>
/// 片段时间是绝对微秒区间；缺少时间的兼容片段继续跟随基础字的旧语义。
/// 该类型刻意允许表示非法区间，以便打开工程时由校验器报告而不是静默修正。
/// </remarks>
public sealed record RubyTextSegment
{
    /// <summary>片段标识，在整份歌词文档内必须唯一。</summary>
    public required string Id { get; init; }

    /// <summary>片段正文。</summary>
    public required string Text { get; init; }

    /// <summary>片段绝对时间；缺失时为 <see langword="null"/>。</summary>
    public TimeRange? Time { get; init; }

    /// <summary>片段时间来源状态。</summary>
    public TimingStatus TimingStatus { get; init; }

    /// <summary>时间与状态是否自洽。</summary>
    public bool IsTimeStatusConsistent => IsTimeStatusPairConsistent(Time, TimingStatus);

    /// <summary>结构与文本是否可用；非法时间由项目校验器单独报告。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrEmpty(Text)
        && Enum.IsDefined(TimingStatus)
        && IsTimeStatusConsistent;

    /// <summary>判断片段的时间与状态组合。</summary>
    public static bool IsTimeStatusPairConsistent(TimeRange? time, TimingStatus status) => status switch
    {
        TimingStatus.Missing => time is null,
        TimingStatus.Invalid => time.HasValue && !time.Value.IsValid,
        _ => time.HasValue && time.Value.IsValid,
    };

    /// <summary>生成旧聚合正文的稳定兼容片段身份，保证可由有界 HTTP 目标表示。</summary>
    public static string CompatibilityId(string rubyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rubyId);
        string candidate = rubyId + "-segment-1";
        return candidate.Length <= 118 ? candidate
            : "ruby-segment-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rubyId))).ToLowerInvariant();
    }

    /// <summary>创建已校验的片段。</summary>
    public static RubyTextSegment Create(
        string id,
        string text,
        TimeRange? time = null,
        TimingStatus timingStatus = TimingStatus.Missing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);

        RubyTextSegment result = new()
        {
            Id = id,
            Text = text,
            Time = time,
            TimingStatus = timingStatus,
        };

        return result.IsValid
            ? result
            : throw new ArgumentException("注音片段的时间与状态不自洽。", nameof(timingStatus));
    }
}
