import { createContext, useContext, useEffect, type CSSProperties, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Button as AriaButton } from 'react-aria-components';
import { TEMPLATE_WORKSHOP_SAMPLE_IDS, type TemplateLibraryId, type TemplateSummary } from '../../protocol';
import { SLOT_LABEL } from '../model/full-template';
import { Button } from '../ui/Button';
import { SliderField, Switch } from '../ui/choices';
import { Select } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { Tag } from '../ui/indicators';
import { useObjectUrl, useObservable } from '../ui/hooks';
import { Note } from '../workspace/panel';
import { SAMPLE_SIZE, thumbnailKey, type SampleFrame, type SampleSource, type TemplateThumbnails } from './template-thumbnails';
import { SAMPLE_LABEL, targetLabel, workshopTitle, WORKSHOP_DURATION_US, type TemplateWorkshop } from './template-workshop';

/** 模板缩略图（G17）：固定试样上的一帧；后端没有开放试样时不占位。 */
export function TemplateThumb(props: { thumbnails: TemplateThumbnails; libraryId: TemplateLibraryId; libraryRevision: string; summary: TemplateSummary }) {
  const { thumbnails, libraryId, libraryRevision, summary } = props;
  const state = useObservable(thumbnails.state$);
  const key = thumbnailKey(libraryId, summary);
  useEffect(() => thumbnails.request(libraryId, libraryRevision, summary), [thumbnails, key, libraryRevision]);
  if (state.available === false) return null;
  return <SampleImage frame={state.frames.get(key)} className="template-thumb" compact />;
}

/** 试样失败时画面下方的链接：只在失败编码对得上时显示（例如编译失败时去源码页签）。 */
export interface SampleFailureLink {
  code: string;
  label: string;
  onClick(): void;
}

/** 样式库的中栏（幕后区）：详情在右栏渲染，试样画布经 portal 放到中栏。 */
export const SampleStage = createContext<HTMLElement | null>(null);

/** 把内容放到样式库中栏；不在样式库页里时不显示。 */
export function OnStage(props: { children: ReactNode }) {
  const stage = useContext(SampleStage);
  return stage ? createPortal(props.children, stage) : null;
}

/** 试样画布并跟着来源走；source 为 undefined 时不换来源（草稿不完整时留着上一张）。画布放在中栏。 */
export function SampleArea(props: {
  workshop: TemplateWorkshop;
  source: SampleSource | null | undefined;
  scope: string;
  blocked: string | undefined;
  sourceNote?: string;
  failureLink?: SampleFailureLink;
}) {
  const { workshop, source, scope } = props;
  useEffect(() => {
    if (source !== undefined) workshop.show(source, scope);
  });
  return (
    <OnStage>
      <SampleView workshop={workshop} blocked={props.blocked} sourceNote={props.sourceNote} failureLink={props.failureLink} />
    </OnStage>
  );
}

const seconds = (us: number) => `${(us / 1_000_000).toFixed(2)} s`;

/**
 * 样式库中栏的试样画布（W7 设计 §2.5）：上面是标题、说明与样本、目标，中间是画面，下面是试播、时刻与循环。
 * 标明是试样、不是工程里的最终外观（整体设计 R5）。sourceNote 是标题后面的来源说明；不给时按草稿写“未保存的修改”。
 */
export function SampleView(props: { workshop: TemplateWorkshop; blocked?: string | undefined; sourceNote?: string | undefined; failureLink?: SampleFailureLink | undefined }) {
  const { workshop } = props;
  const state = useObservable(workshop.state$);
  if (state.available === false) return <Note className="sample-unavailable">后端没有开放模板试样，应用到工程后可在外观页看成片预览。</Note>;
  const samples = state.options?.samples ?? [];
  const targets = samples.find((s) => s.sampleId === state.sampleId)?.targets ?? [];
  // 作者材质草稿要选部位（W8 设计 §2.8）；模板的材质焦点跟着材质页签走。
  const slots = state.source === 'authorDraft' ? (state.options?.materialSlots ?? []) : [];
  const idle = !state.options || !!props.blocked;
  const link = props.failureLink;
  const note = props.sourceNote ?? (state.source === 'draft' ? '未保存的修改，' : '');
  return (
    <section className="template-sample" aria-label="试样" data-focus={state.echoedSlot} data-title={workshopTitle(state)}>
      <div className="canvasbar">
        <span className="cbt">试样</span>
        <span className="canvas-hint">{note}固定样本上的效果，不是工程里的最终外观</span>
        <span className="spacer" />
        {state.source !== 'authorDraft' && state.materialSlot && <Tag className="sample-focus">材质焦点：{SLOT_LABEL[state.materialSlot]}</Tag>}
        <Select
          label="样本"
          labelPlacement="inside"
          size="sm"
          className="sample-select"
          value={state.sampleId}
          options={TEMPLATE_WORKSHOP_SAMPLE_IDS.map((id) => {
            const usable = samples.some((s) => s.sampleId === id && s.targets.length > 0);
            return { value: id, label: `${SAMPLE_LABEL[id]}${state.options && !usable ? '（没有可用目标）' : ''}`, disabled: !!state.options && !usable };
          })}
          onChange={(id) => workshop.setSample(id)}
          disabled={idle}
        />
        <Select
          label="目标"
          labelPlacement="inside"
          size="sm"
          className="sample-target"
          value={state.targetKey ?? ''}
          options={targets.map((t) => ({ value: t.key, label: targetLabel(t.key) }))}
          onChange={(key) => workshop.setTarget(key)}
          disabled={idle || targets.length === 0}
        />
        {slots.length > 0 && (
          <Select
            label="材质部位"
            labelPlacement="inside"
            size="sm"
            className="sample-slot"
            value={state.materialSlot ?? ''}
            options={slots.map((s) => ({ value: s.slot, label: SLOT_LABEL[s.slot] }))}
            onChange={(slot) => slot && workshop.setMaterialSlot(slot)}
            disabled={idle}
          />
        )}
      </div>
      <div className="sample-stage">
        <SampleImage frame={state.frame} className="preview-frame" style={{ aspectRatio: `${SAMPLE_SIZE.width} / ${SAMPLE_SIZE.height}` }} blocked={props.blocked} />
      </div>
      {link && state.frame?.status === 'error' && state.frame.failureCode === link.code && (
        <div className="sample-failure">
          <Button variant="text" size="sm" icon="code" className="sample-failure-link" onPress={() => link.onClick()}>
            {link.label}
          </Button>
        </div>
      )}
      <div className="transport sample-bar">
        <AriaButton
          className="play sample-play"
          isDisabled={idle || !state.targetKey}
          onPress={() => (state.playing ? workshop.pause() : workshop.play())}
          aria-label={state.playing ? '暂停试播' : '试播'}
        >
          <Icon name={state.playing ? 'pause' : 'play_arrow'} filled />
        </AriaButton>
        <span className="transport-time">
          {seconds(state.timeUs)}
          <small>/ {seconds(WORKSHOP_DURATION_US)}</small>
        </span>
        <SliderField
          label="试样时刻"
          hideLabel
          className="sample-time"
          value={Math.round(state.timeUs / 1000)}
          min={0}
          max={WORKSHOP_DURATION_US / 1000}
          step={10}
          format={(ms) => seconds(ms * 1000)}
          onPreview={(ms) => workshop.seek(ms * 1000)}
          onCommit={(ms) => workshop.seek(ms * 1000)}
          disabled={idle || !state.targetKey}
        />
        <Switch label="循环" checked={state.loop} onChange={(loop) => workshop.setLoop(loop)} />
      </div>
    </section>
  );
}

function SampleImage(props: { frame: SampleFrame | undefined; className: string; compact?: boolean; style?: CSSProperties; blocked?: string | undefined }) {
  const { frame } = props;
  const url = useObjectUrl(frame?.frame);
  const overlay = props.blocked ?? overlayText(frame, !!props.compact);
  const tone = frame?.status === 'error' ? ' error' : frame?.status === 'unsupported' ? ' unsupported' : '';
  return (
    <span className={`${props.className}${tone}`} style={props.style} title={frame?.message} data-status={frame?.status} data-blocked={props.blocked ? '' : undefined}>
      {url && <img src={url} alt={props.compact ? '' : '试样画面'} data-sha={frame?.sha256} data-time={frame?.timeUs} />}
      {overlay && <span className="preview-overlay">{overlay}</span>}
    </span>
  );
}

function overlayText(frame: SampleFrame | undefined, compact: boolean): string | undefined {
  switch (frame?.status) {
    case undefined:
      return undefined;
    case 'loading':
      return compact || frame.frame ? undefined : '正在生成试样……';
    case 'ready':
      return undefined;
    case 'unsupported':
      return frame.message;
    case 'error':
      return compact ? '试样失败' : frame.message;
  }
}
