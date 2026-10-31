namespace MakoKaraGenerator.Application.Editing;

/// <summary>对固定原行中的稳定元素重排；下标只在完整原行仍相等时解释。</summary>
/// <param name="Baseline">工作区身份、来源与完整原行；任何原行字段变化均拒绝旧请求。</param>
/// <param name="TokenId">被移动元素的稳定标识。</param>
/// <param name="DestinationIndex">在该固定原行内的最终位置，不作为元素身份使用。</param>
/// <param name="ReassignTimeSlots">显式同意沿原顺序重新分配已定时元素的时间槽。</param>
public sealed record LyricTokenReorderRequest(
    LyricContentEditBaseline Baseline, string TokenId, int DestinationIndex, bool ReassignTimeSlots);
