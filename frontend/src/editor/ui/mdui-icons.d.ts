// @mdui/icons 没有 JSX 类型：所有 <mdui-icon-*> 元素按普通 HTML 元素处理。实际注册的图标见 mdui.ts。
import type React from 'react';

type IconProps = React.DetailedHTMLProps<React.HTMLAttributes<HTMLElement>, HTMLElement>;

declare global {
  namespace React {
    namespace JSX {
      interface IntrinsicElements {
        [icon: `mdui-icon-${string}`]: IconProps;
      }
    }
  }
}
