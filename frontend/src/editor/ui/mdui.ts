// mdui 2 接入：样式、按需注册的组件、Material You 主题（设计 §3.2）。
// 组件按需引入，不整包注册；新增用到的组件时在这里补一行。

import 'mdui/mdui.css';
import 'mdui/components/button.js';
import 'mdui/components/button-icon.js';
import 'mdui/components/card.js';
import 'mdui/components/chip.js';
import 'mdui/components/circular-progress.js';
import 'mdui/components/dialog.js';
import 'mdui/components/divider.js';
import 'mdui/components/linear-progress.js';
import 'mdui/components/list.js';
import 'mdui/components/list-item.js';
import 'mdui/components/list-subheader.js';
import 'mdui/components/menu.js';
import 'mdui/components/menu-item.js';
import 'mdui/components/navigation-drawer.js';
import 'mdui/components/navigation-rail.js';
import 'mdui/components/navigation-rail-item.js';
import 'mdui/components/checkbox.js';
import 'mdui/components/radio.js';
import 'mdui/components/radio-group.js';
import 'mdui/components/tabs.js';
import 'mdui/components/tab.js';
import 'mdui/components/tab-panel.js';
import 'mdui/components/dropdown.js';
import 'mdui/components/select.js';
import 'mdui/components/segmented-button.js';
import 'mdui/components/segmented-button-group.js';
import 'mdui/components/slider.js';
import 'mdui/components/switch.js';
import 'mdui/components/text-field.js';
import 'mdui/components/tooltip.js';
import 'mdui/components/top-app-bar.js';
import 'mdui/components/top-app-bar-title.js';
import '@mdui/icons/add.js';
import '@mdui/icons/arrow-back.js';
import '@mdui/icons/arrow-downward.js';
import '@mdui/icons/arrow-drop-down.js';
import '@mdui/icons/arrow-upward.js';
import '@mdui/icons/call-split.js';
import '@mdui/icons/check.js';
import '@mdui/icons/check-circle.js';
import '@mdui/icons/chevron-left.js';
import '@mdui/icons/chevron-right.js';
import '@mdui/icons/headphones.js';
import '@mdui/icons/my-location.js';
import '@mdui/icons/content-copy.js';
import '@mdui/icons/content-paste.js';
import '@mdui/icons/delete.js';
import '@mdui/icons/description.js';
import '@mdui/icons/download.js';
import '@mdui/icons/drag-indicator.js';
import '@mdui/icons/drive-file-move.js';
import '@mdui/icons/error.js';
import '@mdui/icons/expand-more.js';
import '@mdui/icons/home.js';
import '@mdui/icons/image.js';
import '@mdui/icons/info.js';
import '@mdui/icons/library-books.js';
import '@mdui/icons/link.js';
import '@mdui/icons/link-off.js';
import '@mdui/icons/memory.js';
import '@mdui/icons/merge.js';
import '@mdui/icons/music-note.js';
import '@mdui/icons/music-off.js';
import '@mdui/icons/palette.js';
import '@mdui/icons/playlist-add.js';
import '@mdui/icons/settings.js';
import '@mdui/icons/style.js';
import '@mdui/icons/translate.js';
import '@mdui/icons/upload-file.js';
import '@mdui/icons/view-quilt.js';
import '@mdui/icons/warning.js';
import '@mdui/icons/edit.js';
import '@mdui/icons/close.js';
import '@mdui/icons/folder.js';
import '@mdui/icons/folder-open.js';
import '@mdui/icons/more-vert.js';
import '@mdui/icons/movie.js';
import '@mdui/icons/pause.js';
import '@mdui/icons/play-arrow.js';
import '@mdui/icons/redo.js';
import '@mdui/icons/refresh.js';
import '@mdui/icons/restore.js';
import '@mdui/icons/save.js';
import '@mdui/icons/undo.js';
import '@mdui/icons/zoom-in.js';
import '@mdui/icons/zoom-out.js';
import { setColorScheme } from 'mdui/functions/setColorScheme.js';
import { setTheme } from 'mdui/functions/setTheme.js';

/** 主题种子色：生成整套 Material You 配色。 */
export const SEED_COLOR = '#3f6fd8';

export function installMdui(): void {
  setTheme('auto');
  setColorScheme(SEED_COLOR);
  guardDetachedRefs();
}

type FieldInternals = { inputRef?: { value?: HTMLElement } } & Record<string, unknown>;

/**
 * mdui 2.1.5 的表单组件在 value／disabled 变化后（有的先等 updateComplete）访问内部 input 的引用，
 * 例如校验或调整高度；如果这期间元素已被移除（命令刷新后选择跳到别的文本单元，或保存后编辑面板换成加载条），
 * 引用已清空，会抛出未处理的异常。只在引用已清空时吞掉这类异常。
 */
const DETACHED_GUARDS: Record<string, readonly string[]> = {
  'mdui-text-field': ['onValueChange', 'onDisabledChange'],
  'mdui-switch': ['onDisabledChange'],
  'mdui-checkbox': ['onDisabledChange'],
  'mdui-slider': ['onDisabledChange'],
};

function guardDetachedRefs(): void {
  for (const [tag, methods] of Object.entries(DETACHED_GUARDS)) {
    const proto = customElements.get(tag)?.prototype as FieldInternals | undefined;
    for (const name of methods) {
      const original = proto?.[name];
      if (!proto || typeof original !== 'function') continue;
      proto[name] = function (this: FieldInternals, ...args: unknown[]) {
        const rethrowIfAttached = (error: unknown) => {
          if (this.inputRef?.value) throw error;
        };
        try {
          const result: unknown = original.apply(this, args);
          return result instanceof Promise ? result.catch(rethrowIfAttached) : result;
        } catch (error) {
          rethrowIfAttached(error);
          return undefined;
        }
      };
    }
  }
}
