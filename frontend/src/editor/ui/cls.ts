/** 拼类名：去掉空值。 */
export function cls(...parts: (string | false | null | undefined)[]): string {
  return parts.filter(Boolean).join(' ');
}

/** 去掉值为 undefined 的属性，便于把可选属性透传给 exactOptionalPropertyTypes 下的组件。 */
export function defined<T extends object>(props: T): { [K in keyof T]?: Exclude<T[K], undefined> } {
  return Object.fromEntries(Object.entries(props).filter(([, value]) => value !== undefined)) as { [K in keyof T]?: Exclude<T[K], undefined> };
}
