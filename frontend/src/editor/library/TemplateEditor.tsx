// 模板编辑（W3 设计 §2.5）：外观页签的分组表单。每组标题是“包含在模板中”开关，组内是取值；
// 每次提交给 onChange 一份完整的新定义，由样式库状态记成草稿的一步历史。
// 用了效果包材质的部位只写一句说明，在“材质”页签编辑（W7 设计 §2.3）。

import type { ReactNode } from 'react';
import type {
  FullTemplateDefinition,
  FullTemplateMaterial,
  FullTemplateStyle,
  HorizontalAlignment,
  StaticMaterial,
  StyleGradient,
  StyleStroke,
  StyleTypography,
  TemplateKind,
  TemplateProperty,
} from '../../protocol';
import { addStop, gradientProblem, MAX_GRADIENT_STOPS, removeStop, setStopColor, setStopOffset } from '../model/gradient';
import {
  changeKind,
  DEFAULT_SHADOW,
  DEFAULT_STROKE,
  gradientFrom,
  groupAllowed,
  groupIncluded,
  KIND_LIMIT_TEXT,
  materialCss,
  materialText,
  setGroupIncluded,
  solidFrom,
  TEMPLATE_GROUPS,
  type TemplateGroup,
} from '../model/template-groups';
import { ColorField, CommitField, NumberField, Select, SliderField, Switch } from '../ui/legacy/fields';
import { FallbackFonts, FontCheck, FontPicker } from './FontFields';
import { checkTemplateName, KIND_TEXT } from './template-library-store';

const KIND_OPTIONS = (['line', 'token', 'composite'] as const).map((value) => ({ value, label: KIND_TEXT[value] }));
const ALIGN_OPTIONS: readonly { value: HorizontalAlignment; label: string }[] = [
  { value: 'left', label: '居左' },
  { value: 'center', label: '居中' },
  { value: 'right', label: '居右' },
];
const JOIN_OPTIONS: readonly { value: StyleStroke['join']; label: string }[] = [
  { value: 'round', label: '圆角' },
  { value: 'miter', label: '尖角' },
  { value: 'bevel', label: '斜角' },
];
const MATERIAL_OPTIONS = [
  { value: 'solid', label: '纯色' },
  { value: 'gradient', label: '渐变' },
] as const;

const COLOR_HELPER = '#RRGGBB 或 #RRGGBBAA';
const percent = (value: number) => `${Math.round(value * 100)}%`;

export function TemplateEditor(props: {
  definition: FullTemplateDefinition;
  /** 原定义的属性：重新打开原来就包含的组时恢复原有子集。 */
  baseProperties: readonly TemplateProperty[];
  onChange: (next: FullTemplateDefinition) => void;
  disabled?: boolean;
}) {
  const { definition: d, onChange } = props;
  const disabled = !!props.disabled;
  const limit = KIND_LIMIT_TEXT[d.kind];
  return (
    <div className="template-editor">
      <div className="field-grid">
        <CommitField label="名称" value={d.name} validate={checkTemplateName} onCommit={(name) => onChange({ ...d, name: name.trim() })} disabled={disabled} />
        <Select<TemplateKind> label="用途" value={d.kind} options={KIND_OPTIONS} onChange={(kind) => onChange(changeKind(d, kind))} disabled={disabled} />
      </div>
      {limit && <p className="hint">{limit}换用途时会去掉不允许的组。</p>}
      {TEMPLATE_GROUPS.filter((g) => groupAllowed(g, d.kind)).map((g) => {
        const included = groupIncluded(g, d.properties);
        return (
          <section key={g.id} className={included ? 'template-section' : 'template-section excluded'} data-group={g.id} aria-label={g.label}>
            <div className="template-section-header">
              <span className="template-section-title">{g.label}</span>
              <Switch label="包含在模板中" checked={included} disabled={disabled} onChange={(on) => onChange(setGroupIncluded(d, g, on, props.baseProperties))} />
            </div>
            {included ? <GroupFields group={g} definition={d} onChange={onChange} disabled={disabled} /> : <p className="hint">不包含：应用后这组由目标继承。</p>}
          </section>
        );
      })}
    </div>
  );
}

function GroupFields(props: { group: TemplateGroup; definition: FullTemplateDefinition; onChange: (next: FullTemplateDefinition) => void; disabled: boolean }) {
  const { definition: d, disabled } = props;
  const style = d.style;
  const t = style.typography;
  const setStyle = (patch: Partial<FullTemplateStyle>) => props.onChange({ ...d, style: { ...style, ...patch } });
  const setType = (patch: Partial<StyleTypography>) => setStyle({ typography: { ...t, ...patch } });

  switch (props.group.id) {
    case 'font':
      return (
        <>
          <Fields>
            <FontPicker
              label="字体"
              className="font-family"
              value={t.fontFamilyName}
              placeholder="后端默认字体"
              helper="从后端字体目录选，或输入名称；留空使用后端默认字体"
              onCommit={(fontFamilyName) => setType({ fontFamilyName })}
              disabled={disabled}
            />
          </Fields>
          <FallbackFonts names={t.fallbackFamilyNames} onChange={(fallbackFamilyNames) => setType({ fallbackFamilyNames })} disabled={disabled} />
          <FontCheck typography={t} />
        </>
      );
    case 'size':
      return (
        <Fields>
          <NumberField label="字号" suffix="px" value={t.fontSizePx} greaterThan={0} onCommit={(fontSizePx) => setType({ fontSizePx })} disabled={disabled} />
        </Fields>
      );
    case 'weight': {
      const weights = [...new Set([100, 200, 300, 400, 500, 600, 700, 800, 900, t.fontWeight])].sort((a, b) => a - b);
      return (
        <Fields>
          <Select
            label="字重"
            value={String(t.fontWeight)}
            options={weights.map((w) => ({ value: String(w), label: w === 400 ? '400（常规）' : w === 700 ? '700（粗）' : String(w) }))}
            onChange={(v) => setType({ fontWeight: Number(v) })}
            disabled={disabled}
          />
          <Switch label="粗体" checked={t.isBold} onChange={(isBold) => setType({ isBold })} disabled={disabled} />
          <Switch label="斜体" checked={t.isItalic} onChange={(isItalic) => setType({ isItalic })} disabled={disabled} />
        </Fields>
      );
    }
    case 'spacing':
      return (
        <Fields>
          <NumberField label="字距" suffix="px" value={t.letterSpacingPx} onCommit={(letterSpacingPx) => setType({ letterSpacingPx })} disabled={disabled} />
        </Fields>
      );
    case 'position':
      return (
        <Fields>
          <SliderField label="垂直中心" value={style.verticalCenterRatio} min={0} max={1} step={0.01} format={percent} onCommit={(verticalCenterRatio) => setStyle({ verticalCenterRatio })} disabled={disabled} />
          <Select label="水平对齐" value={style.horizontalAlignment} options={ALIGN_OPTIONS} onChange={(horizontalAlignment) => setStyle({ horizontalAlignment })} disabled={disabled} />
        </Fields>
      );
    case 'unsung':
      return <MaterialFields label="未唱颜色" material={style.unsungFill.material} onChange={(material) => setStyle({ unsungFill: { material } })} disabled={disabled} />;
    case 'sung':
      return <MaterialFields label="已唱颜色" material={style.sungFill.material} onChange={(material) => setStyle({ sungFill: { material } })} disabled={disabled} />;
    case 'stroke': {
      const stroke = style.stroke;
      return (
        <>
          <Switch label="描边：开" checked={!!stroke} onChange={(on) => setStyle({ stroke: on ? DEFAULT_STROKE : null })} disabled={disabled} />
          {stroke ? (
            <>
              <Fields>
                <NumberField label="宽度" suffix="px" value={stroke.widthPx} min={0} onCommit={(widthPx) => setStyle({ stroke: { ...stroke, widthPx } })} disabled={disabled} />
                <Select label="转角" value={stroke.join} options={JOIN_OPTIONS} onChange={(join) => setStyle({ stroke: { ...stroke, join } })} disabled={disabled} />
              </Fields>
              <MaterialFields label="描边颜色" material={stroke.material} onChange={(material) => setStyle({ stroke: { ...stroke, material } })} disabled={disabled} />
            </>
          ) : (
            <p className="hint">模板会明确关闭描边（不是继承）。</p>
          )}
        </>
      );
    }
    case 'shadow': {
      const shadow = style.shadow;
      return (
        <>
          <Switch label="阴影：开" checked={!!shadow} onChange={(on) => setStyle({ shadow: on ? DEFAULT_SHADOW : null })} disabled={disabled} />
          {shadow ? (
            <Fields>
              <ColorField helper={COLOR_HELPER} label="阴影颜色" value={shadow.color} onCommit={(color) => setStyle({ shadow: { ...shadow, color } })} disabled={disabled} />
              <SliderField label="不透明度" value={shadow.opacity} min={0} max={1} step={0.01} format={percent} onCommit={(opacity) => setStyle({ shadow: { ...shadow, opacity } })} disabled={disabled} />
              <NumberField label="角度" suffix="°" value={shadow.angleDegrees} onCommit={(angleDegrees) => setStyle({ shadow: { ...shadow, angleDegrees } })} disabled={disabled} />
              <NumberField label="距离" suffix="px" value={shadow.distancePx} min={0} onCommit={(distancePx) => setStyle({ shadow: { ...shadow, distancePx } })} disabled={disabled} />
              <NumberField label="模糊" suffix="px" value={shadow.blurRadiusPx} min={0} onCommit={(blurRadiusPx) => setStyle({ shadow: { ...shadow, blurRadiusPx } })} disabled={disabled} />
            </Fields>
          ) : (
            <p className="hint">模板会明确关闭阴影（不是继承）。</p>
          )}
        </>
      );
    }
    case 'opacity':
      return (
        <Fields>
          <SliderField label="透明度" value={style.opacity} min={0} max={1} step={0.01} format={percent} onCommit={(opacity) => setStyle({ opacity })} disabled={disabled} />
        </Fields>
      );
    case 'scale':
      return (
        <Fields>
          <NumberField label="缩放" suffix="倍" value={style.scale} greaterThan={0} onCommit={(scale) => setStyle({ scale })} disabled={disabled} />
        </Fields>
      );
  }
}

/** 填充或描边的材质：纯色用取色器；渐变先显示色标条和摘要；效果包材质在“材质”页签编辑。 */
function MaterialFields(props: { label: string; material: FullTemplateMaterial; onChange: (material: FullTemplateMaterial) => void; disabled: boolean }) {
  const { material, disabled } = props;
  if (material.kind === 'package') {
    return (
      <p className="hint" data-package-material>
        <span className="swatch" style={{ background: materialCss(material) }} />
        使用效果包材质 · 在“材质”页签编辑
      </p>
    );
  }
  return (
    <Fields>
      <Select
        label="材质"
        value={material.kind}
        options={MATERIAL_OPTIONS}
        onChange={(kind) => props.onChange(kind === 'gradient' && material.kind === 'solid' ? gradientFrom(material.color) : solidFrom(material))}
        disabled={disabled}
      />
      <StaticMaterialValue label={props.label} material={material} onChange={props.onChange} disabled={disabled} />
    </Fields>
  );
}

/** 纯色或渐变的取值（不含材质类型的选择），外观页签和材质页签共用。 */
export function StaticMaterialValue(props: { label: string; material: StaticMaterial; onChange: (material: StaticMaterial) => void; disabled: boolean }) {
  const { material, disabled } = props;
  return material.kind === 'solid' ? (
    <ColorField helper={COLOR_HELPER} label={props.label} value={material.color} onCommit={(color) => props.onChange({ kind: 'solid', color })} disabled={disabled} />
  ) : (
    <GradientFields gradient={material.gradient} onChange={(gradient) => props.onChange({ kind: 'gradient', gradient })} disabled={disabled} />
  );
}

const GRADIENT_KIND_OPTIONS: readonly { value: StyleGradient['kind']; label: string }[] = [
  { value: 'linear', label: '线性' },
  { value: 'radial', label: '径向' },
];
// 这三个取值能完整保存（R2 已在 1.13.0 修复），但后端渲染器还没实现：成片里按纯色基色绘制（后端 GradientSupport）。
const PENDING_TEXT = '（成片暂按纯色绘制）';
const SPACE_OPTIONS: readonly { value: StyleGradient['coordinateSpace']; label: string }[] = [
  { value: 'line', label: '整行' },
  { value: 'canvas', label: '整个画布' },
  { value: 'glyph', label: `单个字形${PENDING_TEXT}` },
  { value: 'progress', label: `演唱进度${PENDING_TEXT}` },
];
const COLOR_SPACE_OPTIONS: readonly { value: StyleGradient['colorSpace']; label: string }[] = [
  { value: 'linearRgb', label: '线性 RGB' },
  { value: 'hsv', label: 'HSV' },
  { value: 'oklch', label: `OKLCH${PENDING_TEXT}` },
];

/** 后端渲染器还没实现的组合：模板照常保存，成片里按纯色基色绘制。 */
function renderPending(g: StyleGradient): boolean {
  return g.coordinateSpace === 'glyph' || g.coordinateSpace === 'progress' || g.colorSpace === 'oklch';
}
const HUE_OPTIONS: readonly { value: StyleGradient['huePath']; label: string }[] = [
  { value: 'shorter', label: '较短路径' },
  { value: 'longer', label: '较长路径' },
  { value: 'increasing', label: '色相递增' },
  { value: 'decreasing', label: '色相递减' },
];

/**
 * 渐变：类型、角度或中心与半径、坐标空间、插值空间与色相路径、色标。
 * 上方的 CSS 渐变条只按色标从左到右显示颜色，不是渲染结果。
 */
function GradientFields(props: { gradient: StyleGradient; onChange: (gradient: StyleGradient) => void; disabled: boolean }) {
  const { gradient: g, disabled } = props;
  const set = (patch: Partial<StyleGradient>) => props.onChange({ ...g, ...patch });
  const problem = gradientProblem(g);
  const last = g.stops.length - 1;
  return (
    <div className="gradient-editor">
      <span className="gradient-bar" style={{ background: materialCss({ kind: 'gradient', gradient: g }) }} title={materialText({ kind: 'gradient', gradient: g })} />
      <div className="field-grid">
        <Select label="渐变类型" value={g.kind} options={GRADIENT_KIND_OPTIONS} onChange={(kind) => set({ kind })} disabled={disabled} />
        {g.kind === 'linear' ? (
          <NumberField label="角度" suffix="°" value={g.angleDegrees} onCommit={(angleDegrees) => set({ angleDegrees })} disabled={disabled} />
        ) : (
          <>
            <NumberField label="中心 X" value={g.centerX} min={0} max={1} onCommit={(centerX) => set({ centerX })} disabled={disabled} />
            <NumberField label="中心 Y" value={g.centerY} min={0} max={1} onCommit={(centerY) => set({ centerY })} disabled={disabled} />
            <NumberField label="半径 X" value={g.radiusX} greaterThan={0} onCommit={(radiusX) => set({ radiusX })} disabled={disabled} />
            <NumberField label="半径 Y" value={g.radiusY} greaterThan={0} onCommit={(radiusY) => set({ radiusY })} disabled={disabled} />
          </>
        )}
        <Select label="坐标空间" value={g.coordinateSpace} options={SPACE_OPTIONS} onChange={(coordinateSpace) => set({ coordinateSpace })} disabled={disabled} />
        <Select label="插值空间" value={g.colorSpace} options={COLOR_SPACE_OPTIONS} onChange={(colorSpace) => set({ colorSpace })} disabled={disabled} />
        <Select label="色相路径" value={g.huePath} options={HUE_OPTIONS} onChange={(huePath) => set({ huePath })} disabled={disabled || g.colorSpace === 'linearRgb'} />
      </div>
      {g.kind === 'radial' && <p className="hint">中心与半径是相对坐标空间的比例（0～1）。</p>}
      {g.colorSpace === 'linearRgb' && <p className="hint">色相路径只对 HSV、OKLCH 插值有效。</p>}
      {renderPending(g) && <p className="hint">渲染器还没实现这种组合，成片里暂按纯色绘制；模板会原样保存，渲染器支持后自动生效。</p>}
      <div className="gradient-stops" aria-label="色标">
        {g.stops.map((stop, i) => (
          <div key={i} className="gradient-stop" data-stop={i}>
            <NumberField
              label="位置"
              suffix="%"
              value={Math.round(stop.offset * 10000) / 100}
              min={0}
              max={100}
              onCommit={(v) => props.onChange(setStopOffset(g, i, v / 100))}
              disabled={disabled || i === 0 || i === last}
            />
            <ColorField helper={COLOR_HELPER} label={`色标 ${i + 1}`} value={stop.color} onCommit={(color) => props.onChange(setStopColor(g, i, color))} disabled={disabled} />
            <mdui-button-icon aria-label={`删除色标 ${i + 1}`} disabled={disabled || i === 0 || i === last} onClick={() => props.onChange(removeStop(g, i))}>
              <mdui-icon-delete />
            </mdui-button-icon>
          </div>
        ))}
      </div>
      <div className="gradient-actions">
        <mdui-button variant="text" disabled={disabled || g.stops.length >= MAX_GRADIENT_STOPS} onClick={() => props.onChange(addStop(g))}>
          <mdui-icon-add slot="icon" />
          添加色标
        </mdui-button>
        <span className="hint">首末色标固定在 0% 与 100%；中间的色标改位置后按位置重新排序。</span>
      </div>
      {problem && <p className="notice-error">{problem}</p>}
    </div>
  );
}

function Fields(props: { children: ReactNode }) {
  return <div className="field-grid">{props.children}</div>;
}
