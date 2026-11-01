// 图标（界面改版 §2.4）：Material Symbols Rounded 的内联 SVG，颜色跟随文字色，尺寸由所在控件的样式决定。
// 用到新图标时在 scripts/gen-icons.mjs 里补名字并重新生成。

import type { CSSProperties } from 'react';
import { ICON_PATHS } from './icons.generated';

export type IconName = Exclude<keyof typeof ICON_PATHS, `${string}-fill`>;

export function Icon(props: {
  name: IconName;
  /** 用填充版（有的话），例如当前页签。 */
  filled?: boolean;
  className?: string;
  style?: CSSProperties;
  /** 给出时作为有意义的图片朗读；缺省是装饰图标，读屏跳过。 */
  label?: string;
}) {
  const filled = props.filled ? (ICON_PATHS as Record<string, string>)[`${props.name}-fill`] : undefined;
  return (
    <svg
      className={props.className ? `ic ${props.className}` : 'ic'}
      style={props.style}
      viewBox="0 -960 960 960"
      fill="currentColor"
      {...(props.label ? { role: 'img', 'aria-label': props.label } : { 'aria-hidden': true })}
    >
      <path d={filled ?? ICON_PATHS[props.name]} />
    </svg>
  );
}
