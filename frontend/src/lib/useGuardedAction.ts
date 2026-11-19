import { useCallback, useRef, useState } from 'react';
import { actionErrorMessage } from './delivery';
import { ApiError } from './apiClient';

/** A server error as a sentence for the agent: the server's own explanation where there is one. */
export function toActionError(err: unknown): string {
  return err instanceof ApiError ? actionErrorMessage(err.status, err.problem.detail) : actionErrorMessage(undefined, undefined);
}

/** What `run` resolves to: whether it worked (success does not depend on the response having a body), and the value. */
export type ActionResult<T> = { ok: true; value: T } | { ok: false };

export interface GuardedAction {
  busy: boolean;
  error: string | null;
  notice: string | null;
  /**
   * Runs one action at a time. A second tap while one is running is ignored (a ref, not state, so even two taps in the same
   * frame cannot both get through). A refused second tap resolves to `{ ok: false }`; a failure puts its message in `error`.
   */
  run: <T>(action: () => Promise<T>, success?: string) => Promise<ActionResult<T>>;
  clear: () => void;
}

/** `mapError` turns a failure into the sentence shown to the person; the delivery wording is the default. */
export function useGuardedAction(mapError: (err: unknown) => string = toActionError): GuardedAction {
  const locked = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const run = useCallback(async <T,>(action: () => Promise<T>, success?: string): Promise<ActionResult<T>> => {
    if (locked.current) return { ok: false };
    locked.current = true;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const result = await action();
      if (success) setNotice(success);
      return { ok: true, value: result };
    } catch (err) {
      setError(mapError(err));
      return { ok: false };
    } finally {
      locked.current = false;
      setBusy(false);
    }
  }, [mapError]);

  const clear = useCallback(() => {
    setError(null);
    setNotice(null);
  }, []);

  return { busy, error, notice, run, clear };
}
