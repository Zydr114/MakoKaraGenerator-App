import { useEffect, useState, useSyncExternalStore } from 'react';
import type { ObservableValue } from '../client';

export function useObservable<T>(source: ObservableValue<T>): T {
  return useSyncExternalStore(source.subscribe, source.get);
}

/** 每 intervalMs 重新渲染一次，用于倒计时。 */
export function useNow(intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [intervalMs]);
  return now;
}

/** 持有一个 object URL，替换或卸载时释放旧的。 */
export function useObjectUrl(blob: Blob | null): string | null {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!blob) {
      setUrl(null);
      return;
    }
    const next = URL.createObjectURL(blob);
    setUrl(next);
    return () => URL.revokeObjectURL(next);
  }, [blob]);
  return url;
}
