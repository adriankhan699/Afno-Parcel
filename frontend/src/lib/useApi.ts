import { useEffect, useState } from 'react';
import { ApiError, api } from './apiClient';

export type ApiResult<T> =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'error'; message: string; httpStatus?: number }
  | { status: 'loaded'; data: T };

interface Settled<T> {
  path: string | null;
  reloadKey: number;
  data?: T;
  error?: string;
  httpStatus?: number;
}

export function errorMessage(err: unknown, fallback = 'Could not reach the server. Please try again shortly.'): string {
  return err instanceof ApiError ? err.message : fallback;
}

/**
 * GETs `path` whenever it changes (null = do nothing). Change `reloadKey` to fetch the same path again, for example after
 * an edit. The previous result is never shown for a new path.
 */
export function useApi<T>(path: string | null, reloadKey = 0): ApiResult<T> {
  const [settled, setSettled] = useState<Settled<T>>({ path: null, reloadKey: 0 });

  useEffect(() => {
    if (path === null) return;
    const controller = new AbortController();
    api
      .get<T>(path, controller.signal)
      .then((data) => setSettled({ path, reloadKey, data }))
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setSettled({ path, reloadKey, error: errorMessage(err), httpStatus: err instanceof ApiError ? err.status : undefined });
      });
    return () => controller.abort();
  }, [path, reloadKey]);

  if (path === null) return { status: 'idle' };
  if (settled.path !== path || settled.reloadKey !== reloadKey) return { status: 'loading' };
  if (settled.error !== undefined) return { status: 'error', message: settled.error, httpStatus: settled.httpStatus };
  return { status: 'loaded', data: settled.data as T };
}
