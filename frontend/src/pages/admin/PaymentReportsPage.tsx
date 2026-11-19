import { useState, type FormEvent, type ReactNode } from 'react';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../components/form';
import { TableShell, Th, theadClass } from '../../components/NetworkBits';
import { buildDeliveryQuery } from '../../lib/delivery';
import { downloadTextFile } from '../../lib/download';
import { formatNpr } from '../../lib/format';
import { paymentLabel } from '../../lib/labels';
import {
  barPercent,
  defaultReportFilters,
  maxOf,
  providerLabel,
  reportCsv,
  reportFileName,
  reportQuery,
  settlementStatusLabel,
  successShare,
  validateReportRange,
  type ReportFilters,
} from '../../lib/payment';
import { paymentApi } from '../../lib/paymentApi';
import { useApi } from '../../lib/useApi';
import { useFinanceScope } from '../../lib/useFinanceScope';
import type { PaymentReportResponse } from '../../types/api';

function Kpi({ label, value, sub, tone = 'text-slate-900' }: { label: string; value: ReactNode; sub?: ReactNode; tone?: string }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</p>
      <p className={`mt-1 text-2xl font-bold tabular-nums ${tone}`}>{value}</p>
      {sub && <p className="mt-0.5 text-xs text-slate-500">{sub}</p>}
    </div>
  );
}

/** A thin bar for the share of a group's largest value; the number next to it carries the meaning, the bar is only a picture. */
function Bar({ percent, label, tone = 'bg-brand-500' }: { percent: number; label: string; tone?: string }) {
  return (
    <div role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(percent)} aria-label={label} className="mt-1 h-1.5 w-full min-w-24 overflow-hidden rounded-full bg-slate-200">
      <div className={`h-full ${tone}`} style={{ width: `${percent}%` }} />
    </div>
  );
}

/** What was paid, what is waiting, and where the cash on delivery stands, for the accountant. */
export default function PaymentReportsPage() {
  const scope = useFinanceScope();
  const [draft, setDraft] = useState<ReportFilters>(() => defaultReportFilters(new Date()));
  const [applied, setApplied] = useState<ReportFilters>(draft);
  const [error, setError] = useState<string | null>(null);

  const result = useApi<PaymentReportResponse>(`${paymentApi.reportSummary}${buildDeliveryQuery(reportQuery(applied))}`);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const problem = validateReportRange(draft.from, draft.to);
    setError(problem);
    if (!problem) setApplied(draft);
  }

  function handleReset() {
    const fresh = defaultReportFilters(new Date());
    setDraft(fresh);
    setApplied(fresh);
    setError(null);
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-lg font-semibold">Payment reports</h2>
          <p className="mt-1 text-sm text-slate-600">
            Delivery charges are counted on the day they were paid (Nepal dates); what is still unpaid is counted by booking date. Cash on delivery is the money collected by agents.
          </p>
        </div>
        {result.status === 'loaded' && (
          <SecondaryButton type="button" onClick={() => downloadTextFile(reportFileName(result.data), reportCsv(result.data))}>
            Download CSV
          </SecondaryButton>
        )}
      </div>

      <form onSubmit={handleSubmit} role="search" aria-label="Choose the period" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-3">
          <TextField id="rep-from" label="From" type="date" required value={draft.from} onChange={(from) => setDraft((d) => ({ ...d, from }))} />
          <TextField id="rep-to" label="To" type="date" required value={draft.to} onChange={(to) => setDraft((d) => ({ ...d, to }))} error={error ?? undefined} />
          {scope.canChooseBranch ? (
            <SelectField id="rep-branch" label="Branch" placeholder="All branches" value={draft.branchId} onChange={(branchId) => setDraft((d) => ({ ...d, branchId }))} options={scope.branchOptions} />
          ) : (
            <p className="self-end text-sm text-slate-600">Showing your own branch.</p>
          )}
        </div>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit">Show report</PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Last 30 days
          </SecondaryButton>
        </div>
      </form>

      <div aria-live="polite" className="space-y-8">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading the report…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && <Report report={result.data} />}
      </div>
    </div>
  );
}

function Report({ report }: { report: PaymentReportResponse }) {
  const methodMax = maxOf(report.byMethod.map((m) => m.amount));
  const providerMax = maxOf(report.byProvider.map((p) => p.amount));
  const dailyMax = maxOf(report.daily.map((d) => d.amount));
  const empty = report.paidCount === 0 && report.pendingCount === 0 && report.failedAttempts === 0 && report.cod.collectionCount === 0;

  return (
    <>
      <section aria-label="Payments" className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Kpi label="Paid" value={formatNpr(report.paidAmount)} sub={`${report.paidCount} payment${report.paidCount === 1 ? '' : 's'}`} tone="text-emerald-700" />
        <Kpi label="Waiting to be paid" value={formatNpr(report.pendingAmount)} sub={`${report.pendingCount} parcel${report.pendingCount === 1 ? '' : 's'}`} tone="text-amber-700" />
        <Kpi label="Failed online attempts" value={report.failedAttempts} tone={report.failedAttempts > 0 ? 'text-red-700' : 'text-slate-900'} />
        <Kpi label="Cash on delivery collected" value={formatNpr(report.cod.collected)} sub={`${report.cod.collectionCount} collection${report.cod.collectionCount === 1 ? '' : 's'}`} />
      </section>

      {empty && <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">Nothing was paid, collected or attempted in this period.</p>}

      <section aria-labelledby="by-method">
        <h3 id="by-method" className="mb-2 text-base font-semibold">
          By payment method
        </h3>
        {report.byMethod.length === 0 ? (
          <p className="text-sm text-slate-600">No payments in this period.</p>
        ) : (
          <TableShell>
            <thead className={theadClass}>
              <tr>
                <Th>Method</Th>
                <Th right>Payments</Th>
                <Th right>Amount</Th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {report.byMethod.map((m) => (
                <tr key={m.method}>
                  <td className="px-4 py-3">
                    {paymentLabel(m.method)}
                    <Bar percent={barPercent(m.amount, methodMax)} label={`${paymentLabel(m.method)} share of the largest method`} />
                  </td>
                  <td className="px-4 py-3 text-right tabular-nums">{m.count}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{formatNpr(m.amount)}</td>
                </tr>
              ))}
            </tbody>
          </TableShell>
        )}
      </section>

      <section aria-labelledby="by-provider">
        <h3 id="by-provider" className="mb-2 text-base font-semibold">
          By provider
        </h3>
        {report.byProvider.length === 0 ? (
          <p className="text-sm text-slate-600">No provider transactions in this period.</p>
        ) : (
          <TableShell>
            <thead className={theadClass}>
              <tr>
                <Th>Provider</Th>
                <Th right>Succeeded</Th>
                <Th right>Failed</Th>
                <Th right>Waiting</Th>
                <Th right>Success rate</Th>
                <Th right>Amount</Th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {report.byProvider.map((p) => {
                const share = successShare(p);
                return (
                  <tr key={p.provider}>
                    <td className="px-4 py-3">
                      {providerLabel(p.provider)}
                      <Bar percent={barPercent(p.amount, providerMax)} label={`${providerLabel(p.provider)} share of the largest provider`} />
                    </td>
                    <td className="px-4 py-3 text-right tabular-nums">{p.succeeded}</td>
                    <td className="px-4 py-3 text-right tabular-nums">{p.failed}</td>
                    <td className="px-4 py-3 text-right tabular-nums">{p.pending}</td>
                    <td className="px-4 py-3 text-right tabular-nums">{share === null ? '—' : `${share}%`}</td>
                    <td className="px-4 py-3 text-right tabular-nums">{formatNpr(p.amount)}</td>
                  </tr>
                );
              })}
            </tbody>
          </TableShell>
        )}
      </section>

      <section aria-labelledby="by-day">
        <h3 id="by-day" className="mb-2 text-base font-semibold">
          Day by day
        </h3>
        {report.daily.length === 0 ? (
          <p className="text-sm text-slate-600">No payments in this period.</p>
        ) : (
          <TableShell>
            <thead className={theadClass}>
              <tr>
                <Th>Day</Th>
                <Th right>Payments</Th>
                <Th right>Amount</Th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {report.daily.map((d) => (
                <tr key={d.date}>
                  <td className="px-4 py-3">
                    <span className="tabular-nums">{d.date}</span>
                    <Bar percent={barPercent(d.amount, dailyMax)} label={`Amount paid on ${d.date}`} />
                  </td>
                  <td className="px-4 py-3 text-right tabular-nums">{d.count}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{formatNpr(d.amount)}</td>
                </tr>
              ))}
            </tbody>
          </TableShell>
        )}
      </section>

      <section aria-labelledby="cod-position">
        <h3 id="cod-position" className="mb-2 text-base font-semibold">
          Cash on delivery position
        </h3>
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <Kpi label="With agents" value={formatNpr(report.cod.inHand)} sub="collected, not handed in" tone="text-amber-700" />
          <Kpi label="Waiting for count" value={formatNpr(report.cod.inSettlement)} sub="handed in to the accountant" />
          <Kpi label="Count did not match" value={formatNpr(report.cod.disputed)} sub="needs a decision" tone={report.cod.disputed > 0 ? 'text-red-700' : 'text-slate-900'} />
          <Kpi label="Counted and accepted" value={formatNpr(report.cod.settled)} tone="text-emerald-700" />
        </div>
      </section>

      <section aria-labelledby="settlement-counts">
        <h3 id="settlement-counts" className="mb-2 text-base font-semibold">
          Settlements
        </h3>
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <Kpi label={settlementStatusLabel('Submitted')} value={report.settlements.submitted} />
          <Kpi label={settlementStatusLabel('Verified')} value={report.settlements.verified} tone="text-emerald-700" />
          <Kpi label={settlementStatusLabel('Disputed')} value={report.settlements.disputed} tone={report.settlements.disputed > 0 ? 'text-red-700' : 'text-slate-900'} />
          <Kpi label="Total variance" value={formatNpr(report.settlements.totalVariance)} sub="counted minus expected" tone={report.settlements.totalVariance === 0 ? 'text-slate-900' : 'text-red-700'} />
        </div>
      </section>
    </>
  );
}
