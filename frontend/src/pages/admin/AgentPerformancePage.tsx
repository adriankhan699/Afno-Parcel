import { useState, type FormEvent } from 'react';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../components/form';
import { TableShell, Th, theadClass } from '../../components/NetworkBits';
import {
  buildDeliveryQuery,
  defaultPerformanceRange,
  formatAverageHours,
  formatPercent,
  performanceCsv,
  performanceFileName,
  performanceQuery,
  rateBarWidth,
  rateTone,
  rowSuccessRate,
  validatePerformanceRange,
  type PerformanceFilters,
} from '../../lib/delivery';
import { deliveryApi } from '../../lib/deliveryApi';
import { downloadTextFile } from '../../lib/download';
import { formatNpr } from '../../lib/format';
import { useApi } from '../../lib/useApi';
import { useManagerScope } from '../../lib/useManagerScope';
import type { AgentPerformanceReport, AgentPerformanceRow } from '../../types/api';

function SuccessBar({ row }: { row: AgentPerformanceRow }) {
  const rate = rowSuccessRate(row);
  if (rate === null) return <span className="text-slate-500">No attempts</span>;
  return (
    <div className="min-w-32">
      <p className="tabular-nums text-slate-800">{formatPercent(rate)}</p>
      <div
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(rateBarWidth(rate))}
        aria-label={`Delivery success rate of ${row.fullName}`}
        className="mt-1 h-1.5 w-full overflow-hidden rounded-full bg-slate-200"
      >
        <div className={`h-full ${rateTone(rate)}`} style={{ width: `${rateBarWidth(rate)}%` }} />
      </div>
    </div>
  );
}

function Cells({ row }: { row: AgentPerformanceRow }) {
  return (
    <>
      <td className="px-4 py-3 text-right tabular-nums">{row.assigned}</td>
      <td className="px-4 py-3 text-right tabular-nums">{row.delivered}</td>
      <td className="px-4 py-3 text-right tabular-nums">{row.failed}</td>
      <td className="px-4 py-3 text-right tabular-nums">{row.cancelled}</td>
      <td className="px-4 py-3 text-right tabular-nums">{row.inProgress}</td>
      <td className="px-4 py-3">
        <SuccessBar row={row} />
      </td>
      <td className="px-4 py-3 text-right tabular-nums">{formatPercent(row.firstAttemptSuccessPercent)}</td>
      <td className="px-4 py-3 text-right tabular-nums">{formatAverageHours(row.averageDeliveryHours)}</td>
      <td className="px-4 py-3 text-right tabular-nums">
        {formatNpr(row.codCollected)}
        <p className="text-xs text-slate-500">{row.codCollections} collection{row.codCollections === 1 ? '' : 's'}</p>
      </td>
    </>
  );
}

/** How each delivery agent did over a period: deliveries, failures, success rate, speed and cash collected. */
export default function AgentPerformancePage() {
  const scope = useManagerScope();
  const [draft, setDraft] = useState<PerformanceFilters>(() => ({ ...defaultPerformanceRange(new Date()), branchId: '' }));
  const [applied, setApplied] = useState<PerformanceFilters>(draft);
  const [error, setError] = useState<string | null>(null);

  const result = useApi<AgentPerformanceReport>(`${deliveryApi.performance}${buildDeliveryQuery(performanceQuery(applied))}`);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const problem = validatePerformanceRange(draft.from, draft.to);
    setError(problem);
    if (!problem) setApplied(draft);
  }

  function handleReset() {
    const fresh = { ...defaultPerformanceRange(new Date()), branchId: '' };
    setDraft(fresh);
    setApplied(fresh);
    setError(null);
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-lg font-semibold">Agent performance</h2>
          <p className="mt-1 text-sm text-slate-600">
            Counts the parcels given to each agent in the period (Nepal dates). Success rate is delivered divided by delivered plus failed. Average time runs from the moment the parcel was given to the delivery.
          </p>
        </div>
        {result.status === 'loaded' && (
          <SecondaryButton type="button" onClick={() => downloadTextFile(performanceFileName(result.data), performanceCsv(result.data))}>
            Download CSV
          </SecondaryButton>
        )}
      </div>

      <form onSubmit={handleSubmit} role="search" aria-label="Choose the period" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-3">
          <TextField id="perf-from" label="From" type="date" required value={draft.from} onChange={(from) => setDraft((d) => ({ ...d, from }))} />
          <TextField id="perf-to" label="To" type="date" required value={draft.to} onChange={(to) => setDraft((d) => ({ ...d, to }))} error={error ?? undefined} />
          <SelectField
            id="perf-branch"
            label="Branch"
            placeholder={scope.superAdmin ? 'All branches' : 'My branch'}
            value={draft.branchId}
            onChange={(branchId) => setDraft((d) => ({ ...d, branchId }))}
            options={scope.branchOptions}
          />
        </div>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit">Show report</PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Last 30 days
          </SecondaryButton>
        </div>
      </form>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading the report…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.agents.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No delivery agents in this report.</p>
        )}
        {result.status === 'loaded' && result.data.agents.length > 0 && (
          <TableShell>
            <caption className="sr-only">
              Delivery agent performance from {result.data.from} to {result.data.to}
            </caption>
            <thead className={theadClass}>
              <tr>
                <Th>Agent</Th>
                <Th right>Given</Th>
                <Th right>Delivered</Th>
                <Th right>Failed</Th>
                <Th right>Taken back</Th>
                <Th right>In progress</Th>
                <Th>Success rate</Th>
                <Th right>First attempt</Th>
                <Th right>Average time</Th>
                <Th right>Cash collected</Th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {result.data.agents.map((a) => (
                <tr key={a.deliveryAgentId} className="hover:bg-slate-50">
                  <td className="px-4 py-3">
                    <p className="font-medium text-slate-900">
                      {a.fullName}
                      {!a.isActive && <span className="ml-2 rounded-full bg-slate-200 px-2 py-0.5 text-xs font-semibold text-slate-600">Inactive</span>}
                    </p>
                    <p className="text-xs text-slate-500">{a.branchName}</p>
                  </td>
                  <Cells row={a} />
                </tr>
              ))}
            </tbody>
            <tfoot className="border-t-2 border-slate-300 bg-slate-50 font-semibold">
              <tr>
                <th scope="row" className="px-4 py-3 text-left">
                  Total
                </th>
                <Cells row={result.data.totals} />
              </tr>
            </tfoot>
          </TableShell>
        )}
      </div>
    </div>
  );
}
