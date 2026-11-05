import { CommitDraftGroup } from '../ui/commit-drafts';
// 效果包参数控件（W7 设计 §2.4）：按导出的参数表生成，材质页签、效果页签和作者草稿的配置页签共用。
// 取值以 JSON 原文为准；输入框只拦格式不对的输入，越界等问题照常写进草稿，由本地校验写在控件旁并停用“保存”。
// 曲线用图形编辑，图片从包里 role=image 的文件里选（W8 设计 §2.9）。

import { useMemo } from 'react';
import type { EffectPackageFile, EffectPackageSchema, EffectPackageParameter } from '../../protocol';
import {
  colorFromJson,
  colorToJson,
  exportParameters,
  imageFiles,
  isOverridden,
  microsToMillis,
  millisToMicros,
  parameterRaw,
  rangeText,
  withoutParameter,
  withParameter,
} from '../model/effect-params';
import { isJsonInteger, isJsonNumber, parseObjectEntries } from '../model/json-text';
import { Button, IconButton } from '../ui/Button';
import { SliderField, Switch } from '../ui/choices';
import { cls } from '../ui/cls';
import { ColorField, CommitField, Select } from '../ui/fields';
import { Note } from '../workspace/panel';
import { CurveField } from './CurveField';

/**
 * 一行一个参数（.prm）：左边短标签，中间滑块（有范围的数字），右边数值框；没有滑块的控件占中右两格。
 * 末尾一格是“恢复默认”，参数改过时可用。
 */
export function ParameterFields(props: {
  pkg: EffectPackageSchema;
  exportId: string;
  parametersJson: string;
  onChange: (parametersJson: string) => void;
  /** 这一项（材质部位、配方或草稿配置）的问题；带 parameterId 的显示在对应参数下面。 */
  issues: readonly { parameterId?: string | undefined; message: string }[];
  disabled: boolean;
}) {
  const { pkg, exportId, parametersJson, disabled } = props;
  const exp = pkg.exports.find((e) => e.id === exportId);
  const parameters = useMemo(() => (exp ? exportParameters(pkg, exp) : []), [pkg, exp]);
  const extra = useMemo(() => {
    try {
      return [...parseObjectEntries(parametersJson).keys()].filter((id) => !exp?.parameterIds.includes(id));
    } catch {
      return [];
    }
  }, [parametersJson, exp]);
  if (!exp) return null;
  if (parameters.length === 0 && extra.length === 0) return <Note>这个导出没有参数。</Note>;
  const problems = (id: string) => props.issues.filter((i) => i.parameterId === id);
  return (
    <div className="param-fields">
      {parameters.map((parameter) => {
        const range = rangeText(parameter);
        const unit = parameter.type === 'duration' ? '毫秒' : parameter.unit;
        return (
          <CommitDraftGroup key={parameter.id} name={`parameter/${parameter.id}`}><div className={cls('prm', 'param-row', parameter.type === 'curve' && 'top')} data-param={parameter.id}>
            <span className="prm-lbl" title={[parameter.label, unit && `单位 ${unit}`, range && `范围 ${range}`].filter(Boolean).join('，')}>
              {parameter.label}
            </span>
            <ParameterControl
              parameter={parameter}
              files={pkg.files}
              raw={parameterRaw(parametersJson, parameter)}
              onCommit={(raw) => props.onChange(withParameter(parametersJson, parameter, raw))}
              disabled={disabled}
            />
            <IconButton
              icon="settings_backup_restore"
              label={`${parameter.label}恢复默认`}
              tooltip="恢复默认"
              size="sm"
              className="param-reset"
              isDisabled={disabled || !isOverridden(parametersJson, parameter.id)}
              onPress={() => props.onChange(withParameter(parametersJson, parameter, undefined))}
            />
            {problems(parameter.id).map((issue) => (
              <Note key={issue.message} error className="param-problem">
                {issue.message}
              </Note>
            ))}
          </div></CommitDraftGroup>
        );
      })}
      {extra.map((id) => (
        <div key={id} className="prm param-row" data-param={id} data-unknown>
          <span className="prm-lbl faint">{id}</span>
          <span className="param-unknown wide">
            包里没有参数 <code>{id}</code>（可能来自旧版本的包）
          </span>
          <Button variant="text" size="sm" isDisabled={disabled} onPress={() => props.onChange(withoutParameter(parametersJson, id))}>
            删除
          </Button>
        </div>
      ))}
    </div>
  );
}

function ParameterControl(props: {
  parameter: EffectPackageParameter;
  files: readonly EffectPackageFile[];
  raw: string;
  onCommit: (raw: string) => void;
  disabled: boolean;
}) {
  const { parameter: p, raw, disabled } = props;
  const label = p.label;
  const unit = p.unit && p.type !== 'duration' ? p.unit : undefined;
  const compact = { labelPlacement: 'none', size: 'sm' } as const;
  switch (p.type) {
    case 'number':
    case 'integer': {
      const integer = p.type === 'integer';
      const min = p.minimumJson == null ? NaN : Number(p.minimumJson);
      const max = p.maximumJson == null ? NaN : Number(p.maximumJson);
      const slider = Number.isFinite(min) && Number.isFinite(max) && max > min && isJsonNumber(raw);
      const field = (
        <CommitField
          {...compact}
          label={label}
          className={cls('param-number', !slider && 'wide')}
          value={raw}
          {...(unit ? { suffix: unit } : {})}
          validate={(v) => (!isJsonNumber(v) ? '请输入数字。' : integer && !isJsonInteger(v) ? '请输入整数。' : undefined)}
          onCommit={(v) => props.onCommit(v.trim())}
          disabled={disabled}
        />
      );
      if (!slider) return field;
      return (
        <>
          <SliderField
            label={label}
            hideLabel
            value={Math.min(max, Math.max(min, Number(raw)))}
            min={min}
            max={max}
            step={integer ? 1 : (max - min) / 100}
            format={(v) => String(integer ? Math.round(v) : round(v))}
            onCommit={(v) => props.onCommit(String(integer ? Math.round(v) : round(v)))}
            disabled={disabled}
          />
          {field}
        </>
      );
    }
    case 'duration':
      return (
        <CommitField
          {...compact}
          label={`${label}（毫秒）`}
          className="wide"
          suffix="ms"
          value={microsToMillis(raw)}
          validate={(v) => (millisToMicros(v) === undefined ? '请输入毫秒数，最多 3 位小数。' : undefined)}
          onCommit={(v) => props.onCommit(millisToMicros(v)!)}
          disabled={disabled}
        />
      );
    case 'boolean':
      return <Switch label={label} hideLabel className="wide" checked={raw.trim() === 'true'} onChange={(on) => props.onCommit(String(on))} disabled={disabled} />;
    case 'enum': {
      const value = parseString(raw) ?? '';
      const options = p.options.map((o) => ({ value: o.value, label: o.label }));
      if (!options.some((o) => o.value === value)) options.push({ value, label: `${value}（不在选项里）` });
      return <Select {...compact} label={label} className="wide" value={value} options={options} onChange={(v) => props.onCommit(JSON.stringify(v))} disabled={disabled} />;
    }
    case 'text':
      return <CommitField {...compact} label={label} className="wide" value={parseString(raw) ?? raw} onCommit={(v) => props.onCommit(JSON.stringify(v))} disabled={disabled} />;
    case 'color':
      return (
        <ColorField
          {...compact}
          label={label}
          className="wide"
          value={colorFromJson(raw) ?? { red: 0, green: 0, blue: 0, alpha: 255 }}
          onCommit={(color) => props.onCommit(colorToJson(color))}
          disabled={disabled}
        />
      );
    case 'curve':
      return <CurveField label={label} raw={raw} onCommit={props.onCommit} disabled={disabled} />;
    case 'asset': {
      // 后端没有读取包内图片的接口，所以只列路径和大小，不显示缩略图。
      const value = parseString(raw) ?? raw;
      const options = imageFiles(props.files).map((f) => ({ value: f.path, label: `${f.path}（${sizeText(f.size)}）` }));
      if (!options.some((o) => o.value === value)) options.push({ value, label: `${value}（包里没有这个图片）` });
      return (
        <Select {...compact} label={`${label}（包内图片）`} className="wide" value={value} options={options} onChange={(v) => props.onCommit(JSON.stringify(v))} disabled={disabled} />
      );
    }
  }
}

function sizeText(bytes: number): string {
  return bytes < 1024 ? `${bytes} 字节` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function parseString(raw: string): string | undefined {
  try {
    const value: unknown = JSON.parse(raw);
    return typeof value === 'string' ? value : undefined;
  } catch {
    return undefined;
  }
}

/** 滑块的取值留 4 位小数，避免 0.30000000000000004 这类尾数写进参数。 */
function round(value: number): number {
  return Math.round(value * 10000) / 10000;
}
