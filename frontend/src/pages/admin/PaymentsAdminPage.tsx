import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../components/form';
import { TableShell, Th, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import { buildDeliveryQuery } from '../../lib/delivery';
import { downloadTextFile } from '../../lib/download';
import { formatNepalDateTime, formatNpr } from '../../lib/format';
import {
  PROVIDER_OPTIONS,
  TRANSACTION_STATUS_OPTIONS,
  emptyTransactionFilters,
  providerLabel,
  transactionQuery,
  transactionStatusLabel,
  transactionStatusTone,
  transactionsCsv,
  transactionsFileName,
  validateTransactionRange,
  type TransactionFilters,
} from '../../lib/payment';
import { paymentApi } from '../../lib/paymentApi';
import { useApi } from '../../lib/useApi';
import { useFinanceScope } from '../../lib/useFinanceScope';
import type { PagedResponse, PaymentTransactionRow } from '../../types/api';

/** Every payment attempt and cash movement, for the accountant: filter, page through, export. */
export default function PaymentsAdminPage() {
  const scope = useFinanceScope();
  const [draft, setDraft] = useState<TransactionFilters>(emptyTransactionFilters);
  const [applied, setApplied] = useState<TransactionFilters>(emptyTransactionFilters);
  const [page, setPage] = useState(1);
  const [rangeError, setRangeError] = useState<string | null>(null);

  const result = useApi<PagedResponse<PaymentTransactionRow>>(`${paymentApi.transactions}${buildDeliveryQuery(transactionQuery(applied, page))}`);
  const set = (patch: Partial<TransactionFilters>) => setDraft((d) => ({ ...d, ...patch }));

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const problem = validateTransactionRange(draft.from, draft.to);
    setRangeError(problem);
    if (problem) return;
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyTransactionFilters());
    setApplied(emptyTransactionFilters());
    setRangeError(null);
    setPage(1);
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-lg font-semibold">Payment transactions</h2>
          <p className="mt-1 text-sm text-slate-600">
            Every online attempt, cash payment and cash on delivery, newest first. An online payment only shows as paid after the server verified it with the provider.
          </p>
        </div>
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <SecondaryButton type="button" onClick={() => downloadTextFile(transactionsFileName(result.data.page), transactionsCsv(result.data.items))}>
            Download this page as CSV
          </SecondaryButton>
        )}
      </div>

      <form onSubmit={handleSubmit} role="search" aria-label="Filter transactions" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <TextField id="tx-search" label="Search" type="search" placeholder="Reference, provider reference or tracking number" value={draft.search} onChange={(search) => set({ search })} />
          <SelectField id="tx-provider" label="Provider" placeholder="Any provider" value={draft.provider} onChange={(provider) => set({ provider })} options={PROVIDER_OPTIONS} />
          <SelectField id="tx-status" label="Status" placeholder="Any status" value={draft.status} onChange={(status) => set({ status })} options={TRANSACTION_STATUS_OPTIONS} />
          <TextField id="tx-from" label="From" type="date" value={draft.from} onChange={(from) => set({ from })} />
          <TextField id="tx-to" label="To" type="date" value={draft.to} onChange={(to) => set({ to })} error={rangeError ?? undefined} />
          {scope.canChooseBranch && (
            <SelectField id="tx-branch" label="Branch" placeholder="All branches" value={draft.branchId} onChange={(branchId) => set({ branchId })} options={scope.branchOptions} />
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
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading transactions…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No transactions match these filters.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <caption className="sr-only">Payment transactions, page {result.data.page}</caption>
              <thead className={theadClass}>
                <tr>
                  <Th>Started</Th>
                  <Th>Parcel</Th>
                  <Th>Reference</Th>
                  <Th>Provider</Th>
                  <Th>Status</Th>
                  <Th>Provider reference</Th>
                  <Th right>Amount</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((t) => (
                  <tr key={t.id} className="hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3 text-slate-700">
                      {formatNepalDateTime(t.createdAtUtc)}
                      {t.completedAtUtc && <p className="text-xs text-slate-500">done {formatNepalDateTime(t.completedAtUtc)}</p>}
                    </td>
                    <td className="px-4 py-3">
                      <Link to={`/parcels/${t.parcelId}`} className="font-mono text-xs font-medium text-brand-700 underline">
                        {t.trackingNumber}
                      </Link>
                    </td>
                    <td className="px-4 py-3 font-mono text-xs text-slate-700">{t.reference}</td>
                    <td className="whitespace-nowrap px-4 py-3">
                      {providerLabel(t.provider)}
                      {t.type !== 'Charge' && <span className="ml-1 text-xs text-slate-500">({t.type})</span>}
                    </td>
                    <td className="px-4 py-3">
                      <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${transactionStatusTone(t.status)}`}>{transactionStatusLabel(t.status)}</span>
                      {t.failureReason && <p className="mt-1 max-w-56 text-xs text-red-700">{t.failureReason}</p>}
                    </td>
                    <td className="px-4 py-3 font-mono text-xs text-slate-700">{t.providerReference ?? '—'}</td>
                    <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums">{formatNpr(t.amount)}</td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="transaction" onPage={setPage} />
          </>
        )}
      </div>
    </div>
  );
}
