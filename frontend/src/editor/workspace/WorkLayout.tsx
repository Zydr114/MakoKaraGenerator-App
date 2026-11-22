import { createContext, useContext, useEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type PointerEvent, type ReactNode } from 'react';
import { Button as AriaButton } from 'react-aria-components';
import { TimelineHeight } from '../timeline/timeline-size';
import { cls } from '../ui/cls';
import { Icon } from '../ui/Icon';
import {
  clampSide,
  clampTimeline,
  initialSide,
  isOverlaySide,
  layoutKey,
  NARROW_WIDTH,
  parseLayout,
  RAIL_WIDTH,
  SIDE_LIMITS,
  TIMELINE_LIMITS,
  widthMode,
  type LayoutPage,
  type LayoutSizes,
  type Side,
} from './layout-prefs';
import './layout.css';

export interface WorkSide {
  /** 栏名：写在窄条上，也是窄条按钮、分隔条的可访问名称。 */
  label: string;
  content: ReactNode;
  /** 常驻时也能收起成窄条（歌词页的逐字预览）；受控，和 onCollapsedChange 一起给。 */
  collapsed?: boolean;
  onCollapsedChange?: (collapsed: boolean) => void;
  /** 暂时定宽（样式库作者草稿的源码页签）：常驻时不能拖，也不改本机记的宽度。 */
  width?: number | undefined;
}

export interface WorkLayoutProps {
  page: LayoutPage;
  /** 页面根节点的类名（workspace、orchestration、appearance、library），冒烟脚本靠它找页面。 */
  className: string;
  /** 幕后那一块：歌词页合并中右栏时中栏即画布；编排、外观也是中栏（连成 T 形或右侧无缝拼接）。 */
  stage: 'center' | 'right';
  left: WorkSide;
  center: ReactNode;
  /** 可选右栏：歌词页重构后合并中右栏给动态画布，不传 right 即铺满右侧全部宽度。 */
  right?: WorkSide | undefined;
  /** 面板上面的一整条（样式库的返回栏）。 */
  top?: ReactNode;
  /** 底部区：播放条、时间轴等；样式库没有底部区，幕后那一块不往下拼接。 */
  dock?: ReactNode;
  /** 底部区里有时间轴时才能拖高度。 */
  timeline?: boolean;
  status: ReactNode;
  /** 不占位置的东西（对话框）。 */
  children?: ReactNode;
}

interface SideApi {
  hide: () => void;
}
const SideContext = createContext<SideApi | undefined>(undefined);

/** 栏里的内容收起自己所在的栏：常驻可收起时收成窄条，覆盖面板时关上。不在栏里时是 undefined。 */
export function useWorkSide(): SideApi | undefined {
  return useContext(SideContext);
}

type Placement = 'docked' | 'rail' | 'overlay';

/**
 * 工作区布局（界面改版 §2.6）：左栏、中栏、右栏三块面板，下面一整条底部区，最下面是状态栏。
 * - 面板之间留 8px 缝，缝就是分隔条：拖动改宽度，双击恢复默认，按页记在本机；
 * - 幕后那一块向下伸进缝里，和底部区连成一整块，接缝处补内凹圆角；
 * - 窗口变窄时栏改为覆盖面板：平时是一条 40px 的窄条，点开浮在中栏上；拼接退化为单独的底部区。
 */
export function WorkLayout(props: WorkLayoutProps) {
  const { page } = props;
  const [mode, setMode] = useState(() => widthMode(window.innerWidth));
  const [narrow, setNarrow] = useState(() => window.innerWidth < NARROW_WIDTH);
  useEffect(() => {
    const onResize = () => {
      setMode(widthMode(window.innerWidth));
      setNarrow(window.innerWidth < NARROW_WIDTH);
    };
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);

  // 覆盖面板：1200～1439 右栏默认打开；更窄时都先关着，同一时间只开一个。换断点时重置。
  const [open, setOpen] = useState<Side | null>(() => (mode === 'rightOverlay' ? 'right' : null));
  const [openMode, setOpenMode] = useState(mode);
  if (openMode !== mode) {
    setOpenMode(mode);
    setOpen(mode === 'rightOverlay' ? 'right' : null);
  }

  // 最新尺寸：拖动中每次移动都改，松手（或双击、按键）后写进本机。
  const [sizes, setSizes] = useState<LayoutSizes>(() => parseLayout(localStorage.getItem(layoutKey(page)), page));
  const latest = useRef(sizes);
  const commit = () => localStorage.setItem(layoutKey(page), JSON.stringify(latest.current));
  const resize = (patch: Partial<LayoutSizes>) => {
    latest.current = { ...latest.current, ...patch };
    setSizes(latest.current);
  };

  const hasRight = props.right !== undefined;
  const sideOf = (side: Side): WorkSide | undefined => (side === 'left' ? props.left : props.right);
  const placement = (side: Side): Placement | 'none' => {
    const def = sideOf(side);
    if (!def) return 'none';
    return isOverlaySide(mode, side) ? (open === side ? 'overlay' : 'rail') : def.collapsed ? 'rail' : 'docked';
  };
  const toggle = (side: Side) => {
    const def = sideOf(side);
    if (!def) return;
    if (isOverlaySide(mode, side)) setOpen((current) => (current === side ? null : side));
    else def.onCollapsedChange?.(false);
  };
  const hide = (side: Side) => {
    const def = sideOf(side);
    if (!def) return;
    if (isOverlaySide(mode, side)) setOpen(null);
    else def.onCollapsedChange?.(true);
  };
  // 给内容的收起接口保持同一个对象，免得 memo 过的内容跟着重渲染。
  const hideRef = useRef(hide);
  hideRef.current = hide;
  const sideApis = useRef({ left: { hide: () => hideRef.current('left') }, right: { hide: () => hideRef.current('right') } });

  const left = placement('left');
  const right = placement('right');
  const hasDock = props.dock !== undefined;
  const joined = !hasDock ? null : props.stage === 'center' ? 'center' : right === 'docked' ? 'right' : null;
  const rightWidth = props.right?.width ?? sizes.right;
  const style = {
    '--left': `${left === 'docked' ? sizes.left : RAIL_WIDTH}px`,
    '--right': `${right === 'docked' ? rightWidth : RAIL_WIDTH}px`,
    '--left-w': `${sizes.left}px`,
    '--right-w': `${rightWidth}px`,
  } as CSSProperties;

  const dock = useRef<HTMLDivElement>(null);
  const timelineNow = () => dock.current?.querySelector('.timeline-canvas canvas')?.getBoundingClientRect().height ?? TIMELINE_LIMITS.min;

  // 栏的内容一直挂着（收起时只是隐藏），换断点、开关覆盖面板都不丢状态。
  const sideTile = (side: Side, where: Placement | 'none') => {
    const def = sideOf(side);
    if (!def || where === 'none') return null;
    return (
      <div
        className={cls('work-side', side, where, props.stage === side && 'stage-zone', joined === side && 'joins')}
        hidden={where === 'rail'}
        {...(where === 'overlay' ? { role: 'region', 'aria-label': def.label } : {})}
      >
        <SideContext.Provider value={sideApis.current[side]}>{def.content}</SideContext.Provider>
      </div>
    );
  };

  const rail = (side: Side, where: Placement | 'none') => {
    const def = sideOf(side);
    if (!def || where === 'none' || where === 'docked') return null;
    const expanded = where === 'overlay';
    const toward = (side === 'left') === expanded ? 'chevron_left' : 'chevron_right';
    return (
      <AriaButton className={cls('work-rail', side, expanded && 'on')} aria-label={def.label} aria-expanded={expanded} onPress={() => toggle(side)}>
        <Icon name={toward} />
        <span className="rail-label">{def.label}</span>
      </AriaButton>
    );
  };

  return (
    <div className={cls('work-layout', props.className)} data-width={mode}>
      {narrow && (
        <div className="work-banner" role="status">
          <Icon name="aspect_ratio" />
          请使用更宽的窗口：宽度不足 {NARROW_WIDTH} 像素，部分内容可能显示不全。
        </div>
      )}
      {props.top && <div className="work-top">{props.top}</div>}
      <div className={cls('work', !hasDock && 'no-dock', !hasRight && 'no-right')} style={style}>
        {rail('left', left)}
        {sideTile('left', left)}
        {left === 'docked' && (
          <Splitter
            className="col left"
            orientation="vertical"
            label={`调整${props.left.label}宽度`}
            value={sizes.left}
            min={SIDE_LIMITS.left.min}
            max={SIDE_LIMITS.left.max}
            sign={1}
            start={() => latest.current.left}
            onChange={(px) => resize({ left: clampSide('left', px) })}
            onCommit={commit}
            onReset={() => resize({ left: initialSide(page, 'left') })}
          />
        )}
        <div className={cls('work-center', props.stage === 'center' && 'stage-zone', joined === 'center' && 'joins')}>{props.center}</div>
        {props.right && right === 'docked' && props.right.width === undefined && (
          <Splitter
            className="col right"
            orientation="vertical"
            label={`调整${props.right.label}宽度`}
            value={sizes.right}
            min={SIDE_LIMITS.right.min}
            max={SIDE_LIMITS.right.max}
            sign={-1}
            start={() => latest.current.right}
            onChange={(px) => resize({ right: clampSide('right', px) })}
            onCommit={commit}
            onReset={() => resize({ right: initialSide(page, 'right') })}
          />
        )}
        {sideTile('right', right)}
        {rail('right', right)}
        {hasDock && (
          <div ref={dock} className={cls('work-dock', 'stage-zone', joined && `join-${joined}`, !hasRight && joined === 'center' && 'join-no-right')}>
            {joined === 'center' && (
              <>
                <i className="fillet l" aria-hidden />
                {hasRight && <i className="fillet r" aria-hidden />}
              </>
            )}
            {joined === 'right' && <i className="fillet l" aria-hidden />}
            {props.timeline && (
              <Splitter
                className="row"
                orientation="horizontal"
                label="调整时间轴高度"
                value={sizes.timeline ?? Math.round(timelineNow())}
                min={TIMELINE_LIMITS.min}
                max={TIMELINE_LIMITS.max}
                sign={-1}
                start={() => latest.current.timeline ?? timelineNow()}
                onChange={(px) => resize({ timeline: clampTimeline(px) })}
                onCommit={commit}
                onReset={() => resize({ timeline: undefined })}
              />
            )}
            <TimelineHeight.Provider value={sizes.timeline}>{props.dock}</TimelineHeight.Provider>
          </div>
        )}
      </div>
      {props.status}
      {props.children}
    </div>
  );
}

/**
 * 分隔条：role=separator，指针拖动、方向键（Shift 大步）、Home／End、双击恢复默认。
 * sign：指针往右（往下）移动时值是增（1）还是减（-1）。
 */
function Splitter(props: {
  className: string;
  orientation: 'vertical' | 'horizontal';
  label: string;
  value: number;
  min: number;
  max: number;
  sign: 1 | -1;
  start: () => number;
  onChange: (px: number) => void;
  onCommit: () => void;
  onReset: () => void;
}) {
  const drag = useRef<{ id: number; origin: number; start: number } | null>(null);
  const [dragging, setDragging] = useState(false);
  const vertical = props.orientation === 'vertical';
  const coord = (event: PointerEvent) => (vertical ? event.clientX : event.clientY);

  useEffect(() => {
    if (!dragging) return;
    document.documentElement.dataset.resizing = vertical ? 'col' : 'row';
    return () => {
      delete document.documentElement.dataset.resizing;
    };
  }, [dragging, vertical]);

  const end = () => {
    if (!drag.current) return;
    drag.current = null;
    setDragging(false);
    props.onCommit();
  };

  const onKeyDown = (event: KeyboardEvent) => {
    const step = event.shiftKey ? 40 : 8;
    const keys: Record<string, number> = vertical ? { ArrowLeft: -step, ArrowRight: step } : { ArrowUp: -step, ArrowDown: step };
    let next: number | undefined;
    if (event.key in keys) next = props.value + props.sign * keys[event.key]!;
    else if (event.key === 'Home') next = props.min;
    else if (event.key === 'End') next = props.max;
    if (next === undefined) return;
    event.preventDefault();
    props.onChange(next);
    props.onCommit();
  };

  return (
    <div
      className={cls('splitter', props.className, dragging && 'dragging')}
      role="separator"
      aria-orientation={props.orientation}
      aria-label={props.label}
      aria-valuenow={props.value}
      aria-valuemin={props.min}
      aria-valuemax={props.max}
      tabIndex={0}
      title="拖动调整，双击恢复默认"
      onPointerDown={(event) => {
        if (event.button !== 0) return;
        event.preventDefault();
        event.currentTarget.setPointerCapture(event.pointerId);
        drag.current = { id: event.pointerId, origin: coord(event), start: props.start() };
        setDragging(true);
      }}
      onPointerMove={(event) => {
        const d = drag.current;
        if (!d || d.id !== event.pointerId) return;
        props.onChange(d.start + props.sign * (coord(event) - d.origin));
      }}
      onPointerUp={end}
      onPointerCancel={end}
      onLostPointerCapture={end}
      onDoubleClick={() => {
        props.onReset();
        props.onCommit();
      }}
      onKeyDown={onKeyDown}
    />
  );
}
