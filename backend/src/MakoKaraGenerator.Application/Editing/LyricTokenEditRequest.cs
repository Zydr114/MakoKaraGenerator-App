using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>固定完整行与手工文本元素边界的重切请求。</summary>
/// <param name="Baseline">明确来源、稳定行与完整原行；元素标识必须为空。</param>
/// <param name="Segments">无空段且按 Unicode 文本元素边界切开的正文，拼接必须等于原正文。</param>
/// <param name="EstimateTimes">是否沿现有领域规则估算时间。</param>
public sealed record LyricLineTokenSplitRequest(
    LyricContentEditBaseline Baseline, ImmutableArray<string> Segments, bool EstimateTimes);

/// <summary>对明确来源的全部行应用规则；完整原来源用于拒绝旧批量输入。</summary>
/// <param name="Baseline">捕获时的工作区、来源标识与完整原来源。</param>
/// <param name="Rule">现有领域切分规则；None 为合法无变化。</param>
/// <param name="EstimateTimes">是否沿现有领域规则估算时间。</param>
public sealed record LyricBranchTokenSplitRequest(
    LyricBranchEditBaseline Baseline, TokenSplitRule Rule, bool EstimateTimes);
