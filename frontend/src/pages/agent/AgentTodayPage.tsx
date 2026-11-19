import { useState } from 'react';
import { Link } from 'react-router';
import { AgentAssignmentCard } from '../../components/agent/AgentAssignmentCard';
import { AvailabilityToggle, BigButton, Panel } from '../../components/agent/AgentBits';
import { Alert } from '../../components/form';
import Pagination from '../../components/Pagination';
import { api } from '../../lib/apiClient';
import { buildDeliveryQuery } from '../../lib/delivery';
import { deliveryApi } from '../../lib/deliveryApi';
import { formatNpr } from '../../lib/format';
import { useGuardedAction } from '../../lib/useGuardedAction';
import { useStableApi } from '../../lib/useStableApi';
import type { AgentAssignmentResponse, AgentSummaryResponse, PagedResponse } from '../../types/api';

function Stat({ label, value, tone = 'text-slate-900' }: { label: string; value: string | number; tone?: string }) {
  return (
    <div className="rounded-2xl border border-slate-200 bg-white p-3 shadow-sm">
      <p className="text-xs font-medium text-slate-500">{label}</p>
      <p className={`mt-1 text-xl font-bold ${tone}`}>{value}</p>
    </div>
  );
}

/** The agent's day: how it is going, and the parcels they still have to deliver. */
export default function AgentTodayPage() {
  const [page, setPage] = useState(1);
  const [reloadKey, setReloadKey] = useState(0);
  const availability = useGuardedAction();

  const summary = useStableApi<AgentSummaryResponse>(deliveryApi.me, reloadKey);
  const list = useStableApi<PagedResponse<AgentAssignmentResponse>>(
    `${deliveryApi.myAssignments}${buildDeliveryQuery({ scope: 'open', page, pageSize: 20 })}`,
    reloadKey,
  );
  const reload = () => setReloadKey((k) => k + 1);

  async function setAvailable(isAvailable: boolean) {
    await availability.run(() => api.put(deliveryApi.myAvailability, { isAvailable }));
    reload();
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-2xl font-bold tracking-tight">{summary.data ? `Hello, ${summary.data.fullName.split(' ')[0]}` : 'Today'}</h1>
        <BigButton variant="secondary" className="!w-auto" onClick={reload}>
          Refresh
        </BigButton>
      </div>

      {summary.error && <Alert>{summary.error}</Alert>}
      {summary.data && (
        <>
          <p className="text-sm text-slate-600">{summary.data.branchName}</p>
          <div className="grid grid-cols-2 gap-3">
            <Stat label="To deliver" value={summary.data.openAssignments} />
            <Stat label="Delivered today" value={summary.data.deliveredToday} tone="text-emerald-700" />
            <Stat label="Failed today" value={summary.data.failedToday} tone={summary.data.failedToday > 0 ? 'text-red-700' : 'text-slate-900'} />
            <Stat label="Cash collected today" value={formatNpr(summary.data.codCollectedToday)} />
          </div>
          <Panel>
            <p className="text-sm text-slate-600">Cash you are holding (not yet handed in)</p>
            <p className="mt-1 text-2xl font-bold text-amber-900">{formatNpr(summary.data.codCollectedUnsettled)}</p>
          </Panel>
          {availability.error && <Alert>{availability.error}</Alert>}
          <AvailabilityToggle isAvailable={summary.data.isAvailable} busy={availability.busy} onChange={(next) => void setAvailable(next)} />
        </>
      )}

      <h2 className="pt-2 text-lg font-semibold">To deliver</h2>
      <div aria-live="polite" className="space-y-3">
        {list.loading && <p className="text-base text-slate-500">Loading your parcels…</p>}
        {list.error && <Alert>{list.error}</Alert>}
        {list.data && list.data.items.length === 0 && (
          <Panel>
            <p className="text-center text-base text-slate-600">No parcels to deliver right now. A manager will give you new ones.</p>
          </Panel>
        )}
        {list.data?.items.map((a) => (
          <AgentAssignmentCard key={a.id} assignment={a} />
        ))}
      </div>
      {list.data && list.data.totalCount > list.data.pageSize && (
        <Pagination page={list.data.page} totalPages={list.data.totalPages} totalCount={list.data.totalCount} hasNextPage={list.data.hasNextPage} noun="parcel" onPage={setPage} />
      )}

      <p className="pt-2 text-center text-sm text-slate-500">
        Finished deliveries are in{' '}
        <Link to="/agent/history" className="font-semibold text-brand-700 underline">
          History
        </Link>
        .
      </p>
    </div>
  );
}
