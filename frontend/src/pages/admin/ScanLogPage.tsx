import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../components/form';
import { TableShell, Th, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import { adminApi } from '../../lib/adminApi';
import { formatNepalDateTime } from '../../lib/format';
import { buildQuery, emptyScanLogFilters, isRangeInvalid, scanLogQuery, type ScanLogFilters } from '../../lib/network';
import { OUTCOME_LABELS, SCAN_TYPE_OPTIONS, outcomeOf, outcomeTone } from '../../lib/scan';
import { useApi } from '../../lib/useApi';
import { SCAN_OUTCOMES, type PagedResponse, type ParcelScanResponse, type WarehouseResponse } from '../../types/api';

const scanTypeLabel = (value: string) => SCAN_TYPE_OPTIONS.find((o) => o.value === value)?.label ?? value;

export default function ScanLogPage() {
  const [draft, setDraft] = useState<ScanLogFilters>(emptyScanLogFilters);
  const [applied, setApplied] = useState<ScanLogFilters>(emptyScanLogFilters);
  const [page, setPage] = useState(1);
  const [rangeError, setRangeError] = useState<string | null>(null);

  const warehouses = useApi<PagedResponse<WarehouseResponse>>(`${adminApi.warehouses}?page=1&pageSize=100`);
  const result = useApi<PagedResponse<ParcelScanResponse>>(`${adminApi.scanLog}${buildQuery(scanLogQuery(applied, page))}`);
  const set = (patch: Partial<ScanLogFilters>) => setDraft((d) => ({ ...d, ...patch }));

  const warehouseOptions = warehouses.status === 'loaded' ? warehouses.data.items.map((w) => ({ value: w.id, label: `${w.name} (${w.code})` })) : [];

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (isRangeInvalid(draft.from, draft.to)) {
      setRangeError('The "from" date must not be after the "to" date.');
      return;
    }
    setRangeError(null);
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyScanLogFilters());
    setApplied(emptyScanLogFilters());
    setRangeError(null);
    setPage(1);
  }

  return (
    <div className="space-y-6">
      <h2 className="text-lg font-semibold">Scan log</h2>

      <form onSubmit={handleSubmit} role="search" aria-label="Filter scan log" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <TextField id="log-tracking" label="Tracking number" type="search" placeholder="AFN…" value={draft.trackingNumber} onChange={(trackingNumber) => set({ trackingNumber })} />
          <SelectField id="log-warehouse" label="Warehouse" placeholder="All warehouses" value={draft.warehouseId} onChange={(warehouseId) => set({ warehouseId })} options={warehouseOptions} />
          <SelectField
            id="log-type"
            label="Scan type"
            placeholder="Any type"
            value={draft.scanType}
            onChange={(scanType) => set({ scanType })}
            options={SCAN_TYPE_OPTIONS.map((o) => ({ value: o.value, label: o.label }))}
          />
          <SelectField
            id="log-outcome"
            label="Outcome"
            placeholder="Any outcome"
            value={draft.outcome}
            onChange={(outcome) => set({ outcome })}
            options={SCAN_OUTCOMES.map((o) => ({ value: o, label: OUTCOME_LABELS[o] }))}
          />
          <TextField id="log-from" label="From (Nepal date)" type="date" value={draft.from} onChange={(from) => set({ from })} />
          <TextField id="log-to" label="To (Nepal date)" type="date" value={draft.to} onChange={(to) => set({ to })} error={rangeError ?? undefined} />
        </div>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit">Apply filters</PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Clear
          </SecondaryButton>
        </div>
      </form>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading scans…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No scans match these filters.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <thead className={theadClass}>
                <tr>
                  <Th>When</Th>
                  <Th>Tracking number</Th>
                  <Th>Warehouse</Th>
                  <Th>Scan type</Th>
                  <Th>Outcome</Th>
                  <Th>Staff</Th>
                  <Th>Remarks</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((s) => {
                  const outcome = outcomeOf({ outcome: s.outcome, succeeded: s.outcome !== 'Failed' });
                  return (
                    <tr key={s.id} className="hover:bg-slate-50">
                      <td className="whitespace-nowrap px-4 py-3">{formatNepalDateTime(s.scannedAtUtc)}</td>
                      <td className="whitespace-nowrap px-4 py-3">
                        <Link to={`/parcels/${s.parcelId}`} className="font-mono font-medium text-brand-700 underline">
                          {s.trackingNumber}
                        </Link>
                      </td>
                      <td className="px-4 py-3">{s.warehouseName}</td>
                      <td className="whitespace-nowrap px-4 py-3">{scanTypeLabel(s.scanType)}</td>
                      <td className="px-4 py-3">
                        <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${outcomeTone(outcome)}`}>{OUTCOME_LABELS[outcome]}</span>
                      </td>
                      <td className="px-4 py-3">{s.staffName ?? '—'}</td>
                      <td className="px-4 py-3 text-slate-600">{s.remarks ?? '—'}</td>
                    </tr>
                  );
                })}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="scan" onPage={setPage} />
          </>
        )}
      </div>
    </div>
  );
}
