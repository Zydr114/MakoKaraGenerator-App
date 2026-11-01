// 效果包参数控件（W7 设计 §2.4）：按导出的参数表生成，材质页签、效果页签和作者草稿的配置页签共用。
// 取值以 JSON 原文为准；输入框只拦格式不对的输入，越界等问题照常写进草稿，由本地校验写在控件旁并停用“保存”。
// 曲线用图形编辑，图片从包里 role=image 的文件里选（W8 设计 §2.9）。

import { useMemo } from 'react';
import type { EffectPackageFile, EffectPackageInspectResponse, EffectPackageParameter } from '../../protocol';
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
import { ColorField, CommitField, Select, SliderField, Switch } from '../ui/legacy/fields';
import { CurveField } from './CurveField';

export function ParameterFields(props: {
  pkg: EffectPackageInspectResponse;
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
  if (parameters.length === 0 && extra.length === 0) return <p className="hint">这个导出没有参数。</p>;
  const problems = (id: string) => props.issues.filter((i) => i.parameterId === id);
  return (
    <div className="param-fields">
      {parameters.map((parameter) => (
        <div key={parameter.id} className="param-row" data-param={parameter.id}>
          <div className="param-control">
            <ParameterControl
              parameter={parameter}
              files={pkg.files}
              raw={parameterRaw(parametersJson, parameter)}
              onCommit={(raw) => props.onChange(withParameter(parametersJson, parameter, raw))}
              disabled={disabled}
            />
            <mdui-button
              variant="text"
              className="param-reset"
              disabled={disabled || !isOverridden(parametersJson, parameter.id)}
              onClick={() => props.onChange(withParameter(parametersJson, parameter, undefined))}
            >
              恢复默认
            </mdui-button>
          </div>
          {problems(parameter.id).map((issue) => (
            <p key={issue.message} className="notice-error param-problem">
              {issue.message}
            </p>
          ))}
        </div>
      ))}
      {extra.map((id) => (
        <div key={id} className="param-row" data-param={id} data-unknown>
          <div className="param-control">
            <span className="param-unknown">
              包里没有参数 <code>{id}</code>（可能来自旧版本的包）
            </span>
            <mdui-button variant="text" disabled={disabled} onClick={() => props.onChange(withoutParameter(parametersJson, id))}>
              删除
            </mdui-button>
          </div>
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
  const label = p.unit && p.type !== 'duration' ? `${p.label}（${p.unit}）` : p.label;
  const range = rangeText(p);
  switch (p.type) {
    case 'number':
    case 'integer': {
      const integer = p.type === 'integer';
      const min = p.minimumJson == null ? NaN : Number(p.minimumJson);
      const max = p.maximumJson == null ? NaN : Number(p.maximumJson);
      const slider = Number.isFinite(min) && Number.isFinite(max) && max > min && isJsonNumber(raw);
      return (
        <div className="param-number">
          <CommitField
            label={label}
            value={raw}
            helper={range ? `范围 ${range}` : ''}
            validate={(v) => (!isJsonNumber(v) ? '请输入数字。' : integer && !isJsonInteger(v) ? '请输入整数。' : undefined)}
            onCommit={(v) => props.onCommit(v.trim())}
            disabled={disabled}
          />
          {slider && (
            <SliderField
              label=""
              value={Math.min(max, Math.max(min, Number(raw)))}
              min={min}
              max={max}
              step={integer ? 1 : (max - min) / 100}
              format={(v) => String(integer ? Math.round(v) : round(v))}
              onCommit={(v) => props.onCommit(String(integer ? Math.round(v) : round(v)))}
              disabled={disabled}
            />
          )}
        </div>
      );
    }
    case 'duration':
      return (
        <CommitField
          label={`${p.label}（毫秒）`}
          value={microsToMillis(raw)}
          helper={range ? `范围 ${range}` : ''}
          validate={(v) => (millisToMicros(v) === undefined ? '请输入毫秒数，最多 3 位小数。' : undefined)}
          onCommit={(v) => props.onCommit(millisToMicros(v)!)}
          disabled={disabled}
        />
      );
    case 'boolean':
      return <Switch label={label} checked={raw.trim() === 'true'} onChange={(on) => props.onCommit(String(on))} disabled={disabled} />;
    case 'enum': {
      const value = parseString(raw) ?? '';
      const options = p.options.map((o) => ({ value: o.value, label: o.label }));
      if (!options.some((o) => o.value === value)) options.push({ value, label: `${value}（不在选项里）` });
      return <Select label={label} value={value} options={options} onChange={(v) => props.onCommit(JSON.stringify(v))} disabled={disabled} />;
    }
    case 'text':
      return <CommitField label={label} value={parseString(raw) ?? raw} onCommit={(v) => props.onCommit(JSON.stringify(v))} disabled={disabled} />;
    case 'color':
      return (
        <ColorField
          label={label}
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
      return <Select label={`${label}（包内图片）`} value={value} options={options} onChange={(v) => props.onCommit(JSON.stringify(v))} disabled={disabled} />;
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
