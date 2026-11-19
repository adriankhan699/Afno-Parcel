import { useState } from 'react';
import { AgentAssignmentCard } from '../../components/agent/AgentAssignmentCard';
import { Panel } from '../../components/agent/AgentBits';
import { Alert } from '../../components/form';
import Pagination from '../../components/Pagination';
import { buildDeliveryQuery } from '../../lib/delivery';
import { deliveryApi } from '../../lib/deliveryApi';
import { useStableApi } from '../../lib/useStableApi';
import type { AgentAssignmentResponse, PagedResponse } from '../../types/api';

/** Deliveries the agent has finished (delivered, failed or taken back). The receiver's phone and address are no longer shown. */
export default function AgentHistoryPage() {
  const [page, setPage] = useState(1);
  const list = useStableApi<PagedResponse<AgentAssignmentResponse>>(
    `${deliveryApi.myAssignments}${buildDeliveryQuery({ scope: 'history', page, pageSize: 20 })}`,
  );

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold tracking-tight">History</h1>
      <div aria-live="polite" className="space-y-3">
        {list.loading && <p className="text-base text-slate-500">Loading…</p>}
        {list.error && <Alert>{list.error}</Alert>}
        {list.data && list.data.items.length === 0 && (
          <Panel>
            <p className="text-center text-base text-slate-600">Nothing here yet. Finished deliveries will appear here.</p>
          </Panel>
        )}
        {list.data?.items.map((a) => (
          <AgentAssignmentCard key={a.id} assignment={a} showFinished />
        ))}
      </div>
      {list.data && list.data.totalCount > 0 && (
        <Pagination page={list.data.page} totalPages={list.data.totalPages} totalCount={list.data.totalCount} hasNextPage={list.data.hasNextPage} noun="delivery" onPage={setPage} />
      )}
    </div>
  );
}
