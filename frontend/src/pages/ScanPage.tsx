import { useRef, useState, type FormEvent } from 'react';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextAreaField } from '../components/form';
import { ApiError, api } from '../lib/apiClient';
import { problemSummary } from '../lib/problems';
import {
  MAX_BATCH_SIZE,
  SCAN_TYPE_OPTIONS,
  addScans,
  buildBatchRequest,
  remainingAfterScan,
  validateScanForm,
} from '../lib/scan';
import { statusLabel } from '../lib/labels';
import { useApi } from '../lib/useApi';
import type { BatchScanResponse, PagedResponse, ScanType, WarehouseResponse } from '../types/api';

export default function ScanPage() {
  const warehouses = useApi<PagedResponse<WarehouseResponse>>('/api/v1/warehouses?page=1&pageSize=50');
  const inputRef = useRef<HTMLInputElement>(null);

  const [warehouseId, setWarehouseId] = useState('');
  const [scanType, setScanType] = useState<ScanType | ''>('');
  const [remarks, setRemarks] = useState('');
  const [list, setList] = useState<string[]>([]);
  const [entry, setEntry] = useState('');
  const [notice, setNotice] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<BatchScanResponse | null>(null);

  const warehouseOptions =
    warehouses.status === 'loaded' ? warehouses.data.items.filter((w) => w.isActive).map((w) => ({ value: w.id, label: `${w.name} (${w.code})` })) : [];
  const chosenType = SCAN_TYPE_OPTIONS.find((o) => o.value === scanType);

  // A scanner "types" the number and presses Enter, which submits this small form.
  function handleAdd(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!entry.trim()) return;

    const result = addScans(list, entry);
    setList(result.list);
    setEntry('');
    setOutcome(null);

    const problems: string[] = [];
    if (result.invalid.length > 0) problems.push(`Not a tracking number: ${result.invalid.join(', ')}`);
    if (result.duplicates.length > 0) problems.push(`Already in the list: ${result.duplicates.join(', ')}`);
    if (result.rejectedFull.length > 0) problems.push(`The list is full (${MAX_BATCH_SIZE} parcels). Submit these first.`);
    setNotice(problems.length > 0 ? problems.join(' · ') : null);
    inputRef.current?.focus();
  }

  async function handleSubmit() {
    const form = { scanType, warehouseId, remarks, trackingNumbers: list };
    const found = validateScanForm(form);
    setErrors(found);
    setSummary(null);
    if (Object.keys(found).length > 0 || scanType === '') return;

    setBusy(true);
    try {
      const response = await api.post<BatchScanResponse>('/api/v1/scans/batch', buildBatchRequest({ ...form, scanType }));
      setOutcome(response);
      // Keep only the parcels that failed, so they can be fixed and sent again.
      setList(remainingAfterScan(list, response.results));
    } catch (err) {
      setSummary(
        err instanceof ApiError
          ? err.status === 403
            ? 'Your role is not allowed to make this kind of scan.'
            : problemSummary(err.problem)
          : 'Could not reach the server. Please try again shortly.',
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">Warehouse scan</h1>
        <p className="mt-1 text-sm text-slate-600">
          Choose the warehouse and what you are doing, scan the parcels, then submit. Each scan is saved in the parcel&apos;s history.
        </p>
      </div>

      {summary && <Alert>{summary}</Alert>}
      {warehouses.status === 'error' && <Alert>Could not load the warehouses. {warehouses.message}</Alert>}
      {warehouses.status === 'loaded' && warehouseOptions.length === 0 && (
        <Alert tone="info">No warehouses have been set up yet. A branch manager can add one first.</Alert>
      )}

      <section className="grid gap-4 rounded-xl border border-slate-200 bg-white p-5 shadow-sm sm:grid-cols-2">
        <SelectField
          id="warehouse"
          label="Warehouse"
          required
          placeholder="Choose a warehouse"
          value={warehouseId}
          onChange={setWarehouseId}
          options={warehouseOptions}
          error={errors.warehouseId}
        />
        <SelectField
          id="scanType"
          label="Scan type"
          required
          placeholder="Choose a scan type"
          value={scanType}
          onChange={(value) => setScanType(value as ScanType | '')}
          options={SCAN_TYPE_OPTIONS.map((o) => ({ value: o.value, label: o.label }))}
          error={errors.scanType}
          hint={chosenType?.hint}
        />
        <TextAreaField
          id="scanRemarks"
          label="Remarks (optional)"
          rows={2}
          maxLength={500}
          className="sm:col-span-2"
          value={remarks}
          onChange={setRemarks}
          error={errors.remarks}
        />
      </section>

      <section aria-labelledby="scan-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
        <h2 id="scan-heading" className="text-base font-semibold text-slate-900">
          Parcels to scan ({list.length}/{MAX_BATCH_SIZE})
        </h2>

        <form onSubmit={handleAdd} className="mt-3 flex flex-col gap-2 sm:flex-row">
          <label htmlFor="scanEntry" className="sr-only">
            Tracking number
          </label>
          <input
            id="scanEntry"
            ref={inputRef}
            value={entry}
            onChange={(e) => setEntry(e.target.value)}
            autoFocus
            autoComplete="off"
            placeholder="Scan or type a tracking number, then press Enter"
            className="flex-1 rounded-lg border border-slate-300 bg-white px-4 py-3 font-mono text-lg uppercase tracking-wide outline-none focus:border-brand-600 focus:ring-2 focus:ring-brand-100"
          />
          <SecondaryButton type="submit">Add</SecondaryButton>
        </form>
        {notice && (
          <p role="status" className="mt-2 text-sm text-amber-700">
            {notice}
          </p>
        )}
        {errors.trackingNumbers && (
          <p role="alert" className="mt-2 text-sm text-red-700">
            {errors.trackingNumbers}
          </p>
        )}

        {list.length > 0 && (
          <ul className="mt-4 grid gap-2 sm:grid-cols-2">
            {list.map((trackingNumber) => (
              <li key={trackingNumber} className="flex items-center justify-between rounded-lg bg-slate-50 px-3 py-2 font-mono text-sm">
                <span>{trackingNumber}</span>
                <button
                  type="button"
                  onClick={() => setList((current) => current.filter((t) => t !== trackingNumber))}
                  aria-label={`Remove ${trackingNumber}`}
                  className="text-slate-500 hover:text-red-700"
                >
                  ✕
                </button>
              </li>
            ))}
          </ul>
        )}

        <div className="mt-5 flex flex-wrap items-center gap-3">
          <PrimaryButton type="button" disabled={busy} onClick={() => void handleSubmit()}>
            {busy ? 'Submitting…' : `Submit scans (${list.length})`}
          </PrimaryButton>
          {list.length > 0 && (
            <SecondaryButton type="button" disabled={busy} onClick={() => setList([])}>
              Clear list
            </SecondaryButton>
          )}
        </div>
      </section>

      {outcome && (
        <section aria-labelledby="results-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <h2 id="results-heading" className="text-base font-semibold text-slate-900">
            Results
          </h2>
          <p role="status" className="mt-1 text-sm text-slate-600">
            {outcome.succeeded} succeeded, {outcome.failed} failed.
            {outcome.failed > 0 && ' Failed parcels were changed nowhere and stay in the list above.'}
          </p>
          <div className="mt-3 overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-slate-200 text-xs uppercase text-slate-500">
                <tr>
                  <th scope="col" className="py-2 pr-4">
                    Tracking number
                  </th>
                  <th scope="col" className="py-2 pr-4">
                    Result
                  </th>
                  <th scope="col" className="py-2">
                    New status / reason
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {outcome.results.map((r) => (
                  <tr key={r.trackingNumber}>
                    <td className="py-2 pr-4 font-mono">{r.trackingNumber}</td>
                    <td className="py-2 pr-4">
                      <span
                        className={`rounded-full px-2 py-0.5 text-xs font-semibold ${r.succeeded ? 'bg-emerald-50 text-emerald-700' : 'bg-red-50 text-red-700'}`}
                      >
                        {r.succeeded ? 'Scanned' : 'Failed'}
                      </span>
                    </td>
                    <td className="py-2 text-slate-700">
                      {r.succeeded ? (r.statusName ?? (r.status ? statusLabel(r.status) : '—')) : (r.error ?? 'Could not be scanned.')}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  );
}
