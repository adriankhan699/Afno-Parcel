import { useEffect, useState } from 'react';
import { ApiError, api } from '../lib/apiClient';
import { formatAddress } from '../lib/format';
import type { BranchResponse, PagedResponse } from '../types/api';

type State =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'loaded'; page: PagedResponse<BranchResponse> };

const typeLabels: Record<BranchResponse['type'], string> = {
  HeadOffice: 'Head office',
  Branch: 'Branch',
};

export default function BranchesPage() {
  const [state, setState] = useState<State>({ kind: 'loading' });

  useEffect(() => {
    const controller = new AbortController();
    api
      .get<PagedResponse<BranchResponse>>('/api/v1/branches?page=1&pageSize=50', controller.signal)
      .then((page) => setState({ kind: 'loaded', page }))
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        const message =
          err instanceof ApiError ? err.message : 'Could not reach the server. Please try again shortly.';
        setState({ kind: 'error', message });
      });
    return () => controller.abort();
  }, []);

  return (
    <div>
      <h1 className="text-2xl font-bold tracking-tight">Our branches</h1>
      <p className="mt-2 text-sm text-slate-600">Drop off or collect parcels at any of our locations.</p>

      <div className="mt-6">
        {state.kind === 'loading' && <p className="text-sm text-slate-500">Loading branches…</p>}
        {state.kind === 'error' && (
          <p role="alert" className="rounded-lg bg-amber-50 p-4 text-sm text-amber-800">
            {state.message}
          </p>
        )}
        {state.kind === 'loaded' && state.page.items.length === 0 && (
          <p className="text-sm text-slate-500">No branches have been added yet.</p>
        )}
        {state.kind === 'loaded' && state.page.items.length > 0 && (
          <ul className="grid gap-4 sm:grid-cols-2">
            {state.page.items.map((b) => (
              <li key={b.id} className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
                <div className="flex items-start justify-between gap-3">
                  <h2 className="font-semibold">{b.name}</h2>
                  <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600">
                    {typeLabels[b.type] ?? b.type}
                  </span>
                </div>
                <p className="mt-2 text-sm text-slate-600">{formatAddress(b.address)}</p>
                <p className="mt-1 text-sm text-slate-600">☎ {b.phoneNumber}</p>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
