// 生成图标模块（界面改版 §2.1）：从 @material-symbols/svg-400（Rounded，Apache-2.0）只取用到的图标，
// 写到 src/editor/ui/icons.generated.ts。新增图标时在 ICONS 里补名字（Material Symbols 的名字），重新运行：
//   node scripts/gen-icons.mjs
// 带 -fill 的是填充版，Icon 组件用 filled 属性取它。

import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const ICONS = [
  // 外壳与页面
  'home', 'lyrics', 'lyrics-fill', 'dashboard', 'dashboard-fill', 'palette', 'palette-fill', 'style', 'style-fill',
  'settings', 'tune', 'movie', 'info', 'keyboard', 'license', 'code', 'open_in_new', 'memory', 'light_mode', 'dark_mode',
  'brightness_auto', 'colorize', 'update',
  // 通用动作
  'add', 'close', 'check', 'check_circle', 'check_circle-fill', 'undo', 'redo', 'save', 'edit', 'delete', 'content_copy',
  'content_paste', 'download', 'upload', 'upload_file', 'refresh', 'history', 'search', 'more_horiz', 'more_vert',
  'drag_indicator', 'arrow_back', 'arrow_upward', 'arrow_downward', 'arrow_drop_down', 'keyboard_arrow_down',
  'keyboard_arrow_up', 'chevron_left', 'chevron_right', 'swap_horiz', 'link', 'link_off', 'lock', 'visibility',
  'visibility_off', 'build', 'fact_check', 'autorenew', 'contrast', 'radio_button_unchecked',
  // 状态
  'warning', 'warning-fill', 'error', 'error-fill', 'info-fill', 'pending', 'schedule',
  // 文件与媒体
  'folder', 'folder_open', 'drive_file_move', 'description', 'music_note', 'music_off', 'headphones', 'image', 'wallpaper',
  'graphic_eq', 'library_books', 'deployed_code', 'input', 'audio_file', 'settings_backup_restore',
  // 播放与时间轴
  'play_arrow', 'play_arrow-fill', 'pause', 'pause-fill', 'repeat', 'zoom_in', 'zoom_out', 'fit_screen', 'my_location',
  'straighten', 'ads_click',
  // 歌词与编排
  'translate', 'call_split', 'merge', 'playlist_add', 'view_quilt', 'stacks', 'crop_free', 'crop_16_9', 'aspect_ratio',
  'grid_guides', 'layers',
  // 外观与样式库
  'text_fields', 'gradient', 'texture', 'wand_stars',
];

const root = fileURLToPath(new URL('..', import.meta.url));
const lines = [];
for (const name of ICONS) {
  const svg = await readFile(`${root}node_modules/@material-symbols/svg-400/rounded/${name}.svg`, 'utf8');
  if (!svg.includes('viewBox="0 -960 960 960"')) throw new Error(`${name}：viewBox 不是 0 -960 960 960`);
  const paths = [...svg.matchAll(/<path d="([^"]+)"/g)].map((m) => m[1]);
  if (!paths.length) throw new Error(`${name}：找不到 path`);
  lines.push(`  '${name}': '${paths.join('')}',`);
}

await writeFile(
  `${root}src/editor/ui/icons.generated.ts`,
  `// 由 scripts/gen-icons.mjs 生成，不要手改。Material Symbols Rounded，字重 400（Apache-2.0）；viewBox 是 0 -960 960 960。
export const ICON_PATHS = {
${lines.join('\n')}
} as const;
`,
);
console.log(`已生成 ${ICONS.length} 个图标。`);
