// 材质页签（W7 设计 §2.3）：选部位（未唱填充、已唱填充、描边），再编辑这个部位的材质：纯色、渐变或效果包材质。
// 选中的部位同时是试样画布的材质焦点（由样式库页传给试样画布）。

import type { ReactNode } from 'react';
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
import { Button } from '../ui/Button';
import { Segmented } from '../ui/choices';
import { Select } from '../ui/fields';
import { Group, Note } from '../workspace/panel';
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

/** 材质页签（设计稿 #library-material）：部位分段按钮，选中部位的类型与取值，包材质的参数，模板里的效果包。 */
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
  const openAppearance = (
    <Button variant="text" size="sm" onPress={props.onOpenAppearance}>
      在外观页签打开
    </Button>
  );
  const picker = draft && props.components && (
    <ComponentPicker
      components={props.components}
      kind="Material"
      problemOf={(detail) => componentMaterialProblem(slot, detail)}
      disabled={!editable}
      onChoose={(detail) => props.store.useComponentMaterial(slot, detail)}
    />
  );

  return (
    <div className="material-tab">
      <Group title="材质部位" className="slot-pick">
        <Segmented
          label="材质部位"
          size="sm"
          className="slot-buttons"
          value={slot}
          options={MATERIAL_SLOTS.map((s) => ({
            value: s,
            label: SLOT_LABEL[s],
            ...(props.issues.some((i) => i.where.kind === 'slot' && i.where.slot === s) ? { icon: 'error' as const } : {}),
          }))}
          onChange={props.onSlot}
        />
        <Note>选中的部位同时是试样画布的材质焦点。</Note>
      </Group>
      {material === null ? (
        <Group title={SLOT_LABEL[slot]} className="slot-material" data-slot={slot} data-kind="off">
          <div className="material-off">
            <Note>描边已关闭。</Note>
            {draft && openAppearance}
          </div>
        </Group>
      ) : (
        <SlotMaterial
          definition={definition}
          draft={draft}
          catalog={catalog}
          slot={slot}
          material={material}
          issues={slotIssues}
          onChange={change}
          disabled={!editable}
          scope={
            scope && (
              <div className="material-off">
                <Note>{scope}</Note>
                {draft && groupAllowed(group, definition.kind) && openAppearance}
              </div>
            )
          }
          picker={picker}
        />
      )}
      <PackagesPanel store={props.store} definition={definition} draft={draft} catalog={catalog} disabled={props.disabled} />
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
  scope: ReactNode;
  picker: ReactNode;
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
    <>
      <Group title={label} className="slot-material" data-slot={slot} data-kind={material.kind} extra={props.picker}>
        {props.scope}
        <div className="frow">
          <span className="frow-lbl">类型</span>
          <Segmented
            label={`${label}的类型`}
            size="sm"
            className="material-kind"
            value={material.kind}
            options={kindOptions}
            onChange={(kind) => {
              const next = changeMaterialKind(material, kind, fallback, candidate);
              if (next) props.onChange(next);
            }}
            disabled={disabled}
          />
        </div>
        {material.kind !== 'package' && !candidate && !disabled && <Note>要用效果包材质，先在下面导入含材质导出的效果包。</Note>}
        {material.kind === 'package' ? (
          <PackageMaterialFields material={material} pkg={pkg} candidates={candidates} issues={props.issues} onChange={props.onChange} disabled={disabled} />
        ) : (
          <StaticMaterialValue label={label} material={material} onChange={props.onChange} disabled={disabled} />
        )}
      </Group>
      {material.kind === 'package' && (
        <Group title="参数" className="material-params">
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
            <Note>正在读取包信息…读不到时参数原样保留，保存由后端检查。</Note>
          )}
        </Group>
      )}
    </>
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
      <Select
        labelPlacement="side"
        size="sm"
        label="效果包"
        className="material-package"
        value={currentKey}
        options={packageOptions}
        onChange={(key) => {
          const c = candidates.find((x) => packageKey(x.pkg.packageId, x.pkg.packageRevision) === key);
          if (c) props.onChange(packageMaterial(c.pkg, c.exports[0]!.id));
        }}
        disabled={disabled}
      />
      <Select
        labelPlacement="side"
        size="sm"
        label="材质"
        className="material-export"
        value={material.exportId}
        options={exportOptions}
        onChange={(exportId) => props.onChange({ ...material, exportId, parametersJson: '{}' })}
        disabled={disabled || !pkg}
      />
      {whole.map((issue) => (
        <Note key={issue.message} error>
          {issue.message}
        </Note>
      ))}
    </div>
  );
}
