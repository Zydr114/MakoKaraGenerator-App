import { useMemo, useState, type CSSProperties } from 'react';
import { ToggleButton, ToggleButtonGroup } from 'react-aria-components';
import { buildScheme, DEFAULT_SEED, normalizeSeed } from '../theme/scheme';
import { logoIcon } from '../theme/logo/logo';
import { themeStore, type ThemeMode } from '../theme/theme-store';
import { Button } from '../ui/Button';
import { Segmented } from '../ui/choices';
import { Sheet } from '../ui/Dialog';
import { CommitField } from '../ui/fields';
import { useObservable } from '../ui/hooks';
import { Icon } from '../ui/Icon';
import { Tabs } from '../ui/Tabs';
import { cls } from '../ui/cls';
import './shell.css';

/** 主题色色板：第一个是从标志取的默认色，其余取自标志的另外几种颜色，再加绿、橙。 */
const SEEDS: readonly (readonly [hex: string, name: string])[] = [
  [DEFAULT_SEED, '标志蓝'],
  ['#9D41FF', '标志紫'],
  ['#02C8E8', '标志青'],
  ['#F03FA8', '标志粉'],
  ['#F2B705', '标志黄'],
  ['#3F9A52', '绿'],
  ['#E8622C', '橙'],
];

const MODES = [
  { value: 'auto', label: '跟随系统', icon: 'brightness_auto' },
  { value: 'light', label: '浅色', icon: 'light_mode' },
  { value: 'dark', label: '深色', icon: 'dark_mode' },
] as const;

const STRENGTHS = [
  { value: 'soft', label: '柔和' },
  { value: 'vivid', label: '鲜明' },
] as const;

/** 快捷键清单（整体设计 §4.5）。 */
const SHORTCUTS: readonly { keys: string[][]; action: string; scope: string }[] = [
  { keys: [['Ctrl', 'Z']], action: '撤销', scope: '工程页作用于工程；样式库作用于当前草稿' },
  { keys: [['Ctrl', 'Shift', 'Z'], ['Ctrl', 'Y']], action: '重做', scope: '工程页作用于工程；样式库作用于当前草稿' },
  { keys: [['Ctrl', 'S']], action: '保存：工程页保存工程，样式库保存模板', scope: '全局' },
  { keys: [['空格']], action: '播放／暂停', scope: '歌词、编排、外观' },
  { keys: [['←'], ['→'], ['↑'], ['↓']], action: '选上一个／下一个文本单元或行', scope: '歌词、外观' },
  { keys: [['Alt', '←'], ['Alt', '→']], action: '所选文本单元按帧微调', scope: '歌词' },
  { keys: [['按住 J']], action: '打点：按下记起点，松开记终点并前进', scope: '歌词页打点模式' },
  { keys: [['Delete']], action: '歌词页清除文本单元时间；编排页删除所选对象', scope: '歌词、编排' },
  { keys: [['方向键'], ['Shift', '方向键']], action: '画布聚焦时，所选容器移动 1 px；加 Shift 移动 10 px', scope: '编排' },
  { keys: [['Esc']], action: '撤回输入、取消拖动、关闭弹层', scope: '全局' },
];

type Tab = 'appearance' | 'shortcuts';

/**
 * 应用设置（界面改版 §2.5）：右侧面板，外观与快捷键两页，另有“关于”。改动立即生效，记在本机，不需要工程。
 * 外观：主题模式、主题色（色板、自定义）、色彩强度，以及各颜色用在哪里。
 */
export function AppSettings(props: { open: boolean; onClose: () => void; onAbout: () => void }) {
  const [tab, setTab] = useState<Tab>('appearance');
  return (
    <Sheet
      open={props.open}
      onClose={props.onClose}
      title="设置"
      className="app-settings"
      toolbar={
        <div className="stabs">
          <Tabs
            label="设置分类"
            variant="pill"
            value={tab}
            onChange={setTab}
            options={[
              { value: 'appearance', label: '外观', icon: 'palette' },
              { value: 'shortcuts', label: '快捷键', icon: 'keyboard' },
            ]}
          />
          <Button variant="ghost" icon="info" onPress={props.onAbout}>
            关于
          </Button>
        </div>
      }
    >
      {tab === 'appearance' ? <AppearanceSettings /> : <Shortcuts />}
    </Sheet>
  );
}

function AppearanceSettings() {
  const theme = themeStore();
  const state = useObservable(theme.state$);
  const { seed, dark, vivid } = state;
  const preset = SEEDS.some(([hex]) => hex === seed);
  // 每个色块按自己的种子画出主色、辅助色和第三色（同 Android 的壁纸取色），跟着当前明暗和强度。
  const dots = useMemo(() => {
    const dot = (hex: string) => {
      const roles = buildScheme({ seed: hex, dark, vivid });
      return { '--a': roles.primary, '--b': roles['secondary-container'], '--c': roles.tertiary } as CSSProperties;
    };
    return new Map([...SEEDS.map(([hex]) => [hex, dot(hex)] as const), [seed, dot(seed)]]);
  }, [seed, dark, vivid]);

  return (
    <>
      <section className="sset">
        <h4>主题模式</h4>
        <Segmented label="主题模式" value={state.mode} options={MODES} onChange={(mode: ThemeMode) => theme.set({ mode })} />
      </section>
      <section className="sset">
        <h4>主题色</h4>
        <p>默认从软件标志取色。整套界面的颜色都由它推出。</p>
        <ToggleButtonGroup
          aria-label="主题色"
          selectionMode="single"
          selectedKeys={preset ? [seed] : []}
          onSelectionChange={(keys) => {
            const [key] = [...keys];
            if (typeof key === 'string') theme.set({ seed: key });
          }}
          className="swatches"
        >
          {SEEDS.map(([hex, name], i) => (
            <ToggleButton key={hex} id={hex} aria-label={i === 0 ? `${name}（从标志取色，默认）` : name} className={cls('sw', i === 0 && 'logo')}>
              {i === 0 && <img src={logoIcon} alt="" />}
              <span className="swatch-dot" style={dots.get(hex)} />
            </ToggleButton>
          ))}
          <label className={cls('sw', 'custom', !preset && 'on')} title="自定义">
            <span className="swatch-dot" style={dots.get(seed)} />
            <Icon name="colorize" />
            <input type="color" aria-label="自定义主题色" value={seed.toLowerCase()} onChange={(event) => theme.set({ seed: event.currentTarget.value })} />
          </label>
        </ToggleButtonGroup>
        <CommitField
          label="当前"
          labelPlacement="side"
          size="sm"
          className="seedline"
          value={seed}
          validate={(text) => (normalizeSeed(text) ? undefined : '格式是 #RRGGBB')}
          onCommit={(text) => theme.set({ seed: text })}
        />
      </section>
      <section className="sset">
        <h4>色彩强度</h4>
        <Segmented label="色彩强度" value={vivid ? 'vivid' : 'soft'} options={STRENGTHS} onChange={(value) => theme.set({ vivid: value === 'vivid' })} />
      </section>
      <section className="sset">
        <h4>颜色用在哪里</h4>
        <div className="roles">
          <Role color="var(--primary)" name="主色" use="主要操作、选中、焦点" />
          <Role color="var(--secondary-container)" name="辅助色" use="选中的行、当前页、开关按钮" />
          <Role color="var(--tertiary)" name="第三色" use="播放、播放头、逐字填充" />
          <Role color="var(--stage)" name="幕后底色" use="预览和时间轴所在的区域" />
        </div>
        <p>渲染画面的颜色由样式模板决定，不随界面主题变化。</p>
      </section>
    </>
  );
}

function Role(props: { color: string; name: string; use: string }) {
  return (
    <div>
      <i style={{ background: props.color }} />
      <b>{props.name}</b>
      <span>{props.use}</span>
    </div>
  );
}

function Shortcuts() {
  return (
    <section className="sset">
      <p>焦点在文字输入框里时，快捷键都让给输入框。</p>
      <table className="shortcuts">
        <tbody>
          {SHORTCUTS.map((s) => (
            <tr key={s.action}>
              <th scope="row">
                {s.keys.map((combo, i) => (
                  <span key={i} className="combo">
                    {i > 0 && <span className="or">/</span>}
                    {combo.map((k) => (
                      <kbd key={k}>{k}</kbd>
                    ))}
                  </span>
                ))}
              </th>
              <td>
                {s.action}
                <span className="faint">{s.scope}</span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
