// 文本单元拆分规则：新建工程用数字（规范 §5），导入和整来源重切用字符串名（§6.8、§6.9）。

import type { SplitRule, TokenSplitRuleName } from '../../protocol';

export interface SplitRuleOption {
  name: TokenSplitRuleName;
  code: SplitRule;
  label: string;
}

/** 界面上的顺序：推荐的“混合”在前，“不拆分”在最后。 */
export const SPLIT_RULES: readonly SplitRuleOption[] = [
  { name: 'mixed', code: 1, label: '混合（推荐）：中日文逐字，英文按词' },
  { name: 'perTextElement', code: 2, label: '逐字：英文也逐字母' },
  { name: 'latinWords', code: 3, label: '按词：连续中日文算一个元素' },
  { name: 'byPunctuation', code: 4, label: '按标点' },
  { name: 'byWhitespace', code: 5, label: '按空白' },
  { name: 'none', code: 0, label: '不拆分：保持文件原有粒度' },
];
