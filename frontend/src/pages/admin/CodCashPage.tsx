import { useState, type FormEvent } from 'react';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../components/form';
import { TableShell, Th, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import { ResolveSettlementDialog, SettlementDetailDialog, SettlementStatusChip, VerifySettlementDialog } from '../../components/SettlementDialogs';
import { buildDeliveryQuery } from '../../lib/delivery';
import { downloadTextFile } from '../../lib/download';
import { formatNepalDateTime, formatNpr } from '../../lib/format';
import {
  SETTLEMENT_STATUS_OPTIONS,
  agentCashCsv,
  agentCashFileName,
  agentCashQuery,
  canResolveSettlement,
  canVerifySettlement,
  emptySettlementFilters,
  settlementQuery,
  validateTransactionRange,
  varianceText,
  varianceTone,
  type SettlementFilters,
} from '../../lib/payment';
import { paymentApi } from '../../lib/paymentApi';
import { useApi } from '../../lib/useApi';
import { useFinanceScope } from '../../lib/useFinanceScope';
import type { AgentCashPositionRow, PagedResponse, SettlementResponse } from '../../types/api';

type Tab = 'cash' | 'settlements';

/** Cash on delivery: where each agent's cash stands, and the hand-ins the accountant has to count. */
export default function CodCashPage() {
  const [tab, setTab] = useState<Tab>('cash');
  const [notice, setNotice] = useState<string | null>(null);
  // Bumped after a count or a resolution, so both tabs show the new position.
  const [reloadKey, setReloadKey] = useState(0);

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold">Cash on delivery</h2>
        <p className="mt-1 text-sm text-slate-600">
          Agents collect cash from receivers and hand it in at the branch. Count each hand-in here: a count that matches settles the cash, one that does not is marked as disputed until it is resolved.
        </p>
      </div>

      {notice && <Alert tone="success">{notice}</Alert>}

      <div role="tablist" aria-label="Cash on delivery" className="flex gap-1 border-b border-slate-200">
        {(
          [
            ['cash', 'Agent cash'],
            ['settlements', 'Settlements'],
          ] as const
        ).map(([key, label]) => (
          <button
            key={key}
            id={`cod-tab-${key}`}
            role="tab"
            type="button"
            aria-selected={tab === key}
            aria-controls={`cod-panel-${key}`}
            onClick={() => setTab(key)}
            className={`border-b-2 px-3 py-2 text-sm font-medium ${tab === key ? 'border-brand-600 text-brand-700' : 'border-transparent text-slate-600 hover:text-slate-900'}`}
          >
            {label}
          </button>
        ))}
      </div>

      <div role="tabpanel" id={`cod-panel-${tab}`} aria-labelledby={`cod-tab-${tab}`}>
        {tab === 'cash' ? (
          <AgentCashTab reloadKey={reloadKey} />
        ) : (
          <SettlementsTab
            reloadKey={reloadKey}
            onChanged={(message) => {
              setNotice(message);
              setReloadKey((k) => k + 1);
            }}
          />
        )}
      </div>
    </div>
  );
}

function Money({ value, tone = '' }: { value: number; tone?: string }) {
  return <td className={`whitespace-nowrap px-4 py-3 text-right tabular-nums ${tone}`}>{formatNpr(value)}</td>;
}

function AgentCashTab({ reloadKey }: { reloadKey: number }) {
  const scope = useFinanceScope();
  const [branchId, setBranchId] = useState('');
  const result = useApi<AgentCashPositionRow[]>(`${paymentApi.agentCash}${buildDeliveryQuery(agentCashQuery(branchId))}`, reloadKey);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        {scope.canChooseBranch ? (
          <SelectField id="cash-branch" label="Branch" className="w-full sm:w-72" placeholder="All branches" value={branchId} onChange={setBranchId} options={scope.branchOptions} />
        ) : (
          <p className="text-sm text-slate-600">Showing the agents of your own branch.</p>
        )}
        {result.status === 'loaded' && result.data.length > 0 && (
          <SecondaryButton type="button" onClick={() => downloadTextFile(agentCashFileName, agentCashCsv(result.data))}>
            Download CSV
          </SecondaryButton>
        )}
      </div>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No agent has collected cash yet.</p>
        )}
        {result.status === 'loaded' && result.data.length > 0 && (
          <TableShell>
            <caption className="sr-only">Cash position of each delivery agent</caption>
            <thead className={theadClass}>
              <tr>
                <Th>Agent</Th>
                <Th right>In hand</Th>
                <Th right>Waiting for count</Th>
                <Th right>Disputed</Th>
                <Th right>Settled</Th>
                <Th right>Collected in total</Th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {result.data.map((r) => (
                <tr key={r.agentId} className="hover:bg-slate-50">
                  <td className="px-4 py-3">
                    <p className="font-medium text-slate-900">{r.fullName}</p>
                    <p className="text-xs text-slate-500">
                      {r.branchName} · {r.collectionCount} collection{r.collectionCount === 1 ? '' : 's'}
                      {r.lastCollectionAtUtc && <> · last {formatNepalDateTime(r.lastCollectionAtUtc)}</>}
                    </p>
                  </td>
                  <Money value={r.inHand} tone={r.inHand > 0 ? 'font-semibold text-amber-800' : ''} />
                  <Money value={r.inSettlement} />
                  <Money value={r.disputed} tone={r.disputed > 0 ? 'font-semibold text-red-700' : ''} />
                  <Money value={r.settled} tone="text-emerald-700" />
                  <Money value={r.collected} />
                </tr>
              ))}
            </tbody>
            <tfoot className="border-t-2 border-slate-300 bg-slate-50 font-semibold">
              <tr>
                <th scope="row" className="px-4 py-3 text-left">
                  Total
                </th>
                <Money value={result.data.reduce((sum, r) => sum + r.inHand, 0)} />
                <Money value={result.data.reduce((sum, r) => sum + r.inSettlement, 0)} />
                <Money value={result.data.reduce((sum, r) => sum + r.disputed, 0)} />
                <Money value={result.data.reduce((sum, r) => sum + r.settled, 0)} />
                <Money value={result.data.reduce((sum, r) => sum + r.collected, 0)} />
              </tr>
            </tfoot>
          </TableShell>
        )}
      </div>
    </div>
  );
}

function SettlementsTab({ reloadKey, onChanged }: { reloadKey: number; onChanged: (message: string) => void }) {
  const scope = useFinanceScope();
  const [draft, setDraft] = useState<SettlementFilters>(emptySettlementFilters);
  const [applied, setApplied] = useState<SettlementFilters>(emptySettlementFilters);
  const [page, setPage] = useState(1);
  const [rangeError, setRangeError] = useState<string | null>(null);
  const [detailId, setDetailId] = useState<string | null>(null);
  const [verifying, setVerifying] = useState<SettlementResponse | null>(null);
  const [resolving, setResolving] = useState<SettlementResponse | null>(null);

  const result = useApi<PagedResponse<SettlementResponse>>(`${paymentApi.settlements}${buildDeliveryQuery(settlementQuery(applied, page))}`, reloadKey);
  const set = (patch: Partial<SettlementFilters>) => setDraft((d) => ({ ...d, ...patch }));

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const problem = validateTransactionRange(draft.from, draft.to);
    setRangeError(problem);
    if (problem) return;
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptySettlementFilters());
    setApplied(emptySettlementFilters());
    setRangeError(null);
    setPage(1);
  }

  function done(updated: SettlementResponse) {
    onChanged(
      updated.status === 'Verified'
        ? `${updated.referenceNumber} is counted and accepted.`
        : `${updated.referenceNumber} is marked as disputed (${varianceText(updated.variance)}).`,
    );
  }

  return (
    <div className="space-y-4">
      <form onSubmit={handleSubmit} role="search" aria-label="Filter settlements" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <SelectField id="set-status" label="Status" placeholder="Any status" value={draft.status} onChange={(status) => set({ status })} options={SETTLEMENT_STATUS_OPTIONS} />
          <TextField id="set-from" label="From" type="date" value={draft.from} onChange={(from) => set({ from })} />
          <TextField id="set-to" label="To" type="date" value={draft.to} onChange={(to) => set({ to })} error={rangeError ?? undefined} />
          {scope.canChooseBranch && (
            <SelectField id="set-branch" label="Branch" placeholder="All branches" value={draft.branchId} onChange={(branchId) => set({ branchId })} options={scope.branchOptions} />
          )}
        </div>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit">Apply filters</PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Clear
          </SecondaryButton>
        </div>
      </form>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading settlements…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No cash hand-ins match these filters.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <caption className="sr-only">Cash hand-ins</caption>
              <thead className={theadClass}>
                <tr>
                  <Th>Hand-in</Th>
                  <Th>Agent</Th>
                  <Th right>Handed in</Th>
                  <Th right>Counted</Th>
                  <Th>Status</Th>
                  <Th>
                    <span className="sr-only">Actions</span>
                  </Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((s) => (
                  <tr key={s.id} className="hover:bg-slate-50">
                    <td className="px-4 py-3">
                      <p className="font-mono text-xs font-medium text-slate-900">{s.referenceNumber}</p>
                      <p className="text-xs text-slate-500">
                        {s.collectionCount} collection{s.collectionCount === 1 ? '' : 's'}
                        {s.submittedAtUtc && <> · {formatNepalDateTime(s.submittedAtUtc)}</>}
                      </p>
                    </td>
                    <td className="px-4 py-3">
                      {s.agentName ?? '—'}
                      <p className="text-xs text-slate-500">{s.branchName}</p>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums">{formatNpr(s.totalAmount)}</td>
                    <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums">
                      {s.countedAmount === null || s.countedAmount === undefined ? '—' : formatNpr(s.countedAmount)}
                      {s.variance !== null && s.variance !== undefined && s.variance !== 0 && <p className={`text-xs ${varianceTone(s.variance)}`}>{varianceText(s.variance)}</p>}
                    </td>
                    <td className="px-4 py-3">
                      <SettlementStatusChip status={s.status} />
                    </td>
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap justify-end gap-2">
                        <SecondaryButton type="button" onClick={() => setDetailId(s.id)}>
                          Details
                        </SecondaryButton>
                        {canVerifySettlement(s.status) && (
                          <PrimaryButton type="button" onClick={() => setVerifying(s)}>
                            Verify count
                          </PrimaryButton>
                        )}
                        {canResolveSettlement(s.status) && (
                          <PrimaryButton type="button" onClick={() => setResolving(s)}>
                            Resolve
                          </PrimaryButton>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="hand-in" onPage={setPage} />
          </>
        )}
      </div>

      <SettlementDetailDialog settlementId={detailId} onClose={() => setDetailId(null)} />
      <VerifySettlementDialog settlement={verifying} onClose={() => setVerifying(null)} onDone={done} />
      <ResolveSettlementDialog settlement={resolving} onClose={() => setResolving(null)} onDone={done} />
    </div>
  );
}
