namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>切分规则的显示名（Core 与界面共用一份文案，避免两处走偏）。</summary>
public static class TokenSplitRuleExtensions
{
    /// <summary>取规则的显示名。</summary>
    /// <param name="rule">切分规则。</param>
    /// <returns>中文显示名。</returns>
    public static string ToDisplayName(this TokenSplitRule rule) => rule switch
    {
        TokenSplitRule.None => "不切分",
        TokenSplitRule.Mixed => "混合（CJK 逐字 + 拉丁按词）",
        TokenSplitRule.PerTextElement => "每个文本元素",
        TokenSplitRule.LatinWords => "按词（连续同类文字算一个）",
        TokenSplitRule.ByPunctuation => "按标点",
        TokenSplitRule.ByWhitespace => "按空白",
        _ => rule.ToString(),
    };
}
