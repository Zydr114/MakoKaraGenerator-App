import { useState } from 'react';
import { Button as AriaButton, Dialog as AriaDialog, DialogTrigger, Heading, Popover } from 'react-aria-components';
import type { AppearanceState, AppearanceStore } from '../appearance/appearance-store';
import { lineTarget } from '../model/appearance-targets';
import { lineAppearance, linePlacement } from '../model/line-placement';
import { isTimed, type EditorLine } from '../model/lyrics';
import type { OrchestrationModel } from '../model/orchestration';
import type { ApplicationIndex } from '../model/preset-applications';
import type { OrchestrationState, OrchestrationStore } from '../orchestration/orchestration-store';
import type { DictCacheStatus } from '../phonetics/dict-store';
import type { ScrollMode, TimingPreferences } from '../phonetics/timing-preferences';
import type { EditorSession, EditorState } from '../session/editor-session';
import { Button, IconButton } from '../ui/Button';
import { Segmented, Switch } from '../ui/choices';
import { Dialog } from '../ui/Dialog';
import { NumberField, Select, TextField } from '../ui/fields';
import { useObservableSelector } from '../ui/hooks';
import { Icon } from '../ui/Icon';
import { Menu, MenuItem, MenuSection, MenuSeparator, MenuTrigger } from '../ui/Menu';
import { toMicros } from '../model/time';
import { lineHasAnyTiming, lineHasChildTiming, type LyricsWorkMode } from './timing-keymap';

const STAGE_FONT_PRESETS = [
  { size: 34, label: '标准 34px' },
  { size: 42, label: '大号 42px' },
  { size: 52, label: '超大 52px' },
] as const;

const NONE = { subscribe: () => () => undefined, get: () => undefined };

const SCROLL_MODE_OPTIONS: ReadonlyArray<{ value: ScrollMode; label: string }> = [
  { value: 'follow', label: '跟随播放' },
  { value: 'lock', label: '锁定当前行' },
  { value: 'free', label: '自由滚动' },
];

/**
 * 顶部歌词快捷工具条（Phase 3 §5.1）：
 * - 模式切换（打轴 / 编辑 / 全文快编）；
 * - 日语自动注音菜单（当前行 / 整个来源 / 合并汉字内长音与拨音开关 / 词典状态徽标）；
 * - 词典管理入口；
 * - 时间戳工具（重切本行 / 重切整来源 / 整行平移 / 补全缺失词元时间 / 清除本行时间）；
 * - 滚动模式与按键延迟补偿/悔棋设置弹窗；
 * - 右侧紧凑的外观（样式分配）与显示在（编排容器）快捷跳转芯片（保留原 LineLinks 全部能力）。
 */
export function LyricsToolbar(props: {
  session: EditorSession;
  state: EditorState;
  mode: LyricsWorkMode;
  onModeChange: (mode: LyricsWorkMode) => void;
  selectedLine: EditorLine | undefined;
  dictStatus: DictCacheStatus;
  furiganaBusy: boolean;
  prefs: TimingPreferences;
  onUpdatePrefs: (patch: Partial<TimingPreferences>) => void;
  onAutoFurigana: (scope: 'line' | 'branch') => void;
  onOpenDictManager: () => void;
  onResplitLine: () => void;
  onResplitBranch: () => void;
  onFillMissingTimes: () => void;
  onClearLineTokenTimesOnly: () => void;
  onClearLineTiming: () => void;
  onClearBranchTiming: () => void;
  appearance: AppearanceStore | null;
  orchestration: OrchestrationStore | null;
  onOpenPage: (page: 'appearance' | 'orchestration') => void;
}) {
  const {
    session,
    state,
    mode,
    onModeChange,
    selectedLine,
    dictStatus,
    furiganaBusy,
    prefs,
    onUpdatePrefs,
    onAutoFurigana,
    onOpenDictManager,
    appearance,
    orchestration,
    onOpenPage,
  } = props;

  const [shiftOpen, setShiftOpen] = useState(false);
  const [shiftText, setShiftText] = useState('');
  const [prefsOpen, setPrefsOpen] = useState(false);

  const model = useObservableSelector<OrchestrationState | undefined, OrchestrationModel | undefined>(orchestration?.state$ ?? NONE, (s) => s?.model);
  const index = useObservableSelector<AppearanceState | undefined, ApplicationIndex | undefined>(appearance?.state$ ?? NONE, (s) => s?.applications?.index);
  const lines = selectedLine ? (state.linesByBranch[selectedLine.branchId] ?? state.lines) : state.lines;
  const branchName = selectedLine ? (state.branches?.find((b) => b.id === selectedLine.branchId)?.name ?? '主歌词') : '主歌词';
  const look = selectedLine && index ? lineAppearance(index, model, selectedLine, lines, branchName) : undefined;
  const placement = selectedLine && model ? linePlacement(model, selectedLine, lines) : undefined;

  const shiftMs = shiftText.trim() === '' ? null : Number(shiftText);
  const shiftErr = shiftText.trim() !== '' && (shiftMs === null || !Number.isFinite(shiftMs) || shiftMs === 0) ? '请输入非零毫秒数，例如 -120' : undefined;
  const canShift = !!selectedLine && isTimed(selectedLine) && session.supportsCommand('lyrics.shiftLine');

  const applyShift = async () => {
    if (!selectedLine || shiftMs === null || shiftErr || !canShift) return;
    const res = await session.command({
      type: 'lyrics.shiftLine',
      target: { branchId: selectedLine.branchId, lineId: selectedLine.id },
      deltaUs: toMicros(Math.round(shiftMs * 1000)),
    });
    if (res) {
      setShiftText('');
      setShiftOpen(false);
    }
  };

  return (
    <header className="lyrics-toolbar" role="toolbar" aria-label="歌词工作区工具栏">
      <Segmented<LyricsWorkMode>
        label="歌词模式"
        size="sm"
        value={mode}
        onChange={onModeChange}
        options={[
          { value: 'timing', label: '打轴', icon: 'ads_click' },
          { value: 'edit', label: '编辑', icon: 'edit' },
          { value: 'quickEdit', label: '全文快编', icon: 'description' },
        ]}
      />

      <span className="lt-divider" aria-hidden />

      {/* 日语自动注音下拉菜单 + 词典状态徽标 */}
      <MenuTrigger>
        <Button
          variant="tonal"
          size="sm"
          icon="translate"
          trailingIcon="arrow_drop_down"
          isPending={furiganaBusy}
          isDisabled={furiganaBusy || state.lines.length === 0}
          className="lt-furigana-btn"
        >
          <span>日语自动注音</span>
          <span className={`lt-dict-dot ${dictStatus.installed ? 'ready' : 'missing'}`} title={dictStatus.installed ? 'NAIST-jdic 词典已就绪' : '首次使用需按需下载词典'} />
        </Button>
        <Menu
          label="日语自动注音"
          onAction={(id) => {
            if (id === 'line') onAutoFurigana('line');
            else if (id === 'branch') onAutoFurigana('branch');
            else if (id === 'mergeOpt') onUpdatePrefs({ mergeLongAndNasalInKanji: !prefs.mergeLongAndNasalInKanji });
            else if (id === 'dict') onOpenDictManager();
          }}
        >
          <MenuItem id="line" icon="wand_stars" disabled={!selectedLine || !session.supportsCommand('lyrics.setRubySegments')}>
            为当前行生成日语注音
          </MenuItem>
          <MenuItem id="branch" icon="lyrics" disabled={state.lines.length === 0 || !session.supportsCommand('lyrics.setRubySegments')}>
            为整个来源生成日语注音（{state.lines.length} 行）
          </MenuItem>
          <MenuSeparator />
          <MenuSection selectionMode="multiple" selectedKeys={prefs.mergeLongAndNasalInKanji ? ['mergeOpt'] : []}>
            <MenuItem id="mergeOpt" description="开启后 郷=きょう、感=かん 合为 1 拍">
              合并汉字内长音／拨音
            </MenuItem>
          </MenuSection>
          <MenuSeparator />
          <MenuItem
            id="dict"
            icon="library_books"
            description={dictStatus.installed ? 'NAIST-jdic 0.6.3b 已离线缓存' : '未缓存 · 首次按需下载约 14~16 MB'}
          >
            词典与用户读音管理…
          </MenuItem>
        </Menu>
      </MenuTrigger>

      <IconButton icon="library_books" label="词典与用户读音管理" size="sm" onPress={onOpenDictManager} />

      {/* 时间戳与切分工具 */}
      <MenuTrigger>
        <Button variant="ghost" size="sm" icon="schedule" trailingIcon="arrow_drop_down" isDisabled={mode === 'quickEdit'}>
          时间工具
        </Button>
        <Menu
          label="时间戳工具"
          onAction={(id) => {
            if (id === 'resplitLine') props.onResplitLine();
            else if (id === 'resplitBranch') props.onResplitBranch();
            else if (id === 'shiftLine') setShiftOpen(true);
            else if (id === 'fillMissing') props.onFillMissingTimes();
            else if (id === 'clearLineTokens') props.onClearLineTokenTimesOnly();
            else if (id === 'clearLineFull') props.onClearLineTiming();
            else if (id === 'clearBranchFull') props.onClearBranchTiming();
          }}
        >
          <MenuItem id="resplitLine" icon="call_split" disabled={!selectedLine || !session.supportsCommand('lyrics.resplitLine')}>
            按规则重切本行…
          </MenuItem>
          <MenuItem id="resplitBranch" icon="wand_stars" disabled={state.lines.length === 0 || !session.supportsCommand('lyrics.resplitBranch')}>
            按规则重切整个来源…
          </MenuItem>
          <MenuSeparator />
          <MenuItem id="shiftLine" icon="swap_horiz" disabled={!canShift}>
            整行平移…
          </MenuItem>
          <MenuItem
            id="fillMissing"
            icon="autorenew"
            disabled={!selectedLine || !isTimed(selectedLine) || !session.supportsCommand('lyrics.setTokenTimes')}
            description="按字数均分补齐本行未定时实义词元"
          >
            补全本行缺失词元时间
          </MenuItem>
          <MenuSeparator />
          <MenuItem
            id="clearLineTokens"
            icon="undo"
            disabled={
              !selectedLine ||
              !lineHasChildTiming(selectedLine) ||
              (!session.supportsCommand('lyrics.clearLineTiming') && !session.supportsCommand('lyrics.clearTokenTime'))
            }
            description="清除本行词元与注音拍时间，保留行起止区间"
          >
            仅清除本行逐字时间（保留行时间）
          </MenuItem>
          <MenuItem
            id="clearLineFull"
            icon="delete"
            danger
            disabled={
              !selectedLine ||
              !lineHasAnyTiming(selectedLine) ||
              (!session.supportsCommand('lyrics.clearLineTiming') && !session.supportsCommand('lyrics.clearTokenTime'))
            }
            description="清除本行起止、词元与注音拍时间，光标复位行首"
          >
            清除本行全部时间（含行与逐字）
          </MenuItem>
          <MenuItem
            id="clearBranchFull"
            icon="delete"
            danger
            disabled={
              state.lines.length === 0 ||
              !state.lines.some(lineHasAnyTiming) ||
              !session.supportsCommand('lyrics.clearBranchTiming')
            }
            description="清空当前来源全部行与逐字时间，保留文本、分段与注音"
          >
            清除全部时间戳（保留文本与分段）…
          </MenuItem>
        </Menu>
      </MenuTrigger>

      {/* 整行平移对话框（从时间工具触发） */}
      <Dialog
        open={shiftOpen}
        onClose={() => setShiftOpen(false)}
        title="整行平移"
        description="整行和已打轴的词元、注音一起前移或后移。"
        className="ask"
        actions={
          <>
            <Button variant="text" size="sm" onPress={() => setShiftOpen(false)}>
              取消
            </Button>
            <Button variant="filled" size="sm" isDisabled={shiftMs === null || !!shiftErr} onPress={() => void applyShift()}>
              平移
            </Button>
          </>
        }
      >
        <TextField
          label="偏移毫秒数（可为负）"
          value={shiftText}
          error={shiftErr}
          helper="正数延后、负数提前，例如 -120"
          onChange={setShiftText}
          onEnter={() => void applyShift()}
          autoFocus
          suffix="ms"
        />
      </Dialog>

      {mode !== 'quickEdit' && (
        <Select<ScrollMode>
          label="滚动模式"
          labelPlacement="none"
          size="sm"
          value={prefs.scrollMode}
          options={SCROLL_MODE_OPTIONS}
          onChange={(scrollMode) => onUpdatePrefs({ scrollMode })}
          className="lt-scroll-select"
          title="动态画布滚动模式"
        />
      )}

      {/* 打轴偏好与延迟补偿弹窗 */}
      <DialogTrigger isOpen={prefsOpen} onOpenChange={setPrefsOpen}>
        <Button variant="ghost" size="sm" icon="tune" className="lt-offset-btn" aria-label="打轴延迟补偿与偏好设置">
          {prefs.tagOffsetMs !== 0 ? `补偿 ${prefs.tagOffsetMs > 0 ? '+' : ''}${prefs.tagOffsetMs}ms` : '打轴设置'}
        </Button>
        <Popover className="popover lt-prefs-pop" offset={6} placement="bottom start">
          <AriaDialog className="lt-prefs-dialog" aria-label="打轴偏好设置">
            <Heading slot="title" className="shift-title">
              打轴与画布偏好
            </Heading>
            <div className="lt-fs-group">
              <NumberField
                label="画布当前行字号"
                size="sm"
                suffix="px"
                integer
                min={28}
                max={60}
                value={prefs.stageFontSize}
                helper="上下文行与注音字号随此值等比缩放"
                onCommit={(stageFontSize) => onUpdatePrefs({ stageFontSize })}
              />
              <div className="lt-fs-presets" role="group" aria-label="画布字号预设">
                {STAGE_FONT_PRESETS.map((preset) => (
                  <button
                    key={preset.size}
                    type="button"
                    className={`lt-fs-preset${prefs.stageFontSize === preset.size ? ' is-active' : ''}`}
                    onClick={() => onUpdatePrefs({ stageFontSize: preset.size })}
                  >
                    {preset.label}
                  </button>
                ))}
              </div>
            </div>
            <NumberField
              label="按键延迟补偿"
              size="sm"
              suffix="ms"
              integer
              min={-500}
              max={500}
              value={prefs.tagOffsetMs}
              helper="落点时间自动减去此值（消除人体反应/蓝牙延迟）"
              onCommit={(tagOffsetMs) => onUpdatePrefs({ tagOffsetMs })}
            />
            <Switch
              label="按 F2 弹出注音框时自动暂停播放"
              checked={prefs.pauseOnRubyPopover}
              onChange={(pauseOnRubyPopover) => onUpdatePrefs({ pauseOnRubyPopover })}
            />
            <Switch
              label="合并汉字内长音／拨音（郷=きょう）"
              checked={prefs.mergeLongAndNasalInKanji}
              onChange={(mergeLongAndNasalInKanji) => onUpdatePrefs({ mergeLongAndNasalInKanji })}
            />
          </AriaDialog>
        </Popover>
      </DialogTrigger>

      <span className="spacer" />

      {/* 右侧紧凑的外观与编排跳转芯片（零丢失原 LineLinks） */}
      {selectedLine && (look || placement) && (
        <div className="lt-links" role="group" aria-label="关联的外观与编排">
          {look && appearance && (
            <AriaButton
              className="lt-chip"
              aria-label={`外观：${look.name ?? '默认样式'}，${look.source}。前往外观页`}
              onPress={() => {
                appearance.setTargets([lineTarget(selectedLine)]);
                onOpenPage('appearance');
              }}
            >
              <Icon name="palette" />
              <span className="lt-chip-text">{look.name ?? '默认样式'}</span>
              <Icon name="chevron_right" className="lt-chip-go" />
            </AriaButton>
          )}
          {placement && orchestration && (
            <AriaButton
              className="lt-chip"
              aria-label={`显示在：${placement.title}，${placement.detail}。前往编排页`}
              onPress={() => {
                orchestration.setBranch(selectedLine.branchId);
                if (placement.clipId) orchestration.selectClip(placement.clipId);
                else orchestration.select(placement.containerId);
                onOpenPage('orchestration');
              }}
            >
              <Icon name="dashboard" />
              <span className="lt-chip-text">{placement.title}</span>
              <Icon name="chevron_right" className="lt-chip-go" />
            </AriaButton>
          )}
        </div>
      )}
    </header>
  );
}
