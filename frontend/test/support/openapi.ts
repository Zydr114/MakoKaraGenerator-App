// 冻结 OpenAPI（backend/web/openapi.json）的最小校验器，只用于测试。
// 只实现该文件实际用到的 JSON Schema 关键字：type、properties、required、additionalProperties、prefixItems、items、$ref、enum、const、
// pattern、oneOf、anyOf、allOf、not、if／then／else、minimum、maximum、exclusiveMinimum、minLength、maxLength、
// minItems、maxItems、uniqueItems、contains／minContains／maxContains、dependentRequired、format(date-time)。
// 设计 §5“类型漂移的防护”：集成测试用它校验每个实际响应。

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

type Schema = Record<string, unknown>;

export interface SchemaIssue {
  path: string;
  message: string;
}

export const OPENAPI_PATH = fileURLToPath(new URL('../../../backend/web/openapi.json', import.meta.url));

export class OpenApiContract {
  readonly doc: { paths: Record<string, Record<string, unknown>>; components: { schemas: Record<string, Schema> } };

  constructor(doc: OpenApiContract['doc']) {
    this.doc = doc;
  }

  static load(): OpenApiContract {
    return new OpenApiContract(JSON.parse(readFileSync(OPENAPI_PATH, 'utf8')));
  }

  schema(name: string): Schema {
    const schema = this.doc.components.schemas[name];
    if (!schema) throw new Error(`OpenAPI 中没有 schema ${name}`);
    return schema;
  }

  validateNamed(name: string, value: unknown): SchemaIssue[] {
    return this.validate(this.schema(name), value, '$');
  }

  validate(schema: Schema, value: unknown, path = '$'): SchemaIssue[] {
    const issues: SchemaIssue[] = [];
    this.check(schema, value, path, issues);
    return issues;
  }

  /** 按方法与实际路径找到 OpenAPI 操作。 */
  findOperation(method: string, pathname: string): { template: string; operation: Record<string, unknown> } | null {
    const actual = pathname.split('/');
    for (const [template, item] of Object.entries(this.doc.paths)) {
      const parts = template.split('/');
      if (parts.length !== actual.length) continue;
      if (!parts.every((p, i) => (p.startsWith('{') && p.endsWith('}')) || p === actual[i])) continue;
      const operation = item[method.toLowerCase()] as Record<string, unknown> | undefined;
      if (operation) return { template, operation };
    }
    return null;
  }

  /** 校验一次真实响应的 JSON 正文；找不到 JSON schema（二进制、SSE）时返回 null。 */
  validateResponse(method: string, pathname: string, status: number, body: unknown): { template: string; issues: SchemaIssue[] } | null {
    const found = this.findOperation(method, pathname);
    if (!found) return { template: '(unknown)', issues: [{ path: '$', message: `OpenAPI 中没有 ${method} ${pathname}` }] };
    const responses = found.operation.responses as Record<string, { content?: Record<string, { schema?: Schema }> }>;
    const response = responses[String(status)] ?? responses.default;
    if (!response) return { template: found.template, issues: [{ path: '$', message: `OpenAPI 未声明状态 ${status}` }] };
    const content = response.content ?? {};
    const schema = content['application/json']?.schema ?? content['application/problem+json']?.schema;
    if (!schema) return null;
    return { template: found.template, issues: this.validate(schema, body) };
  }

  private resolve(schema: Schema): Schema {
    let current = schema;
    for (let guard = 0; typeof current.$ref === 'string' && guard < 32; guard++) {
      const name = (current.$ref as string).replace('#/components/schemas/', '');
      current = this.schema(name);
    }
    return current;
  }

  private check(raw: Schema, value: unknown, path: string, issues: SchemaIssue[]): void {
    const schema = this.resolve(raw);
    const push = (message: string) => issues.push({ path, message });

    const passes = (s: Schema) => this.validate(s, value, path).length === 0;
    if (Array.isArray(schema.oneOf)) {
      const matches = (schema.oneOf as Schema[]).filter(passes).length;
      if (matches !== 1) push(`oneOf 匹配了 ${matches} 个分支`);
    }
    if (Array.isArray(schema.anyOf) && !(schema.anyOf as Schema[]).some(passes)) push('anyOf 没有匹配的分支');
    if (Array.isArray(schema.allOf)) {
      for (const inner of schema.allOf as Schema[]) this.check(inner, value, path, issues);
    }
    if (schema.not && typeof schema.not === 'object' && passes(schema.not as Schema)) push('不应匹配 not 分支');
    if (schema.if && typeof schema.if === 'object') {
      const branch = passes(schema.if as Schema) ? schema.then : schema.else;
      if (branch && typeof branch === 'object') this.check(branch as Schema, value, path, issues);
    }
    if ('const' in schema && value !== schema.const) push(`应为常量 ${JSON.stringify(schema.const)}`);
    if (Array.isArray(schema.enum) && !schema.enum.includes(value as never)) push(`不在枚举 ${JSON.stringify(schema.enum)} 中`);

    if (schema.type !== undefined) {
      const types = Array.isArray(schema.type) ? (schema.type as string[]) : [schema.type as string];
      if (!types.some((t) => matchesType(t, value))) {
        push(`类型应为 ${types.join('|')}，实际 ${value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value}`);
        return;
      }
    }

    if (typeof value === 'string') {
      if (typeof schema.pattern === 'string' && !new RegExp(schema.pattern, 'u').test(value)) push(`不匹配 ${schema.pattern}`);
      if (typeof schema.minLength === 'number' && [...value].length < schema.minLength) push(`长度小于 ${schema.minLength}`);
      if (typeof schema.maxLength === 'number' && [...value].length > schema.maxLength) push(`长度大于 ${schema.maxLength}`);
      if (schema.format === 'date-time' && !Number.isFinite(Date.parse(value))) push('不是合法的 date-time');
    }
    if (typeof value === 'number') {
      if (typeof schema.minimum === 'number' && value < schema.minimum) push(`小于 ${schema.minimum}`);
      if (typeof schema.maximum === 'number' && value > schema.maximum) push(`大于 ${schema.maximum}`);
      if (typeof schema.exclusiveMinimum === 'number' && value <= schema.exclusiveMinimum) push(`应大于 ${schema.exclusiveMinimum}`);
    }
    if (Array.isArray(value)) {
      if (typeof schema.minItems === 'number' && value.length < schema.minItems) push(`元素少于 ${schema.minItems}`);
      if (typeof schema.maxItems === 'number' && value.length > schema.maxItems) push(`元素多于 ${schema.maxItems}`);
      // 2020-12：prefixItems 逐位校验前几项，items 只管其后的元素。
      const prefix = Array.isArray(schema.prefixItems) ? (schema.prefixItems as Schema[]) : [];
      prefix.forEach((itemSchema, i) => {
        if (i < value.length) this.check(itemSchema, value[i], `${path}[${i}]`, issues);
      });
      if (schema.items && typeof schema.items === 'object') {
        value.forEach((item, i) => {
          if (i >= prefix.length) this.check(schema.items as Schema, item, `${path}[${i}]`, issues);
        });
      }
      if (schema.uniqueItems === true) {
        const seen = new Set(value.map((item) => canonicalJson(item)));
        if (seen.size !== value.length) push('元素有重复');
      }
      if (schema.contains && typeof schema.contains === 'object') {
        const count = value.filter((item) => this.validate(schema.contains as Schema, item, path).length === 0).length;
        const min = typeof schema.minContains === 'number' ? schema.minContains : 1;
        if (count < min) push(`满足 contains 的元素少于 ${min}`);
        if (typeof schema.maxContains === 'number' && count > schema.maxContains) push(`满足 contains 的元素多于 ${schema.maxContains}`);
      }
    }
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      const object = value as Record<string, unknown>;
      const properties = (schema.properties ?? {}) as Record<string, Schema>;
      for (const key of (schema.required ?? []) as string[]) {
        if (!(key in object)) push(`缺少必需字段 ${key}`);
      }
      for (const [key, needs] of Object.entries((schema.dependentRequired ?? {}) as Record<string, string[]>)) {
        if (key in object) for (const need of needs) if (!(need in object)) push(`有 ${key} 时缺少 ${need}`);
      }
      for (const [key, inner] of Object.entries(object)) {
        if (properties[key]) this.check(properties[key], inner, `${path}.${key}`, issues);
        else if (schema.additionalProperties === false) issues.push({ path: `${path}.${key}`, message: '不允许的字段' });
        else if (schema.additionalProperties && typeof schema.additionalProperties === 'object') {
          this.check(schema.additionalProperties as Schema, inner, `${path}.${key}`, issues);
        }
      }
    }
  }
}

/** 键排序后的 JSON，用于 uniqueItems 比较。 */
function canonicalJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  if (value && typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
    return `{${entries.map(([k, v]) => `${JSON.stringify(k)}:${canonicalJson(v)}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

function matchesType(type: string, value: unknown): boolean {
  switch (type) {
    case 'null':
      return value === null;
    case 'string':
      return typeof value === 'string';
    case 'boolean':
      return typeof value === 'boolean';
    case 'integer':
      return typeof value === 'number' && Number.isInteger(value);
    case 'number':
      return typeof value === 'number';
    case 'array':
      return Array.isArray(value);
    case 'object':
      return value !== null && typeof value === 'object' && !Array.isArray(value);
    default:
      return true;
  }
}
