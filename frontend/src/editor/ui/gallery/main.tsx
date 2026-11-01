// 控件样张（开发用，界面改版 §4）：把 ui/ 下的新控件按设计稿的样子摆一遍，深浅两种模式对照截图。
// 地址参数：mode=light|dark 固定模式；open=dialog|form|sheet|confirm|text|reorder|toast|menu 打开时直接弹出，便于截图。

import { StrictMode, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { installTheme, type ThemeMode } from '../../theme/theme-store';
import { useObservable } from '../hooks';
import type { Rgba } from '../../model/settings';
import { Button, IconButton } from '../Button';
import { Checkbox, RadioGroup, Segmented, SliderField, Switch } from '../choices';
import { List, Tree, type Key } from '../collections';
import { Dialog, Sheet } from '../Dialog';
import { askReorderSlots, askText, confirmAction, DialogHost } from '../dialogs';
import { toast, ToastHost } from '../feedback';
import { ColorField, CommitField, FieldNote, FilePicker, NumberField, Select, TextField } from '../fields';
import { FormDialog } from '../FormDialog';
import { Icon } from '../Icon';
import { ProgressBar, Spinner, Tag } from '../indicators';
import { Menu, MenuItem, MenuSection, MenuSeparator, MenuTrigger, SubmenuTrigger } from '../Menu';
import { Tabs } from '../Tabs';
import { Tooltip } from '../Tooltip';
import { logoIcon } from '../../theme/logo/logo';
import '../../theme/tokens.css';
import '../../theme/base.css';
import './gallery.css';

const params = new URLSearchParams(location.search);
const theme = installTheme();
const fixedMode = params.get('mode');
if (fixedMode === 'light' || fixedMode === 'dark') theme.set({ mode: fixedMode });
const open = params.get('open');

const LINES = [
  { id: 'l1', time: '00:12.40', text: '夜空に溶けてゆく 君の声', sub: '消失在夜空里的 你的声音' },
  { id: 'l2', time: '00:16.85', text: 'ひとつだけ 願いが叶うなら', sub: '如果只能实现一个愿望' },
  { id: 'l3', time: '00:21.10', text: 'あの日の約束を もう一度', sub: '再一次 那天的约定' },
  { id: 'l4', time: '00:25.62', text: '（间奏）', sub: '' },
];

const TREE = [
  {
    id: 'root',
    name: '画面',
    children: [
      { id: 'main', name: '主歌词', children: [{ id: 'slot1', name: '上行' }, { id: 'slot2', name: '下行' }] },
      { id: 'trans', name: '翻译：中文' },
      { id: 'leadin', name: '前奏提示组' },
    ],
  },
];

function Gallery() {
  const state = useObservable(theme.state$);
  const [text, setText] = useState('夜空に溶けてゆく');
  const [search, setSearch] = useState('');
  const [size, setSize] = useState(48);
  const [color, setColor] = useState<Rgba>({ red: 1, green: 155, blue: 254, alpha: 255 });
  const [align, setAlign] = useState<'left' | 'center' | 'right'>('center');
  const [font, setFont] = useState<'noto' | 'source' | 'yu'>('noto');
  const [format, setFormat] = useState<'mp4' | 'mov' | 'png'>('mp4');
  const [quality, setQuality] = useState<'draft' | 'final'>('final');
  const [loop, setLoop] = useState(true);
  const [snap, setSnap] = useState(true);
  const [all, setAll] = useState(false);
  const [some, setSome] = useState(true);
  const [zoom, setZoom] = useState(60);
  const [page, setPage] = useState<'lyrics' | 'orchestration' | 'appearance' | 'library'>('lyrics');
  const [pane, setPane] = useState<'props' | 'anim' | 'check'>('props');
  const [lines, setLines] = useState(LINES);
  const [selected, setSelected] = useState<Set<Key>>(new Set(['l2']));
  const [treeSel, setTreeSel] = useState<Set<Key>>(new Set(['slot1']));
  const [dialog, setDialog] = useState(open === 'dialog');
  const [form, setForm] = useState(open === 'form');
  const [sheet, setSheet] = useState(open === 'sheet');
  const [busy, setBusy] = useState(false);
  const [device, setDevice] = useState<Set<Key>>(new Set(['auto']));

  useEffect(() => {
    if (open === 'confirm') void confirmAction({ headline: '删除这个来源？', description: '翻译“中文”和它的 24 行都会删除，可以撤销。', confirmText: '删除', danger: true });
    if (open === 'text') void askText({ headline: '新建容器', label: '名称', confirmText: '新建' });
    if (open === 'reorder') void askReorderSlots();
    if (open === 'toast') toast('已保存到 夜空.mkproj', { label: '打开位置', run: () => {} });
  }, []);

  const reorder = (keys: Key[], target: Key, position: 'before' | 'after') => {
    setLines((list) => {
      const moving = list.filter((l) => keys.includes(l.id));
      const rest = list.filter((l) => !keys.includes(l.id));
      const at = rest.findIndex((l) => l.id === target) + (position === 'after' ? 1 : 0);
      return [...rest.slice(0, at), ...moving, ...rest.slice(at)];
    });
  };

  return (
    <div className="gallery">
      <header className="g-top">
        <img src={logoIcon} alt="" className="g-logo" />
        <b>控件样张</b>
        <Tabs
          label="页面"
          variant="pill"
          value={page}
          onChange={setPage}
          options={[
            { value: 'lyrics', label: '歌词', icon: 'lyrics' },
            { value: 'orchestration', label: '编排', icon: 'dashboard' },
            { value: 'appearance', label: '外观', icon: 'palette' },
            { value: 'library', label: '样式库', icon: 'style' },
          ]}
        />
        <span className="spacer" />
        <Segmented<ThemeMode>
          label="模式"
          size="sm"
          value={state.mode}
          onChange={(mode) => theme.set({ mode })}
          options={[
            { value: 'auto', label: '跟随系统' },
            { value: 'light', label: '浅色' },
            { value: 'dark', label: '深色' },
          ]}
        />
        <CommitField label="主题色" labelPlacement="none" size="sm" value={state.seed} onCommit={(seed) => theme.set({ seed })} style={{ width: 100 }} />
        <Switch label="鲜艳" checked={state.vivid} onChange={(vivid) => theme.set({ vivid })} />
      </header>

      <main className="g-grid">
        <section className="g-panel">
          <h3>按钮</h3>
          <div className="g-row">
            <Button variant="filled" icon="download">
              导出
            </Button>
            <Button variant="tonal" icon="play_arrow">
              试播
            </Button>
            <Button icon="upload_file">导入歌词…</Button>
            <Button variant="text">取消</Button>
            <Button variant="ghost" icon="history">
              历史
            </Button>
          </div>
          <div className="g-row">
            <Button size="sm" variant="filled">
              应用
            </Button>
            <Button size="sm" variant="tonal" trailingIcon="arrow_drop_down">
              快速布局
            </Button>
            <Button size="sm">重置</Button>
            <Button size="sm" isDisabled>
              不可用
            </Button>
            <Button size="sm" variant="filled" isPending>
              验证中
            </Button>
            <Button size="sm" danger icon="delete">
              删除
            </Button>
            <Button size="lg" variant="filled" icon="add">
              新建工程
            </Button>
          </div>
          <div className="g-row">
            <IconButton icon="undo" label="撤销" />
            <IconButton icon="redo" label="重做" isDisabled />
            <IconButton icon="repeat" label="循环播放" selected={loop} onSelectedChange={setLoop} />
            <IconButton icon="play_arrow" label="播放" variant="filled" />
            <IconButton icon="tune" label="设置" variant="tonal" />
            <IconButton icon="more_vert" label="更多" variant="outlined" />
            <span className="vsep" />
            <IconButton icon="zoom_out" label="缩小" size="sm" />
            <IconButton icon="zoom_in" label="放大" size="sm" />
            <IconButton icon="my_location" label="跟随播放头" size="sm" selected={snap} onSelectedChange={setSnap} fillWhenSelected />
          </div>
        </section>

        <section className="g-panel">
          <h3>输入</h3>
          <div className="g-form">
            <TextField label="歌词" value={text} onChange={setText} helper="回车提交，Esc 撤回。" />
            <TextField label="搜索" labelPlacement="none" icon="search" placeholder="搜索歌词" value={search} onChange={setSearch} />
            <NumberField label="字号" value={size} onCommit={setSize} min={8} max={400} integer suffix="px" />
            <NumberField label="描边" value={-2} onCommit={() => {}} min={0} max={20} suffix="px" />
            <ColorField label="填充色" value={color} onCommit={setColor} />
            <Select
              label="字体"
              value={font}
              onChange={setFont}
              options={[
                { value: 'noto', label: 'Noto Sans CJK JP', description: '本机字体' },
                { value: 'source', label: '思源宋体' },
                { value: 'yu', label: 'Yu Gothic UI', disabled: true },
              ]}
            />
            <TextField label="备注" value="第二段副歌换成渐显。" onChange={() => {}} rows={3} />
            <TextField label="只读" value="C:/Songs/夜空.mkproj" onChange={() => {}} readOnly />
          </div>
          <h4>属性检查器（标签在左）</h4>
          <div className="g-form side">
            <NumberField label="X" labelPlacement="side" size="sm" value={960} onCommit={() => {}} suffix="px" />
            <NumberField label="Y" labelPlacement="side" size="sm" value={880} onCommit={() => {}} suffix="px" />
            <ColorField label="描边色" labelPlacement="side" size="sm" value={{ red: 20, green: 24, blue: 40, alpha: 255 }} onCommit={() => {}} />
            <Select
              label="对齐"
              labelPlacement="side"
              size="sm"
              value={align}
              onChange={setAlign}
              options={[
                { value: 'left', label: '左' },
                { value: 'center', label: '居中' },
                { value: 'right', label: '右' },
              ]}
            />
          </div>
          <FilePicker label="选择音频" accept="audio/*" file={null} onChange={() => {}} />
          <FieldNote icon="info" action={<Button size="sm" variant="text">去设置</Button>}>
            没有音频时只能按句打轴。
          </FieldNote>
        </section>

        <section className="g-panel">
          <h3>选择</h3>
          <div className="g-row">
            <Switch label="吸附到节拍" checked={snap} onChange={setSnap} />
            <Switch label="停用" checked={false} onChange={() => {}} disabled />
          </div>
          <div className="g-row">
            <Checkbox label="全选" checked={all} onChange={setAll} />
            <Checkbox label="部分" checked={false} indeterminate={some} onChange={() => setSome(!some)} />
            <Checkbox label="已选" checked onChange={() => {}} />
          </div>
          <RadioGroup
            label="质量"
            orientation="horizontal"
            value={quality}
            onChange={setQuality}
            options={[
              { value: 'draft', label: '草稿' },
              { value: 'final', label: '成片' },
            ]}
          />
          <Segmented
            label="对齐"
            value={align}
            onChange={setAlign}
            options={[
              { value: 'left', label: '左' },
              { value: 'center', label: '居中' },
              { value: 'right', label: '右' },
            ]}
          />
          <Segmented
            label="对齐（小）"
            size="sm"
            stretch
            value={align}
            onChange={setAlign}
            options={[
              { value: 'left', label: '左' },
              { value: 'center', label: '居中' },
              { value: 'right', label: '右' },
            ]}
          />
          <SliderField label="缩放" value={zoom} min={10} max={200} step={1} format={(v) => `${v}%`} onCommit={setZoom} />
          <SliderField label="不透明度" value={0.4} min={0} max={1} step={0.01} format={(v) => v.toFixed(2)} onCommit={() => {}} disabled />
          <RadioGroup
            label="导出格式"
            variant="card"
            value={format}
            onChange={setFormat}
            options={[
              { value: 'mp4', label: 'MP4 视频', description: 'H.264，带音频，直接发布。' },
              { value: 'mov', label: 'MOV 透明', description: 'ProRes 4444，叠到别的视频上。' },
              { value: 'png', label: 'PNG 序列', description: '逐帧图片，体积大。', disabled: true },
            ]}
          />
        </section>

        <section className="g-panel">
          <h3>页签与标记</h3>
          <Tabs
            label="检查器"
            value={pane}
            onChange={setPane}
            options={[
              { value: 'props', label: '属性' },
              { value: 'anim', label: '动画' },
              { value: 'check', label: '检查', badge: 3 },
            ]}
          >
            {(value) => <p className="g-pad muted">当前：{value}</p>}
          </Tabs>
          <div className="g-row">
            <Tag>默认</Tag>
            <Tag tone="accent" icon="check">
              已应用
            </Tag>
            <Tag tone="ok" icon="check_circle">
              通过
            </Tag>
            <Tag tone="warn" icon="warning">
              2 处警告
            </Tag>
            <Tag tone="error" icon="error">
              失败
            </Tag>
          </div>
          <ProgressBar label="正在导出" value={62} showLabel />
          <ProgressBar label="正在连接" showLabel />
          <div className="g-row">
            <Spinner />
            <Tooltip label="悬停 0.6 秒后出现">
              <Button size="sm" variant="ghost" icon="info">
                提示
              </Button>
            </Tooltip>
            <MenuTrigger defaultOpen={open === 'menu'}>
              <Button size="sm" trailingIcon="arrow_drop_down">
                文件
              </Button>
              <Menu onAction={(id) => toast(`菜单：${String(id)}`)}>
                <MenuItem id="save" icon="save" shortcut="Ctrl+S">
                  保存
                </MenuItem>
                <MenuItem id="saveAs" icon="drive_file_move" description="换个位置保存一份">
                  另存为…
                </MenuItem>
                <SubmenuTrigger>
                  <MenuItem id="export" icon="download">
                    导出歌词
                  </MenuItem>
                  <Menu placement="end top">
                    <MenuItem id="lrc">增强 LRC</MenuItem>
                    <MenuItem id="ttml">TTML</MenuItem>
                  </Menu>
                </SubmenuTrigger>
                <MenuSeparator />
                <MenuItem id="close" icon="close" danger>
                  关闭工程
                </MenuItem>
                <MenuItem id="disabled" disabled>
                  不可用的一项
                </MenuItem>
              </Menu>
            </MenuTrigger>
            <MenuTrigger>
              <IconButton icon="memory" label="渲染设备" />
              <Menu selectionMode="single" selectedKeys={device} onSelectionChange={(keys) => keys !== 'all' && setDevice(new Set(keys))} placement="bottom end">
                <MenuSection title="渲染设备">
                  <MenuItem id="auto" description="后端默认：GPU">
                    跟随后端
                  </MenuItem>
                  <MenuItem id="cpu">CPU</MenuItem>
                  <MenuItem id="gpu">GPU</MenuItem>
                </MenuSection>
              </Menu>
            </MenuTrigger>
          </div>
        </section>

        <section className="g-panel g-tall">
          <h3>列表（可拖动排序）</h3>
          <List
            label="句子"
            items={lines}
            textValue={(l) => l.text}
            selectionMode="multiple"
            selectedKeys={selected}
            onSelectionChange={setSelected}
            onReorder={reorder}
            rowClassName={() => 'g-line'}
          >
            {(line) => (
              <>
                <span className="g-time tnum faint">{line.time}</span>
                <span className="g-text">
                  <span lang="ja">{line.text}</span>
                  {line.sub && <span className="muted">{line.sub}</span>}
                </span>
              </>
            )}
          </List>
          <h3>树</h3>
          <Tree
            label="结构"
            items={TREE}
            textValue={(n) => n.name}
            defaultExpandedKeys={['root', 'main']}
            selectedKeys={treeSel}
            onSelectionChange={setTreeSel}
          >
            {(node) => (
              <>
                <Icon name={node.children ? 'stacks' : 'text_fields'} />
                <span>{node.name}</span>
              </>
            )}
          </Tree>
        </section>

        <section className="g-panel stage-zone">
          <h3>幕后区（第三色）</h3>
          <div className="g-row">
            <Button variant="filled" icon="check">
              应用模板
            </Button>
            <Button variant="tonal">试看</Button>
            <IconButton icon="visibility" label="显示参考框" selected onSelectedChange={() => {}} />
          </div>
          <Segmented
            label="预览"
            size="sm"
            value={quality}
            onChange={setQuality}
            options={[
              { value: 'draft', label: '草稿' },
              { value: 'final', label: '成片' },
            ]}
          />
          <Switch label="安全区" checked onChange={() => {}} />
          <SliderField label="时刻" value={42} min={0} max={100} step={1} format={(v) => `${v}%`} onCommit={() => {}} />
          <TextField label="模板名" value="淡色字幕" onChange={() => {}} />
          <div className="g-row">
            <Tag tone="accent">含效果</Tag>
            <Tag>内置</Tag>
          </div>
        </section>

        <section className="g-panel">
          <h3>弹出层</h3>
          <div className="g-row">
            <Button onPress={() => setDialog(true)}>对话框</Button>
            <Button onPress={() => setForm(true)}>表单对话框</Button>
            <Button onPress={() => setSheet(true)}>右侧面板</Button>
            <Button onPress={() => void confirmAction({ headline: '删除这个来源？', description: '翻译“中文”和它的 24 行都会删除，可以撤销。', confirmText: '删除', danger: true })}>
              确认
            </Button>
            <Button onPress={() => void askText({ headline: '新建容器', label: '名称', confirmText: '新建' })}>输入</Button>
            <Button onPress={() => void askReorderSlots()}>竖排按钮</Button>
            <Button onPress={() => toast('已保存到 夜空.mkproj', { label: '打开位置', run: () => {} })}>消息条</Button>
          </div>
        </section>
      </main>

      <Dialog
        open={dialog}
        onClose={() => setDialog(false)}
        title="关于 MakoKara"
        description="卡拉 OK 字幕编辑器。"
        actions={
          <Button variant="text" onPress={() => setDialog(false)}>
            关闭
          </Button>
        }
      >
        <p className="g-p">版本 0.108.0 · 合同 1.40.0</p>
      </Dialog>
      <FormDialog
        open={form}
        headline="新建工程"
        description="选好歌词和音频，之后都能再改。"
        submitText="创建"
        busy={busy}
        onSubmit={() => {
          setBusy(true);
          setTimeout(() => (setBusy(false), setForm(false)), 1200);
        }}
        onClose={() => setForm(false)}
        secondaryAction={{ text: '上一步', onClick: () => {} }}
        headerExtra={<Tag>2 / 3</Tag>}
      >
        <TextField label="工程名" value="夜空" onChange={() => {}} />
        <FilePicker label="选择音频" accept="audio/*" file={null} onChange={() => {}} />
      </FormDialog>
      <Sheet
        open={sheet}
        onClose={() => setSheet(false)}
        title="工程设置"
        headerActions={<IconButton icon="history" label="历史" />}
        toolbar={
          <Tabs
            label="设置分组"
            value={pane}
            onChange={setPane}
            options={[
              { value: 'props', label: '基本' },
              { value: 'anim', label: '背景' },
              { value: 'check', label: '音频' },
            ]}
          />
        }
      >
        <TextField label="工程名" value="夜空" onChange={() => {}} />
        <NumberField label="帧率" value={60} onCommit={() => {}} suffix="fps" />
        <Switch label="导出时带音频" checked onChange={() => {}} />
      </Sheet>
      <DialogHost />
      <ToastHost />
    </div>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Gallery />
  </StrictMode>,
);
