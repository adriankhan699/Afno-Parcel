import { useEffect, useState } from 'react';
import { env } from '../config/env';

type Status = 'checking' | 'online' | 'offline';

const styles: Record<Status, string> = {
  checking: 'bg-slate-100 text-slate-600',
  online: 'bg-emerald-50 text-emerald-700',
  offline: 'bg-amber-50 text-amber-700',
};

/** Shows whether the backend API is reachable (calls the liveness probe). */
export default function ApiStatusBadge() {
  const [status, setStatus] = useState<Status>('checking');

  useEffect(() => {
    const controller = new AbortController();
    fetch(`${env.apiBaseUrl}/health/live`, { signal: controller.signal })
      .then((res) => setStatus(res.ok ? 'online' : 'offline'))
      .catch((err: unknown) => {
        if (!(err instanceof DOMException && err.name === 'AbortError')) {
          setStatus('offline');
        }
      });
    return () => controller.abort();
  }, []);

  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium ${styles[status]}`}>
      <span aria-hidden className="h-1.5 w-1.5 rounded-full bg-current" />
      API {status}
    </span>
  );
}
