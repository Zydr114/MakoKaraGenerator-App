import type { ReactNode } from 'react';
import {
  MAX_STROKE_LAYERS,
  type SceneTextAppearance,
  type StaticMaterial,
  type StyleGlow,
  type StyleShadow,
  type StyleStroke,
  type StyleTypography,
} from '../../protocol';
import { DEFAULT_SCENE_TEXT_APPEARANCE, toStaticSceneAppearance } from '../model/style-overrides';
import {
  DEFAULT_GLOW,
  DEFAULT_SHADOW,
  DEFAULT_STROKE,
  addStrokeLayer,
  gradientFrom,
  moveStrokeLayer,
  removeStrokeLayer,
  solidFrom,
  strokeMoveProblem,
} from '../model/template-groups';
import { FallbackFonts, FontCheck, FontPicker } from '../library/FontFields';
import { GradientFields, InlineSlider, StaticMaterialValue } from '../library/TemplateEditor';
import { Button, IconButton } from '../ui/Button';
import { ColorField, NumberField, Select } from '../ui/fields';
import { Group, Note, ToolButton } from '../workspace/panel';
import '../library/library.css';

const JOIN_OPTIONS: readonly { value: StyleStroke['join']; label: string }[] = [
  { value: 'round', label: '圆角' },
  { value: 'miter', label: '尖角' },
  { value: 'bevel', label: '斜角' },
];

const FACE_OPTIONS = [
  { value: 'regular', label: '常规' },
  { value: 'bold', label: '粗体' },
  { value: 'italic', label: '斜体' },
  { value: 'boldItalic', label: '粗斜体' },
] as const;

type Face = (typeof FACE_OPTIONS)[number]['value'];
const faceOf = (t: StyleTypography): Face => (t.isBold ? (t.isItalic ? 'boldItalic' : 'bold') : t.isItalic ? 'italic' : 'regular');

const STATIC_KINDS = [
  { value: 'solid', label: '纯色' },
  { value: 'gradient', label: '渐变' },
] as const;

const BINARY_STROKE_OPTIONS = [
  { value: 'on', label: '有描边' },
  { value: 'off', label: '不描边' },
] as const;

const FOLLOW_OPTIONS = [
  { value: 'follow', label: '跟随未到' },
  { value: 'on', label: '单独设置' },
  { value: 'off', label: '关闭' },
] as const;

const percent = (value: number) => `${Math.round(value * 100)}%`;

/** 前奏提示组独立双态外观编辑器（字形排版、待唱未到与已过两态、透明度，以及一键同步歌词配色）。 */
export function CountdownAppearanceEditor(props: {
  containerId: string;
  appearance: SceneTextAppearance | null | undefined;
  disabled: boolean;
  busy: boolean;
  canSyncLyric: boolean;
  onChange: (next: SceneTextAppearance<StaticMaterial>) => void;
  onSyncLyric: () => void;
}) {
  const { containerId, disabled, busy } = props;
  const locked = disabled || busy;
  const current = props.appearance ? toStaticSceneAppearance(props.appearance) : DEFAULT_SCENE_TEXT_APPEARANCE;
  const t = current.typography;
  const weights = [...new Set([100, 200, 300, 400, 500, 600, 700, 800, 900, t.fontWeight])].sort((a, b) => a - b);

  const commit = (patch: Partial<SceneTextAppearance<StaticMaterial>>) => props.onChange({ ...current, ...patch });
  const commitType = (patch: Partial<StyleTypography>) => commit({ typography: { ...t, ...patch } });
  const commitPending = (patch: Partial<SceneTextAppearance<StaticMaterial>['pending']>) => commit({ pending: { ...current.pending, ...patch } });
  const commitElapsed = (patch: Partial<SceneTextAppearance<StaticMaterial>['elapsed']>) => commit({ elapsed: { ...current.elapsed, ...patch } });

  const elapsedStrokesMode =
    current.elapsed.strokes.kind === 'follow' ? 'follow' : current.elapsed.strokes.value.length > 0 ? 'on' : 'off';
  const elapsedStrokes = current.elapsed.strokes.kind === 'set' ? current.elapsed.strokes.value : [];

  const elapsedShadowMode =
    current.elapsed.shadow.kind === 'follow' ? 'follow' : current.elapsed.shadow.value ? 'on' : 'off';
  const elapsedShadow = current.elapsed.shadow.kind === 'set' ? current.elapsed.shadow.value : null;

  const elapsedGlowMode =
    current.elapsed.glow.kind === 'follow' ? 'follow' : current.elapsed.glow.value ? 'on' : 'off';
  const elapsedGlow = current.elapsed.glow.kind === 'set' ? current.elapsed.glow.value : null;

  return (
    <div className="countdown-appearance" data-container-id={containerId}>
      <Group title="提示组外观" className="countdown-appearance-group">
        <div className="countdown-appearance-bar">
          <Button variant="tonal" size="sm" icon="autorenew" isDisabled={locked || !props.canSyncLyric} onPress={props.onSyncLyric}>
            同步当前歌词配色
          </Button>
        </div>
        <Note>提示组拥有独立的待唱/已过配色、描边与字体，不会隐式继承主歌词预设；需要一致配色时可随时同步。</Note>
      </Group>

      <Group title="字体与透明" className="countdown-appearance-group">
        <AppearanceRow label="字体">
          <FontPicker
            label="提示组字体"
            className="font-family"
            value={t.fontFamilyName}
            placeholder="后端默认字体"
            disabled={locked}
            onCommit={(fontFamilyName) => commitType({ fontFamilyName })}
          />
          <FallbackFonts names={t.fallbackFamilyNames} disabled={locked} onChange={(fallbackFamilyNames) => commitType({ fallbackFamilyNames })} />
        </AppearanceRow>
        <div className="g2">
          <NumberField
            label="字号"
            labelPlacement="inside"
            size="sm"
            suffix="px"
            value={t.fontSizePx}
            greaterThan={0}
            disabled={locked}
            onCommit={(fontSizePx) => commitType({ fontSizePx })}
          />
          <NumberField
            label="字距"
            labelPlacement="inside"
            size="sm"
            suffix="px"
            value={t.letterSpacingPx}
            disabled={locked}
            onCommit={(letterSpacingPx) => commitType({ letterSpacingPx })}
          />
        </div>
        <div className="g2">
          <Select
            label="字重"
            labelPlacement="inside"
            size="sm"
            value={String(t.fontWeight)}
            options={weights.map((w) => ({ value: String(w), label: w === 400 ? '常规（400）' : w === 700 ? '粗（700）' : String(w) }))}
            disabled={locked}
            onChange={(v) => commitType({ fontWeight: Number(v) })}
          />
          <Select<Face>
            label="字形"
            labelPlacement="inside"
            size="sm"
            value={faceOf(t)}
            options={FACE_OPTIONS}
            disabled={locked}
            onChange={(face) => commitType({ isBold: face === 'bold' || face === 'boldItalic', isItalic: face === 'italic' || face === 'boldItalic' })}
          />
        </div>
        <div className="ctl-row">
          <span className="sub-lbl">整体透明度</span>
          <InlineSlider label="提示组透明度" value={current.opacity} min={0} max={1} step={0.01} format={percent} disabled={locked} onCommit={(opacity) => commit({ opacity })} />
        </div>
        <FontCheck typography={t} />
      </Group>

      <Group title="未到（待唱状态）" className="countdown-appearance-group">
        <MaterialRow label="未到填充" material={current.pending.fill} disabled={locked} onChange={(fill) => commitPending({ fill })} />
        <AppearanceRow label="未到描边">
          <Select<'on' | 'off'>
            label="未到描边开关"
            labelPlacement="none"
            size="sm"
            className="w"
            value={current.pending.strokes.length > 0 ? 'on' : 'off'}
            options={BINARY_STROKE_OPTIONS}
            disabled={locked}
            onChange={(mode) => commitPending({ strokes: mode === 'off' ? [] : current.pending.strokes.length > 0 ? current.pending.strokes : [DEFAULT_STROKE] })}
          />
          {current.pending.strokes.length > 0 && (
            <StaticStrokeList
              label="未到描边"
              layers={current.pending.strokes}
              disabled={locked}
              onChange={(strokes) => commitPending({ strokes })}
            />
          )}
        </AppearanceRow>
        <ShadowEditor
          label="未到阴影"
          shadow={current.pending.shadow}
          follow={false}
          disabled={locked}
          onMode={(mode) => commitPending({ shadow: mode === 'on' ? current.pending.shadow ?? DEFAULT_SHADOW : null })}
          onChange={(shadow) => commitPending({ shadow })}
        />
        <GlowEditor
          label="未到辉光"
          glow={current.pending.glow}
          follow={false}
          disabled={locked}
          onMode={(mode) => commitPending({ glow: mode === 'on' ? current.pending.glow ?? DEFAULT_GLOW : null })}
          onChange={(glow) => commitPending({ glow })}
        />
      </Group>

      <Group title="已过（点亮 / 填充状态）" className="countdown-appearance-group">
        <MaterialRow label="已过填充" material={current.elapsed.fill} disabled={locked} onChange={(fill) => commitElapsed({ fill })} />
        <AppearanceRow label="已过描边">
          <Select<'follow' | 'on' | 'off'>
            label="已过描边开关"
            labelPlacement="none"
            size="sm"
            className="w"
            value={elapsedStrokesMode}
            options={FOLLOW_OPTIONS}
            disabled={locked}
            onChange={(mode) => {
              if (mode === 'follow') commitElapsed({ strokes: { kind: 'follow' } });
              else if (mode === 'off') commitElapsed({ strokes: { kind: 'set', value: [] } });
              else if (elapsedStrokesMode !== 'on') {
                commitElapsed({
                  strokes: {
                    kind: 'set',
                    value: current.pending.strokes.length > 0 ? [...current.pending.strokes] : [DEFAULT_STROKE],
                  },
                });
              }
            }}
          />
          {elapsedStrokesMode === 'on' && (
            <StaticStrokeList
              label="已过描边"
              layers={elapsedStrokes}
              disabled={locked}
              onChange={(value) => commitElapsed({ strokes: { kind: 'set', value } })}
            />
          )}
        </AppearanceRow>
        <ShadowEditor
          label="已过阴影"
          shadow={elapsedShadow}
          follow
          mode={elapsedShadowMode}
          disabled={locked}
          onMode={(mode) => {
            if (mode === 'follow') commitElapsed({ shadow: { kind: 'follow' } });
            else if (mode === 'off') commitElapsed({ shadow: { kind: 'set', value: null } });
            else if (elapsedShadowMode !== 'on') commitElapsed({ shadow: { kind: 'set', value: current.pending.shadow ?? DEFAULT_SHADOW } });
          }}
          onChange={(value) => commitElapsed({ shadow: { kind: 'set', value } })}
        />
        <GlowEditor
          label="已过辉光"
          glow={elapsedGlow}
          follow
          mode={elapsedGlowMode}
          disabled={locked}
          onMode={(mode) => {
            if (mode === 'follow') commitElapsed({ glow: { kind: 'follow' } });
            else if (mode === 'off') commitElapsed({ glow: { kind: 'set', value: null } });
            else if (elapsedGlowMode !== 'on') commitElapsed({ glow: { kind: 'set', value: current.pending.glow ?? DEFAULT_GLOW } });
          }}
          onChange={(value) => commitElapsed({ glow: { kind: 'set', value } })}
        />
      </Group>
    </div>
  );
}

function AppearanceRow(props: { label: string; children: ReactNode }) {
  return (
    <div className="countdown-field">
      <span className="sub-lbl">{props.label}</span>
      <div className="countdown-field-body">{props.children}</div>
    </div>
  );
}

function MaterialRow(props: { label: string; material: StaticMaterial; disabled: boolean; onChange: (material: StaticMaterial) => void }) {
  const { label, material, disabled, onChange } = props;
  return (
    <AppearanceRow label={label}>
      <div className="countdown-ctl-line">
        <Select
          label={`${label}类型`}
          labelPlacement="none"
          size="sm"
          className="w"
          value={material.kind}
          options={STATIC_KINDS}
          disabled={disabled}
          onChange={(kind) => onChange(kind === 'gradient' && material.kind === 'solid' ? gradientFrom(material.color) : solidFrom(material))}
        />
        <StaticMaterialValue label={label} material={material} onChange={onChange} disabled={disabled} compact />
      </div>
      {material.kind === 'gradient' && <GradientFields gradient={material.gradient} onChange={(gradient) => onChange({ kind: 'gradient', gradient })} disabled={disabled} />}
    </AppearanceRow>
  );
}

function StaticStrokeList(props: {
  label: string;
  layers: StyleStroke<StaticMaterial>[];
  disabled: boolean;
  onChange: (layers: StyleStroke<StaticMaterial>[]) => void;
}) {
  const { label, layers, disabled, onChange } = props;
  const set = (i: number, layer: StyleStroke<StaticMaterial>) => onChange(layers.map((l, j) => (j === i ? layer : l)));
  return (
    <div className="stroke-layers" role="group" aria-label={label}>
      {layers.map((layer, i) => {
        const name = `${label}第 ${i + 1} 层`;
        const material = layer.material;
        return (
          <div key={i} className="stroke-layer">
            <div className="stroke-layer-head">
              <span className="sub-lbl">第 {i + 1} 层</span>
              <Select
                label={`${name}类型`}
                labelPlacement="none"
                size="sm"
                className="w"
                value={material.kind}
                options={STATIC_KINDS}
                disabled={disabled}
                onChange={(kind) => set(i, { ...layer, material: kind === 'gradient' && material.kind === 'solid' ? gradientFrom(material.color) : solidFrom(material) })}
              />
              <StaticMaterialValue label={`${name}颜色`} material={material} onChange={(m) => set(i, { ...layer, material: m })} disabled={disabled} compact />
            </div>
            <div className="stroke-layer-shape">
              <NumberField
                label="宽度"
                labelPlacement="inside"
                size="sm"
                suffix="px"
                value={layer.widthPx}
                greaterThan={0}
                disabled={disabled}
                onCommit={(widthPx) => set(i, { ...layer, widthPx })}
              />
              <Select label="转角" labelPlacement="inside" size="sm" value={layer.join} options={JOIN_OPTIONS} disabled={disabled} onChange={(join) => set(i, { ...layer, join })} />
              <ToolButton
                icon="arrow_upward"
                label={`${name}向内移`}
                reason={strokeMoveProblem(layers, i, -1)}
                disabled={disabled}
                onPress={() => onChange(moveStrokeLayer(layers, i, -1) as StyleStroke<StaticMaterial>[])}
              />
              <ToolButton
                icon="arrow_downward"
                label={`${name}向外移`}
                reason={strokeMoveProblem(layers, i, 1)}
                disabled={disabled}
                onPress={() => onChange(moveStrokeLayer(layers, i, 1) as StyleStroke<StaticMaterial>[])}
              />
              <IconButton icon="delete" size="sm" label={`删除${name}`} isDisabled={disabled} onPress={() => onChange(removeStrokeLayer(layers, i) as StyleStroke<StaticMaterial>[])} />
            </div>
            {material.kind === 'gradient' && <GradientFields gradient={material.gradient} onChange={(gradient) => set(i, { ...layer, material: { kind: 'gradient', gradient } })} disabled={disabled} />}
          </div>
        );
      })}
      <div className="stroke-layer-actions">
        <Button
          variant="text"
          size="sm"
          icon="add"
          isDisabled={disabled || layers.length >= MAX_STROKE_LAYERS}
          onPress={() => onChange(addStrokeLayer(layers) as StyleStroke<StaticMaterial>[])}
        >
          加一层外圈
        </Button>
      </div>
    </div>
  );
}

const BINARY_EFFECT_OPTIONS = [
  { value: 'off', label: '关闭' },
  { value: 'on', label: '开启' },
] as const;

function ShadowEditor(props: {
  label: string;
  shadow: StyleShadow | null;
  follow: boolean;
  mode?: 'follow' | 'on' | 'off';
  disabled: boolean;
  onMode: (mode: 'follow' | 'on' | 'off') => void;
  onChange: (next: StyleShadow) => void;
}) {
  const value = props.mode ?? (props.shadow ? 'on' : 'off');
  const editable = value === 'on' ? props.shadow : null;
  const material: StaticMaterial | null = editable
    ? editable.gradient
      ? { kind: 'gradient', gradient: editable.gradient }
      : { kind: 'solid', color: editable.color }
    : null;
  const applyMaterial = (next: StaticMaterial) => {
    if (!editable) return;
    if (next.kind === 'solid') props.onChange({ ...editable, color: next.color, gradient: null });
    else props.onChange({ ...editable, color: next.gradient.stops[0]?.color ?? editable.color, gradient: next.gradient });
  };
  return (
    <AppearanceRow label={props.label}>
      <div className="countdown-ctl-line">
        <Select
          label={`${props.label}开关`}
          labelPlacement="none"
          size="sm"
          className="w"
          value={value}
          options={props.follow ? FOLLOW_OPTIONS : BINARY_EFFECT_OPTIONS}
          disabled={props.disabled}
          onChange={props.onMode}
        />
        {material && (
          <StaticMaterialValue
            label={`${props.label}颜色`}
            material={material}
            disabled={props.disabled}
            onChange={applyMaterial}
            compact
          />
        )}
      </div>
      {editable && material && (
        <>
          <div className="ctl-row">
            <span className="sub-lbl">着色</span>
            <Select
              label={`${props.label}着色类型`}
              labelPlacement="none"
              size="sm"
              className="w"
              value={material.kind}
              options={STATIC_KINDS}
              disabled={props.disabled}
              onChange={(kind) => applyMaterial(kind === 'gradient' && material.kind === 'solid' ? gradientFrom(material.color) : solidFrom(material))}
            />
          </div>
          {editable.gradient && (
            <GradientFields
              gradient={editable.gradient}
              disabled={props.disabled}
              onChange={(gradient) => props.onChange({ ...editable, gradient })}
            />
          )}
          <div className="ctl-row">
            <span className="sub-lbl">不透明度</span>
            <InlineSlider label={`${props.label}不透明度`} value={editable.opacity} min={0} max={1} step={0.01} format={percent} disabled={props.disabled} onCommit={(opacity) => props.onChange({ ...editable, opacity })} />
          </div>
          <div className="g2">
            <NumberField label="角度" labelPlacement="inside" size="sm" suffix="°" value={editable.angleDegrees} disabled={props.disabled} onCommit={(angleDegrees) => props.onChange({ ...editable, angleDegrees })} />
            <NumberField label="距离" labelPlacement="inside" size="sm" suffix="px" value={editable.distancePx} min={0} disabled={props.disabled} onCommit={(distancePx) => props.onChange({ ...editable, distancePx })} />
          </div>
          <div className="g2">
            <NumberField label="扩张" labelPlacement="inside" size="sm" suffix="px" value={editable.spreadPx} min={0} disabled={props.disabled} onCommit={(spreadPx) => props.onChange({ ...editable, spreadPx })} />
            <NumberField label="模糊" labelPlacement="inside" size="sm" suffix="px" value={editable.blurRadiusPx} min={0} disabled={props.disabled} onCommit={(blurRadiusPx) => props.onChange({ ...editable, blurRadiusPx })} />
          </div>
        </>
      )}
    </AppearanceRow>
  );
}

function GlowEditor(props: {
  label: string;
  glow: StyleGlow | null;
  follow: boolean;
  mode?: 'follow' | 'on' | 'off';
  disabled: boolean;
  onMode: (mode: 'follow' | 'on' | 'off') => void;
  onChange: (next: StyleGlow) => void;
}) {
  const value = props.mode ?? (props.glow ? 'on' : 'off');
  const editable = value === 'on' ? props.glow : null;
  return (
    <AppearanceRow label={props.label}>
      <div className="countdown-ctl-line">
        <Select
          label={`${props.label}开关`}
          labelPlacement="none"
          size="sm"
          className="w"
          value={value}
          options={props.follow ? FOLLOW_OPTIONS : BINARY_EFFECT_OPTIONS}
          disabled={props.disabled}
          onChange={props.onMode}
        />
        {editable && (
          <ColorField
            label={`${props.label}颜色`}
            labelPlacement="none"
            size="sm"
            value={editable.color}
            disabled={props.disabled}
            onCommit={(color) => props.onChange({ ...editable, color })}
          />
        )}
      </div>
      {editable && (
        <>
          <div className="ctl-row">
            <span className="sub-lbl">不透明度</span>
            <InlineSlider label={`${props.label}不透明度`} value={editable.opacity} min={0} max={1} step={0.01} format={percent} disabled={props.disabled} onCommit={(opacity) => props.onChange({ ...editable, opacity })} />
          </div>
          <div className="g2">
            <NumberField label="扩张" labelPlacement="inside" size="sm" suffix="px" value={editable.spreadPx} min={0} disabled={props.disabled} onCommit={(spreadPx) => props.onChange({ ...editable, spreadPx })} />
            <NumberField label="模糊" labelPlacement="inside" size="sm" suffix="px" value={editable.blurRadiusPx} min={0} disabled={props.disabled} onCommit={(blurRadiusPx) => props.onChange({ ...editable, blurRadiusPx })} />
          </div>
        </>
      )}
    </AppearanceRow>
  );
}
