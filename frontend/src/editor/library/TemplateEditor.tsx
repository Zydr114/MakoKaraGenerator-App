// 模板编辑（W3 设计 §2.5，界面改版 §3.6）：外观页签。一项一行：左边是名称，中间是取值，右边的开关决定这一项写不写进模板；
// 每次提交给 onChange 一份完整的新定义，由样式库状态记成草稿的一步历史。
// 用了效果包材质的部位只写一行链接，在“材质”页签编辑（W7 设计 §2.3）。
// 两态（合同 1.50.0）：未唱、已唱各有填充、描边、阴影、辉光，已唱的描边、阴影、辉光可跟随未唱；擦除的羽化与高亮两态共享。
// 外观按状态分组（2026-11-13 用户选定）：未唱、已唱各一组；描边是由内到外的层列表，最多 4 层，效果包材质只能在最内层。

import { useEffect, useState, type CSSProperties, type ReactNode } from 'react';
import {
  MAX_STROKE_LAYERS,
  type FullTemplateDefinition,
  type FullTemplateMaterial,
  type FullTemplateStyle,
  type HorizontalAlignment,
  type StaticMaterial,
  type StyleGradient,
  type StyleGlow,
  type StyleShadow,
  type StyleStroke,
  type StyleTypography,
  type StyleWipeHighlight,
  type StyleWipeHighlightBlend,
  type TemplateMaterialSlot,
  type TemplateProperty,
} from '../../protocol';
import { addStop, gradientProblem, MAX_GRADIENT_STOPS, removeStop, setStopColor, setStopOffset } from '../model/gradient';
import {
  DEFAULT_GLOW,
  DEFAULT_HIGHLIGHT,
  DEFAULT_SHADOW,
  DEFAULT_STROKE,
  addStrokeLayer,
  gradientFrom,
  groupAllowed,
  groupIncluded,
  isSungEffect,
  KIND_LIMIT_TEXT,
  materialCss,
  materialText,
  moveStrokeLayer,
  PACKAGE_INNER_TEXT,
  removeStrokeLayer,
  setGroupIncluded,
  setTemplateEffect,
  solidFrom,
  strokeMoveProblem,
  templateEffectEditing,
  TEMPLATE_GROUPS,
  type EffectGroupId,
  type TemplateGroup,
  type TemplateGroupId,
} from '../model/template-groups';
import { Button, IconButton } from '../ui/Button';
import { SliderField, Switch } from '../ui/choices';
import { cls } from '../ui/cls';
import { ColorField, CommitField, NumberField, Select } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { Group, Note, ToolButton } from '../workspace/panel';
import { FallbackFonts, FontCheck, FontPicker } from './FontFields';
import { checkTemplateName } from './template-library-store';

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
const BLEND_OPTIONS: readonly { value: StyleWipeHighlightBlend; label: string }[] = [
  { value: 'screen', label: '滤色' },
  { value: 'normal', label: '普通' },
  { value: 'add', label: '相加' },
];
/** 字形：粗体、斜体两个开关合成一个下拉。 */
const FACE_OPTIONS = [
  { value: 'regular', label: '常规' },
  { value: 'bold', label: '粗体' },
  { value: 'italic', label: '斜体' },
  { value: 'boldItalic', label: '粗斜体' },
] as const;
type Face = (typeof FACE_OPTIONS)[number]['value'];
const faceOf = (t: StyleTypography): Face => (t.isBold ? (t.isItalic ? 'boldItalic' : 'bold') : t.isItalic ? 'italic' : 'regular');

const percent = (value: number) => `${Math.round(value * 100)}%`;

/** 外观页签的分组，每组里按 TEMPLATE_GROUPS 的顺序列出用途允许的项。 */
const SECTIONS: readonly { title: string; ids: readonly TemplateGroupId[] }[] = [
  { title: '文字', ids: ['font', 'size', 'weight', 'spacing'] },
  { title: '未唱', ids: ['unsung', 'stroke', 'shadow', 'glow'] },
  { title: '已唱', ids: ['sung', 'sungStroke', 'sungShadow', 'sungGlow'] },
  { title: '擦除', ids: ['feather', 'highlight'] },
  { title: '位置与透明', ids: ['position', 'opacity', 'scale'] },
];
/** 行首的短名称（所在组已写明状态；完整名称作开关的可访问名称）。 */
const ROW_LABEL: Record<TemplateGroupId, string> = {
  font: '字体',
  size: '字号',
  weight: '字重',
  spacing: '字距',
  position: '位置',
  unsung: '填充',
  sung: '填充',
  stroke: '描边',
  sungStroke: '描边',
  shadow: '阴影',
  sungShadow: '阴影',
  glow: '辉光',
  sungGlow: '辉光',
  feather: '羽化',
  highlight: '高亮',
  opacity: '透明度',
  scale: '缩放',
};

export function TemplateEditor(props: {
  definition: FullTemplateDefinition;
  /** 原定义的属性：重新打开原来就包含的组时恢复原有子集。 */
  baseProperties: readonly TemplateProperty[];
  onChange: (next: FullTemplateDefinition) => void;
  /** 去材质页签编辑这个部位的效果包材质。 */
  onOpenMaterial: (slot: TemplateMaterialSlot) => void;
  disabled?: boolean;
}) {
  const { definition: d, onChange } = props;
  const disabled = !!props.disabled;
  const limit = KIND_LIMIT_TEXT[d.kind];
  const [fontCheck, setFontCheck] = useState(false);
  const fontGroup = TEMPLATE_GROUPS.find((g) => g.id === 'font')!;
  const canCheckFont = groupAllowed(fontGroup, d.kind);
  return (
    <div className="template-editor">
      <p className="pintro">
        右边的开关打开时，这一项写进模板；关掉时由目标继承。{limit && `${limit}换用途时会去掉不允许的项。`}
      </p>
      <Group title="模板" className="template-basics">
        <CommitField
          label="名称"
          labelPlacement="side"
          size="sm"
          className="template-name-field"
          value={d.name}
          validate={checkTemplateName}
          onCommit={(name) => onChange({ ...d, name: name.trim() })}
          disabled={disabled}
        />
      </Group>
      {SECTIONS.map((section) => {
        const groups = TEMPLATE_GROUPS.filter((g) => section.ids.includes(g.id) && groupAllowed(g, d.kind));
        if (groups.length === 0) return null;
        const text = section.ids.includes('font');
        return (
          <Group
            key={section.title}
            title={section.title}
            extra={
              text &&
              canCheckFont && (
                <Button variant="text" size="sm" icon="font_download" className="font-check-toggle" aria-expanded={fontCheck} onPress={() => setFontCheck(!fontCheck)}>
                  字体检查
                </Button>
              )
            }
          >
            {groups.map((g) => (
              <PropRow
                key={g.id}
                group={g}
                definition={d}
                included={groupIncluded(g, d.properties)}
                onInclude={(on) => onChange(setGroupIncluded(d, g, on, props.baseProperties))}
                onChange={onChange}
                onOpenMaterial={props.onOpenMaterial}
                disabled={disabled}
              />
            ))}
            {text && fontCheck && <FontCheck typography={d.style.typography} />}
          </Group>
        );
      })}
    </div>
  );
}

/** 一项一行：名称｜取值｜写进模板的开关；取值放不下的（回退字体、渐变、阴影参数）放在下面一行。 */
function PropRow(props: {
  group: TemplateGroup;
  definition: FullTemplateDefinition;
  included: boolean;
  onInclude: (on: boolean) => void;
  onChange: (next: FullTemplateDefinition) => void;
  onOpenMaterial: (slot: TemplateMaterialSlot) => void;
  disabled: boolean;
}) {
  const { group: g, included, disabled } = props;
  const row = included ? rowFields(g.id, props.definition, props.onChange, props.onOpenMaterial, disabled) : undefined;
  return (
    <div className={cls('prow', 'template-section', !included && 'off excluded')} data-group={g.id}>
      <span className="prow-lbl" title={g.label}>
        {ROW_LABEL[g.id]}
      </span>
      <div className="ctl">{row ? row.ctl : <span className="small inherit">由目标继承</span>}</div>
      <Switch label={`${g.label}写进模板`} hideLabel checked={included} disabled={disabled} onChange={props.onInclude} className="include" />
      {row?.sub && <div className="prow-sub">{row.sub}</div>}
    </div>
  );
}

function rowFields(
  id: TemplateGroupId,
  d: FullTemplateDefinition,
  onChange: (next: FullTemplateDefinition) => void,
  onOpenMaterial: (slot: TemplateMaterialSlot) => void,
  disabled: boolean,
): { ctl: ReactNode; sub?: ReactNode } {
  const style = d.style;
  const t = style.typography;
  const setStyle = (patch: Partial<FullTemplateStyle>) => onChange({ ...d, style: { ...style, ...patch } });
  const setType = (patch: Partial<StyleTypography>) => setStyle({ typography: { ...t, ...patch } });

  switch (id) {
    case 'font':
      return {
        ctl: (
          <FontPicker
            label="字体"
            className="font-family"
            value={t.fontFamilyName}
            placeholder="后端默认字体"
            onCommit={(fontFamilyName) => setType({ fontFamilyName })}
            disabled={disabled}
          />
        ),
        sub: <FallbackFonts names={t.fallbackFamilyNames} onChange={(fallbackFamilyNames) => setType({ fallbackFamilyNames })} disabled={disabled} />,
      };
    case 'size':
      return {
        ctl: <NumberField label="字号" labelPlacement="none" size="sm" className="w" suffix="px" value={t.fontSizePx} greaterThan={0} onCommit={(fontSizePx) => setType({ fontSizePx })} disabled={disabled} />,
      };
    case 'weight': {
      const weights = [...new Set([100, 200, 300, 400, 500, 600, 700, 800, 900, t.fontWeight])].sort((a, b) => a - b);
      return {
        ctl: (
          <>
            <Select
              label="字重"
              labelPlacement="none"
              size="sm"
              value={String(t.fontWeight)}
              options={weights.map((w) => ({ value: String(w), label: w === 400 ? '常规（400）' : w === 700 ? '粗（700）' : String(w) }))}
              onChange={(v) => setType({ fontWeight: Number(v) })}
              disabled={disabled}
            />
            <Select<Face>
              label="字形"
              labelPlacement="none"
              size="sm"
              className="w"
              value={faceOf(t)}
              options={FACE_OPTIONS}
              onChange={(face) => setType({ isBold: face === 'bold' || face === 'boldItalic', isItalic: face === 'italic' || face === 'boldItalic' })}
              disabled={disabled}
            />
          </>
        ),
      };
    }
    case 'spacing':
      return {
        ctl: <NumberField label="字距" labelPlacement="none" size="sm" className="w" suffix="px" value={t.letterSpacingPx} onCommit={(letterSpacingPx) => setType({ letterSpacingPx })} disabled={disabled} />,
      };
    case 'position':
      return {
        ctl: (
          <>
            <Select label="水平对齐" labelPlacement="none" size="sm" className="w" value={style.horizontalAlignment} options={ALIGN_OPTIONS} onChange={(horizontalAlignment) => setStyle({ horizontalAlignment })} disabled={disabled} />
            <InlineSlider label="垂直中心" value={style.verticalCenterRatio} min={0} max={1} step={0.01} format={percent} onCommit={(verticalCenterRatio) => setStyle({ verticalCenterRatio })} disabled={disabled} />
          </>
        ),
      };
    case 'unsung':
      return materialRow('未唱颜色', 'unsungFill', style.unsung.fill, (fill) => setStyle({ unsung: { ...style.unsung, fill } }), onOpenMaterial, disabled);
    case 'sung':
      return materialRow('已唱颜色', 'sungFill', style.sung.fill, (fill) => setStyle({ sung: { ...style.sung, fill } }), onOpenMaterial, disabled);
    case 'stroke':
      return strokeRow('未唱描边', style.unsung.strokes, (strokes) => strokes !== 'follow' && setStyle({ unsung: { ...style.unsung, strokes } }), { onOpenMaterial: () => onOpenMaterial('stroke') }, disabled);
    case 'sungStroke': {
      const value = style.sung.strokes.kind === 'follow' ? 'follow' : style.sung.strokes.value;
      const write = (strokes: StyleStroke<FullTemplateMaterial>[] | 'follow') =>
        setStyle({ sung: { ...style.sung, strokes: strokes === 'follow' ? { kind: 'follow' } : { kind: 'set', value: strokes } } });
      return strokeRow('已唱描边', value, write, { followFrom: style.unsung.strokes }, disabled);
    }
    case 'shadow':
    case 'sungShadow':
    case 'glow':
    case 'sungGlow':
    case 'highlight':
      return effectRow(d, id, onChange, disabled);
    case 'feather':
      return {
        ctl: (
          <NumberField
            label="羽化宽度"
            labelPlacement="none"
            size="sm"
            className="w"
            suffix="×字号"
            value={style.wipe.featherRatio}
            min={0}
            max={2}
            onCommit={(featherRatio) => setStyle({ wipe: { ...style.wipe, featherRatio } })}
            disabled={disabled}
          />
        ),
        sub: <Note>过渡带宽度按字号的倍数，0 是硬边。</Note>,
      };
    case 'opacity':
      return {
        ctl: <InlineSlider label="透明度" value={style.opacity} min={0} max={1} step={0.01} format={percent} onCommit={(opacity) => setStyle({ opacity })} disabled={disabled} />,
      };
    case 'scale':
      return {
        ctl: <NumberField label="缩放" labelPlacement="none" size="sm" className="w" suffix="倍" value={style.scale} greaterThan={0} onCommit={(scale) => setStyle({ scale })} disabled={disabled} />,
      };
  }
}

const STATIC_KINDS = [
  { value: 'solid', label: '纯色' },
  { value: 'gradient', label: '渐变' },
] as const;

/** 填充的材质：类型加颜色；渐变的参数和色标在下面一行；效果包材质只写一行链接，去材质页签编辑。 */
function materialRow(
  label: string,
  slot: TemplateMaterialSlot,
  material: FullTemplateMaterial,
  onChange: (material: FullTemplateMaterial) => void,
  onOpenMaterial: (slot: TemplateMaterialSlot) => void,
  disabled: boolean,
): { ctl: ReactNode; sub?: ReactNode } {
  if (material.kind === 'package') return { ctl: <PackageMaterialLink material={material} onOpen={() => onOpenMaterial(slot)} /> };
  return {
    ctl: (
      <>
        <Select
          label={`${label}的类型`}
          labelPlacement="none"
          size="sm"
          className="w"
          value={material.kind}
          options={STATIC_KINDS}
          onChange={(kind) => onChange(kind === 'gradient' && material.kind === 'solid' ? gradientFrom(material.color) : solidFrom(material))}
          disabled={disabled}
        />
        <StaticMaterialValue label={label} material={material} onChange={onChange} disabled={disabled} compact />
      </>
    ),
    ...(material.kind === 'gradient' ? { sub: <GradientFields gradient={material.gradient} onChange={(gradient) => onChange({ kind: 'gradient', gradient })} disabled={disabled} /> } : {}),
  };
}

function PackageMaterialLink(props: { material: Extract<FullTemplateMaterial, { kind: 'package' }>; onOpen?: () => void }) {
  if (!props.onOpen) {
    return (
      <span className="material-link" data-package-material title="已唱描边暂无工坊材质槽，不能在这里编辑">
        <Icon name="texture" />
        <span>{materialText(props.material)}</span>
      </span>
    );
  }
  return (
    <button type="button" className="material-link" data-package-material onClick={props.onOpen} title="在材质页签编辑">
      <Icon name="texture" />
      <span>{materialText(props.material)}</span>
      <Icon name="chevron_right" className="go" />
    </button>
  );
}

const FOLLOW_OPTION = { value: 'follow', label: '跟随未唱' } as const;
/** 已唱一侧的描边、阴影、辉光：跟随未唱放第一项（2026-11-13 用户选定）。 */
const SUNG_OPTIONS = [FOLLOW_OPTION, { value: 'on', label: '单独设置' }, { value: 'off', label: '关闭' }] as const;
const UNSUNG_STROKE_OPTIONS = [
  { value: 'on', label: '有描边' },
  { value: 'off', label: '不描边' },
] as const;

/**
 * 描边：未唱一侧“有描边／不描边”，已唱一侧“跟随未唱／单独设置／关闭”；“不描边”“关闭”是明确的空表，不是继承。
 * 开着时下面是层列表；已唱改为单独设置时从未唱的各层起步。
 */
function strokeRow(
  label: string,
  strokes: StyleStroke<FullTemplateMaterial>[] | 'follow',
  onChange: (strokes: StyleStroke<FullTemplateMaterial>[] | 'follow') => void,
  options: { followFrom?: StyleStroke<FullTemplateMaterial>[]; onOpenMaterial?: () => void },
  disabled: boolean,
): { ctl: ReactNode; sub?: ReactNode } {
  const sung = options.followFrom !== undefined;
  const layers = strokes === 'follow' ? [] : strokes;
  const mode = strokes === 'follow' ? 'follow' : layers.length > 0 ? 'on' : 'off';
  return {
    ctl: (
      <>
        <Select
          label={`${label}开关`}
          labelPlacement="none"
          size="sm"
          className="w"
          value={mode}
          options={sung ? SUNG_OPTIONS : UNSUNG_STROKE_OPTIONS}
          onChange={(next) => {
            if (next === 'follow') onChange('follow');
            else if (next === 'off') onChange([]);
            else if (mode !== 'on') onChange(options.followFrom?.length ? [...options.followFrom] : [DEFAULT_STROKE]);
          }}
          disabled={disabled}
        />
        <span className="small inherit">{mode === 'follow' ? '与未唱相同' : mode === 'off' ? '明确关闭，不是继承' : `共 ${layers.length} 层，由内到外`}</span>
      </>
    ),
    ...(mode === 'on' ? { sub: <StrokeLayerList label={label} layers={layers} onChange={onChange} onOpenMaterial={options.onOpenMaterial} disabled={disabled} /> } : {}),
  };
}

/**
 * 描边层列表：每层一块，上面一行是材质（类型与颜色，包材质只写链接），下面是宽度、转角与移动、删除，渐变参数再往下。
 * 包材质只能在最内层，移动会把它换到外圈时按钮停用并写明原因。onOpenMaterial 省略时（已唱描边没有工坊槽）包材质只显示。
 */
function StrokeLayerList(props: {
  label: string;
  layers: StyleStroke<FullTemplateMaterial>[];
  onChange: (layers: StyleStroke<FullTemplateMaterial>[]) => void;
  onOpenMaterial: (() => void) | undefined;
  disabled: boolean;
}) {
  const { label, layers, disabled } = props;
  const set = (i: number, layer: StyleStroke<FullTemplateMaterial>) => props.onChange(layers.map((l, j) => (j === i ? layer : l)));
  const packageInner = layers.length > 1 && layers[0]!.material.kind === 'package';
  return (
    <div className="stroke-layers" role="group" aria-label={`${label}各层`}>
      {layers.map((layer, i) => {
        const name = `${label}第 ${i + 1} 层`;
        const material = layer.material;
        return (
          <div key={i} className="stroke-layer" data-layer={i}>
            <div className="stroke-layer-head">
              <span className="sub-lbl">第 {i + 1} 层</span>
              {material.kind === 'package' ? (
                <PackageMaterialLink material={material} {...(props.onOpenMaterial ? { onOpen: props.onOpenMaterial } : {})} />
              ) : (
                <>
                  <Select
                    label={`${name}的类型`}
                    labelPlacement="none"
                    size="sm"
                    className="w"
                    value={material.kind}
                    options={STATIC_KINDS}
                    onChange={(kind) => set(i, { ...layer, material: kind === 'gradient' && material.kind === 'solid' ? gradientFrom(material.color) : solidFrom(material) })}
                    disabled={disabled}
                  />
                  <StaticMaterialValue label={`${name}颜色`} material={material} onChange={(m) => set(i, { ...layer, material: m })} disabled={disabled} compact />
                </>
              )}
            </div>
            <div className="stroke-layer-shape">
              <NumberField label="宽度" labelPlacement="inside" size="sm" suffix="px" value={layer.widthPx} greaterThan={0} onCommit={(widthPx) => set(i, { ...layer, widthPx })} disabled={disabled} />
              <Select label="转角" labelPlacement="inside" size="sm" value={layer.join} options={JOIN_OPTIONS} onChange={(join) => set(i, { ...layer, join })} disabled={disabled} />
              <ToolButton icon="arrow_upward" label={`${name}向内移`} reason={strokeMoveProblem(layers, i, -1)} disabled={disabled} onPress={() => props.onChange(moveStrokeLayer(layers, i, -1))} />
              <ToolButton icon="arrow_downward" label={`${name}向外移`} reason={strokeMoveProblem(layers, i, 1)} disabled={disabled} onPress={() => props.onChange(moveStrokeLayer(layers, i, 1))} />
              <IconButton icon="delete" size="sm" label={`删除${name}`} isDisabled={disabled} onPress={() => props.onChange(removeStrokeLayer(layers, i))} />
            </div>
            {material.kind === 'gradient' && <GradientFields gradient={material.gradient} onChange={(gradient) => set(i, { ...layer, material: { kind: 'gradient', gradient } })} disabled={disabled} />}
          </div>
        );
      })}
      <div className="stroke-layer-actions">
        <Button variant="text" size="sm" icon="add" isDisabled={disabled || layers.length >= MAX_STROKE_LAYERS} onPress={() => props.onChange(addStrokeLayer(layers))}>
          加一层外圈
        </Button>
        <span className="faint small">由内到外，最多 {MAX_STROKE_LAYERS} 层</span>
      </div>
      {packageInner && <Note>最内层用了效果包材质。{PACKAGE_INNER_TEXT}外圈只能用纯色与渐变。</Note>}
    </div>
  );
}

const EFFECT_TEXT: Record<'shadow' | 'glow' | 'highlight', { name: string; off: string; on: string; initial: StyleShadow | StyleGlow | StyleWipeHighlight }> = {
  shadow: { name: '阴影', off: '无阴影', on: '有阴影', initial: DEFAULT_SHADOW },
  glow: { name: '辉光', off: '无辉光', on: '有辉光', initial: DEFAULT_GLOW },
  highlight: { name: '高亮', off: '无高亮', on: '有高亮', initial: DEFAULT_HIGHLIGHT },
};

function effectKind(group: EffectGroupId): 'shadow' | 'glow' | 'highlight' {
  return group === 'shadow' || group === 'sungShadow' ? 'shadow' : group === 'glow' || group === 'sungGlow' ? 'glow' : 'highlight';
}

/**
 * 阴影、辉光（两态）与擦除高亮：右侧包含开关控制继承；这里的“无…”“关闭”写入显式 null（已唱一侧是 set null），
 * 已唱一侧的“跟随未唱”写入 follow。只含叶时是部分覆盖：改哪一项就把那一项写进模板。
 */
function effectRow(d: FullTemplateDefinition, group: EffectGroupId, onChange: (next: FullTemplateDefinition) => void, disabled: boolean): { ctl: ReactNode; sub?: ReactNode } {
  const kind = effectKind(group);
  const text = EFFECT_TEXT[kind];
  const { mode, value } = templateEffectEditing(d, group);
  const sungSide = isSungEffect(group);
  const prefix = group.startsWith('sung') ? '已唱' : group === 'highlight' ? '' : '未唱';
  const set = (next: StyleShadow | StyleGlow | StyleWipeHighlight | null | 'follow', leaf?: string) => onChange(setTemplateEffect(d, group, next as never, leaf as never));
  const options = [
    ...(mode === 'partial' ? [{ value: 'partial', label: '部分覆盖' }] : []),
    ...(sungSide
      ? SUNG_OPTIONS
      : [
          { value: 'off', label: text.off },
          { value: 'on', label: text.on },
        ]),
  ];
  const editable = value && mode !== 'follow' ? value : undefined;
  return {
    ctl: (
      <>
        <Select
          label={`${prefix}${text.name}开关`}
          labelPlacement="none"
          size="sm"
          className="w"
          value={mode}
          options={options}
          onChange={(v) => {
            // 已唱从跟随改为单独设置时，从当前生效的未唱取值起步。
            if (v === 'follow') set('follow');
            else if (v === 'on' && mode !== 'on') set(value ?? text.initial);
            else if (v === 'off') set(null);
          }}
          disabled={disabled}
        />
        {editable ? (
          <ColorField label={`${prefix}${text.name}颜色`} labelPlacement="none" size="sm" value={editable.color} onCommit={(color) => set({ ...editable, color }, 'color')} disabled={disabled} />
        ) : (
          <span className="small inherit">{mode === 'follow' ? '与未唱相同' : '明确关闭，不是继承'}</span>
        )}
      </>
    ),
    sub: editable && (
      <>
        {mode === 'partial' && <Note>只覆盖模板包含的{text.name}属性，其余从目标继承。修改下方数值会将对应属性写进模板。</Note>}
        {kind === 'shadow' && shadowFields(prefix, editable as StyleShadow, set, disabled)}
        {kind === 'glow' && glowFields(prefix, editable as StyleGlow, set, disabled, mode !== 'partial')}
        {kind === 'highlight' && highlightFields(editable as StyleWipeHighlight, set, disabled)}
      </>
    ),
  };
}

type EffectSetter = (next: StyleShadow | StyleGlow | StyleWipeHighlight, leaf: string) => void;

function shadowFields(prefix: string, shadow: StyleShadow, set: EffectSetter, disabled: boolean): ReactNode {
  return (
    <>
      <div className="ctl-row">
        <span className="sub-lbl">不透明度</span>
        <InlineSlider label={`${prefix}阴影不透明度`} value={shadow.opacity} min={0} max={1} step={0.01} format={percent} onCommit={(opacity) => set({ ...shadow, opacity }, 'opacity')} disabled={disabled} />
      </div>
      <div className="g3">
        <NumberField label="角度" labelPlacement="inside" size="sm" suffix="°" value={shadow.angleDegrees} onCommit={(angleDegrees) => set({ ...shadow, angleDegrees }, 'angleDegrees')} disabled={disabled} />
        <NumberField label="距离" labelPlacement="inside" size="sm" suffix="px" value={shadow.distancePx} min={0} onCommit={(distancePx) => set({ ...shadow, distancePx }, 'distancePx')} disabled={disabled} />
        <NumberField label="模糊" labelPlacement="inside" size="sm" suffix="px" value={shadow.blurRadiusPx} min={0} onCommit={(blurRadiusPx) => set({ ...shadow, blurRadiusPx }, 'blurRadiusPx')} disabled={disabled} />
      </div>
    </>
  );
}

function glowFields(prefix: string, glow: StyleGlow, set: EffectSetter, disabled: boolean, whole: boolean): ReactNode {
  return (
    <>
      <div className="ctl-row">
        <span className="sub-lbl">不透明度</span>
        <InlineSlider label={`${prefix}辉光不透明度`} value={glow.opacity} min={0} max={1} step={0.01} format={percent} onCommit={(opacity) => set({ ...glow, opacity }, 'opacity')} disabled={disabled} />
      </div>
      <div className="g2">
        <NumberField label="扩张" labelPlacement="inside" size="sm" suffix="px" value={glow.spreadPx} min={0} onCommit={(spreadPx) => set({ ...glow, spreadPx }, 'spreadPx')} disabled={disabled} />
        <NumberField label="模糊" labelPlacement="inside" size="sm" suffix="px" value={glow.blurRadiusPx} min={0} onCommit={(blurRadiusPx) => set({ ...glow, blurRadiusPx }, 'blurRadiusPx')} disabled={disabled} />
      </div>
      <Note>扩张增加光晕厚度，模糊使边缘更柔和。{whole && '两项都为 0 时没有光晕。'}</Note>
    </>
  );
}

function highlightFields(highlight: StyleWipeHighlight, set: EffectSetter, disabled: boolean): ReactNode {
  return (
    <>
      <div className="ctl-row">
        <span className="sub-lbl">强度</span>
        <InlineSlider label="高亮强度" value={highlight.opacity} min={0} max={1} step={0.01} format={percent} onCommit={(opacity) => set({ ...highlight, opacity }, 'opacity')} disabled={disabled} />
      </div>
      <div className="g2">
        <NumberField label="宽度" labelPlacement="inside" size="sm" suffix="×字号" value={highlight.widthRatio} greaterThan={0} max={2} onCommit={(widthRatio) => set({ ...highlight, widthRatio }, 'widthRatio')} disabled={disabled} />
        <Select label="混合" labelPlacement="inside" size="sm" value={highlight.blend} options={BLEND_OPTIONS} onChange={(blend) => set({ ...highlight, blend }, 'blend')} disabled={disabled} />
      </div>
      <Note>擦除线上的一道亮带，叠在填充与描边上，羽化为 0 时也显示。</Note>
    </>
  );
}

/** 滑块加右边的数值：拖动中数值跟手，松手才提交。 */
export function InlineSlider(props: {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  format: (value: number) => string;
  onCommit: (value: number) => void;
  disabled?: boolean;
  className?: string;
}) {
  const [shown, setShown] = useState(props.value);
  useEffect(() => setShown(props.value), [props.value]);
  return (
    <span className={cls('islider', props.className)}>
      <SliderField
        label={props.label}
        hideLabel
        value={props.value}
        min={props.min}
        max={props.max}
        step={props.step}
        format={props.format}
        onPreview={setShown}
        onCommit={props.onCommit}
        disabled={!!props.disabled}
      />
      <span className="islider-value">{props.format(shown)}</span>
    </span>
  );
}

/**
 * 纯色或渐变的取值（不含材质类型的选择），外观页签和材质页签共用。
 * compact：外观页签的一行里只放颜色或渐变条，渐变的参数由调用方放在下面一行。
 */
export function StaticMaterialValue(props: { label: string; material: StaticMaterial; onChange: (material: StaticMaterial) => void; disabled: boolean; compact?: boolean }) {
  const { material, disabled } = props;
  if (material.kind === 'solid') {
    return (
      <ColorField
        label={props.label}
        labelPlacement={props.compact ? 'none' : 'side'}
        size="sm"
        value={material.color}
        onCommit={(color) => props.onChange({ kind: 'solid', color })}
        disabled={disabled}
      />
    );
  }
  if (props.compact) return <GradientStrip gradient={material.gradient} />;
  return <GradientFields gradient={material.gradient} onChange={(gradient) => props.onChange({ kind: 'gradient', gradient })} disabled={disabled} />;
}

function GradientStrip(props: { gradient: StyleGradient }) {
  const material = { kind: 'gradient' as const, gradient: props.gradient };
  return <span className="gradient-strip" style={{ background: materialCss(material) }} title={materialText(material)} />;
}

const GRADIENT_KIND_OPTIONS: readonly { value: StyleGradient['kind']; label: string }[] = [
  { value: 'linear', label: '线性' },
  { value: 'radial', label: '径向' },
];
// OKLCH 插值能完整保存，但后端渲染器还没实现：成片里按纯色基色绘制（后端 GradientSupport）。单字坐标 0.112.76 起已绘制，演唱进度坐标 1.50.0 起删除。
const PENDING_TEXT = '（成片暂按纯色绘制）';
const SPACE_OPTIONS: readonly { value: StyleGradient['coordinateSpace']; label: string }[] = [
  { value: 'line', label: '整行' },
  { value: 'canvas', label: '整个画布' },
  { value: 'glyph', label: '单个字形' },
];
const COLOR_SPACE_OPTIONS: readonly { value: StyleGradient['colorSpace']; label: string }[] = [
  { value: 'linearRgb', label: '线性 RGB' },
  { value: 'hsv', label: 'HSV' },
  { value: 'oklch', label: `OKLCH${PENDING_TEXT}` },
];

/** 后端渲染器还没实现的组合（只剩 OKLCH 插值）：模板照常保存，成片里按纯色基色绘制。 */
function renderPending(g: StyleGradient): boolean {
  return g.colorSpace === 'oklch';
}
const HUE_OPTIONS: readonly { value: StyleGradient['huePath']; label: string }[] = [
  { value: 'shorter', label: '较短路径' },
  { value: 'longer', label: '较长路径' },
  { value: 'increasing', label: '色相递增' },
  { value: 'decreasing', label: '色相递减' },
];

/**
 * 渐变：类型、角度或中心与半径、坐标空间、插值空间与色相路径、色标。
 * 渐变条只按色标从左到右显示颜色，不是渲染结果；条下面的圆点是色标位置。
 */
export function GradientFields(props: { gradient: StyleGradient; onChange: (gradient: StyleGradient) => void; disabled: boolean }) {
  const { gradient: g, disabled } = props;
  const set = (patch: Partial<StyleGradient>) => props.onChange({ ...g, ...patch });
  const problem = gradientProblem(g);
  const last = g.stops.length - 1;
  const material = { kind: 'gradient' as const, gradient: g };
  return (
    <div className="gradient-editor">
      <div className="g2">
        <Select label="类型" labelPlacement="inside" size="sm" value={g.kind} options={GRADIENT_KIND_OPTIONS} onChange={(kind) => set({ kind })} disabled={disabled} />
        {g.kind === 'linear' ? (
          <NumberField label="角度" labelPlacement="inside" size="sm" suffix="°" value={g.angleDegrees} onCommit={(angleDegrees) => set({ angleDegrees })} disabled={disabled} />
        ) : (
          <>
            <span />
            <NumberField label="中心 X" labelPlacement="inside" size="sm" value={g.centerX} min={0} max={1} onCommit={(centerX) => set({ centerX })} disabled={disabled} />
            <NumberField label="中心 Y" labelPlacement="inside" size="sm" value={g.centerY} min={0} max={1} onCommit={(centerY) => set({ centerY })} disabled={disabled} />
            <NumberField label="半径 X" labelPlacement="inside" size="sm" value={g.radiusX} greaterThan={0} onCommit={(radiusX) => set({ radiusX })} disabled={disabled} />
            <NumberField label="半径 Y" labelPlacement="inside" size="sm" value={g.radiusY} greaterThan={0} onCommit={(radiusY) => set({ radiusY })} disabled={disabled} />
          </>
        )}
        <Select label="坐标" labelPlacement="inside" size="sm" value={g.coordinateSpace} options={SPACE_OPTIONS} onChange={(coordinateSpace) => set({ coordinateSpace })} disabled={disabled} />
        <Select label="插值" labelPlacement="inside" size="sm" value={g.colorSpace} options={COLOR_SPACE_OPTIONS} onChange={(colorSpace) => set({ colorSpace })} disabled={disabled} />
        <Select
          label="色相路径"
          labelPlacement="inside"
          size="sm"
          className="span2"
          value={g.huePath}
          options={HUE_OPTIONS}
          onChange={(huePath) => set({ huePath })}
          disabled={disabled || g.colorSpace === 'linearRgb'}
        />
      </div>
      {g.kind === 'radial' && <Note>中心与半径是相对坐标空间的比例（0～1）。</Note>}
      {renderPending(g) && <Note>渲染器还没实现这种组合，成片里暂按纯色绘制；模板会原样保存，渲染器支持后自动生效。</Note>}
      <div className="grad" style={{ background: materialCss(material) }} title={materialText(material)} aria-hidden>
        {g.stops.map((stop, i) => (
          <i key={i} style={{ '--x': `${stop.offset * 100}%`, '--c': materialCss({ kind: 'solid', color: stop.color }) } as CSSProperties} />
        ))}
      </div>
      <div className="gradient-stops" role="group" aria-label="色标">
        {g.stops.map((stop, i) => (
          <div key={i} className="stop gradient-stop" data-stop={i}>
            <NumberField
              label={`色标 ${i + 1}`}
              labelPlacement="inside"
              size="sm"
              suffix="%"
              value={Math.round(stop.offset * 10000) / 100}
              min={0}
              max={100}
              onCommit={(v) => props.onChange(setStopOffset(g, i, v / 100))}
              disabled={disabled || i === 0 || i === last}
            />
            <ColorField label={`色标 ${i + 1} 的颜色`} labelPlacement="none" size="sm" value={stop.color} onCommit={(color) => props.onChange(setStopColor(g, i, color))} disabled={disabled} />
            {i === 0 || i === last ? (
              <span />
            ) : (
              <IconButton icon="delete" size="sm" label={`删除色标 ${i + 1}`} isDisabled={disabled} onPress={() => props.onChange(removeStop(g, i))} />
            )}
          </div>
        ))}
      </div>
      <div className="gradient-actions">
        <Button variant="text" size="sm" icon="add" isDisabled={disabled || g.stops.length >= MAX_GRADIENT_STOPS} onPress={() => props.onChange(addStop(g))}>
          添加色标
        </Button>
        <span className="faint small">首末色标固定在 0% 与 100%</span>
      </div>
      {problem && <Note error>{problem}</Note>}
    </div>
  );
}
