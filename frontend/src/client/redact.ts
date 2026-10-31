// 凭证打码。请求日志、控制台显示和测试留档都只使用打码后的副本。

const SECRET_KEYS = new Set(['accesstoken', 'resumetoken', 'bootstraptoken', 'token', 'authorization']);
export const REDACTED = '***';

export function isSecretKey(key: string): boolean {
  return SECRET_KEYS.has(key.toLowerCase());
}

/** 深拷贝并把凭证字段替换成 ***。 */
export function redact<T>(value: T): T {
  if (Array.isArray(value)) return value.map((item) => redact(item)) as T;
  if (value && typeof value === 'object' && Object.getPrototypeOf(value) === Object.prototype) {
    const out: Record<string, unknown> = {};
    for (const [key, inner] of Object.entries(value)) {
      out[key] = isSecretKey(key) && inner != null ? REDACTED : redact(inner);
    }
    return out as T;
  }
  return value;
}

export function redactHeaders(headers: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [key, value] of Object.entries(headers)) {
    out[key] = isSecretKey(key) ? (value.startsWith('Bearer ') ? `Bearer ${REDACTED}` : REDACTED) : value;
  }
  return out;
}

/** 规范 §1.1 禁止的 URL 参数名；客户端从不使用它们，这里做最后一道防线。 */
const FORBIDDEN_QUERY = new Set(['token', 'access_token', 'accesstoken', 'bootstraptoken', 'resumetoken']);

export function assertNoCredentialInUrl(url: URL): void {
  for (const key of url.searchParams.keys()) {
    if (FORBIDDEN_QUERY.has(key.toLowerCase())) {
      throw new Error(`拒绝把凭证参数 ${key} 放进 URL。`);
    }
  }
}
