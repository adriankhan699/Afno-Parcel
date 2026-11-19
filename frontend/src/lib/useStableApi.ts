import { useState } from 'react';
import { useApi } from './useApi';

export interface StableApi<T> {
  /** The latest loaded data for this path. It stays on screen while a reload is in flight, so the page does not flash. */
  data: T | null;
  /** True only until the first load of this path finishes. */
  loading: boolean;
  error: string | null;
  httpStatus?: number;
}

/**
 * Like useApi, but keeps showing the previous result while the same path is reloaded (after an action, or a refresh), so
 * a phone screen does not blink back to "Loading…" each time. A different path starts again from scratch.
 */
export function useStableApi<T>(path: string | null, reloadKey = 0): StableApi<T> {
  const result = useApi<T>(path, reloadKey);
  const [kept, setKept] = useState<{ path: string | null; data: T } | null>(null);

  if (result.status === 'loaded' && (kept === null || kept.data !== result.data || kept.path !== path)) {
    setKept({ path, data: result.data });
  }

  const data = kept !== null && kept.path === path ? kept.data : result.status === 'loaded' ? result.data : null;
  return {
    data,
    loading: data === null && (result.status === 'loading' || result.status === 'idle'),
    error: result.status === 'error' && data === null ? result.message : null,
    httpStatus: result.status === 'error' ? result.httpStatus : undefined,
  };
}
