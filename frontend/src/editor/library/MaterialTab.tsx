// 材质页签（W7 设计 §2.3）：选部位（未唱填充、已唱填充、描边），再编辑这个部位的材质：纯色、渐变或效果包材质。
// 选中的部位同时是试样画布的材质焦点（由样式库页传给试样画布）。

import type { EffectPackageInspectResponse, FullTemplateDefinition, FullTemplateMaterial, TemplateMaterialSlot } from '../../protocol';
import {
  changeMaterialKind,
  materialCandidates,
  materialFallbackColor,
  MATERIAL_SLOTS,
  packageKey,
  packageMaterial,
  shortRevision,
  SLOT_LABEL,
  slotMaterial,
  withSlotMaterial,
  type DefinitionIssue,
} from '../model/full-template';
import { groupAllowed, groupIncluded, TEMPLATE_GROUPS, type TemplateGroupId } from '../model/template-groups';
import { componentMaterialProblem } from '../model/script-components';
import { Select } from '../ui/fields';
import { PackagesPanel } from './PackagesPanel';
import { ParameterFields } from './ParameterFields';
import type { TemplateDraft, TemplateLibraryStore } from './template-library-store';
import { StaticMaterialValue } from './TemplateEditor';
import type { ComponentLibraryStore } from './component-library-store';
import { ComponentPicker } from './ComponentPicker';

const SLOT_GROUP: Record<TemplateMaterialSlot, TemplateGroupId> = { unsungFill: 'unsung', sungFill: 'sung', stroke: 'stroke' };

const KIND_OPTIONS: readonly { value: FullTemplateMaterial['kind']; label: string }[] = [
  { value: 'solid', label: '纯色' },
  { value: 'gradient', label: '渐变' },
  { value: 'package', label: '效果包' },
];

export function MaterialTab(props: {
  store: TemplateLibraryStore;
  definition: FullTemplateDefinition;
  draft: TemplateDraft | undefined;
  catalog: ReadonlyMap<string, EffectPackageInspectResponse>;
  issues: readonly DefinitionIssue[];
  slot: TemplateMaterialSlot;
  onSlot: (slot: TemplateMaterialSlot) => void;
  /** 打开外观页签（描边关闭、部位不包含时去那里打开）。 */
  onOpenAppearance: () => void;
  disabled: boolean;
  components: ComponentLibraryStore | undefined;
}) {
  const { definition, draft, catalog, slot } = props;
  const editable = !!draft && !props.disabled;
  const material = slotMaterial(definition.style, slot);
  const group = TEMPLATE_GROUPS.find((g) => g.id === SLOT_GROUP[slot])!;
  const change = (next: FullTemplateMaterial) => props.store.edit({ ...definition, style: withSlotMaterial(definition.style, slot, next) });
  const slotIssues = props.issues.filter((i) => i.where.kind === 'slot' && i.where.slot === slot);
  const scope = !groupAllowed(group, definition.kind)
    ? `这个用途的模板不能改${group.label}，这里的材质不会生效。`
    : !groupIncluded(group, definition.properties)
      ? `这个模板不包含“${group.label}”（由目标继承），这里的材质不会生效。`
      : undefined;

  return (
    <div className="material-tab">
      <PackagesPanel store={props.store} definition={definition} draft={draft} catalog={catalog} disabled={props.disabled} />
      <div className="slot-buttons" role="group" aria-label="材质部位">
        {MATERIAL_SLOTS.map((s) => {
          const off = s === 'stroke' && !definition.style.stroke;
          const problem = props.issues.some((i) => i.where.kind === 'slot' && i.where.slot === s);
          return (
            <mdui-button key={s} variant={s === slot ? 'filled' : 'outlined'} data-slot={s} data-selected={s === slot} onClick={() => props.onSlot(s)}>
              {off ? '描边已关闭' : SLOT_LABEL[s]}
              {problem && <mdui-icon-error slot="end-icon" />}
            </mdui-button>
          );
        })}
      </div>
      {material === null ? (
        <div className="material-off">
          <p className="hint">描边已关闭。</p>
          {draft && (
            <mdui-button variant="text" onClick={props.onOpenAppearance}>
              在外观页签打开
            </mdui-button>
          )}
        </div>
      ) : (
        <>
          {scope && (
            <div className="material-off">
              <p className="hint">{scope}</p>
              {draft && groupAllowed(group, definition.kind) && (
                <mdui-button variant="text" onClick={props.onOpenAppearance}>
                  在外观页签打开
                </mdui-button>
              )}
            </div>
          )}
          <SlotMaterial
            definition={definition}
            draft={draft}
            catalog={catalog}
            slot={slot}
            material={material}
            issues={slotIssues}
            onChange={change}
            disabled={!editable}
          />
          {draft && props.components && <div className="add-effect">
            <ComponentPicker
              components={props.components}
              kind="Material"
              problemOf={(detail) => componentMaterialProblem(slot, detail)}
              disabled={!editable}
              onChoose={(detail) => props.store.useComponentMaterial(slot, detail)}
            />
          </div>}
        </>
      )}
    </div>
  );
}

function SlotMaterial(props: {
  definition: FullTemplateDefinition;
  draft: TemplateDraft | undefined;
  catalog: ReadonlyMap<string, EffectPackageInspectResponse>;
  slot: TemplateMaterialSlot;
  material: FullTemplateMaterial;
  issues: readonly DefinitionIssue[];
  onChange: (material: FullTemplateMaterial) => void;
  disabled: boolean;
}) {
  const { material, catalog, slot, disabled } = props;
  const references = [...props.definition.packages, ...(props.draft?.imported ?? [])];
  const candidates = materialCandidates(references, catalog, slot);
  const first = candidates[0];
  const candidate = first ? packageMaterial(first.pkg, first.exports[0]!.id) : undefined;
  const label = SLOT_LABEL[slot];
  const pkg = material.kind === 'package' ? catalog.get(packageKey(material.packageId, material.revision)) : undefined;
  const fallback = material.kind === 'package' ? materialFallbackColor(pkg?.exports.find((e) => e.id === material.exportId)) : undefined;
  const kindOptions = KIND_OPTIONS.map((o) => (o.value === 'package' && !candidate && material.kind !== 'package' ? { ...o, disabled: true } : o));

  return (
    <div className="slot-material" data-slot={slot} data-kind={material.kind}>
      <Select
        label={`${label}的类型`}
        value={material.kind}
        options={kindOptions}
        onChange={(kind) => {
          const next = changeMaterialKind(material, kind, fallback, candidate);
          if (next) props.onChange(next);
        }}
        disabled={disabled}
      />
      {material.kind !== 'package' && !candidate && !disabled && <p className="hint">要用效果包材质，先在上面导入含材质导出的效果包。</p>}
      {material.kind === 'package' ? (
        <PackageMaterialFields material={material} pkg={pkg} candidates={candidates} issues={props.issues} onChange={props.onChange} disabled={disabled} />
      ) : (
        <StaticMaterialValue label={label} material={material} onChange={props.onChange} disabled={disabled} />
      )}
    </div>
  );
}

function PackageMaterialFields(props: {
  material: Extract<FullTemplateMaterial, { kind: 'package' }>;
  pkg: EffectPackageInspectResponse | undefined;
  candidates: ReturnType<typeof materialCandidates>;
  issues: readonly DefinitionIssue[];
  onChange: (material: FullTemplateMaterial) => void;
  disabled: boolean;
}) {
  const { material, pkg, candidates, disabled } = props;
  const currentKey = packageKey(material.packageId, material.revision);
  const packageOptions = candidates.map((c) => ({ value: packageKey(c.pkg.packageId, c.pkg.packageRevision), label: `${c.pkg.name} ${c.pkg.version}（${shortRevision(c.pkg.packageRevision)}）` }));
  if (!packageOptions.some((o) => o.value === currentKey)) packageOptions.unshift({ value: currentKey, label: pkg ? `${pkg.name} ${pkg.version}（不能用在这个部位）` : `${material.packageId}（没有读到包信息）` });
  const exports = pkg?.exports.filter((e) => e.kind === 'material') ?? [];
  const exportOptions = exports.map((e) => ({ value: e.id, label: e.name }));
  if (!exportOptions.some((o) => o.value === material.exportId)) exportOptions.unshift({ value: material.exportId, label: `${material.exportId}（包里没有）` });
  const whole = props.issues.filter((i) => !i.parameterId);

  return (
    <div className="package-material">
      <div className="field-row">
        <Select
          label="效果包"
          value={currentKey}
          options={packageOptions}
          onChange={(key) => {
            const c = candidates.find((x) => packageKey(x.pkg.packageId, x.pkg.packageRevision) === key);
            if (c) props.onChange(packageMaterial(c.pkg, c.exports[0]!.id));
          }}
          disabled={disabled}
        />
        <Select
          label="材质"
          value={material.exportId}
          options={exportOptions}
          onChange={(exportId) => props.onChange({ ...material, exportId, parametersJson: '{}' })}
          disabled={disabled || !pkg}
        />
      </div>
      {whole.map((issue) => (
        <p key={issue.message} className="notice-error">
          {issue.message}
        </p>
      ))}
      {pkg ? (
        <ParameterFields
          pkg={pkg}
          exportId={material.exportId}
          parametersJson={material.parametersJson}
          onChange={(parametersJson) => props.onChange({ ...material, parametersJson })}
          issues={props.issues}
          disabled={disabled}
        />
      ) : (
        <p className="hint">正在读取包信息…读不到时参数原样保留，保存由后端检查。</p>
      )}
    </div>
  );
}
