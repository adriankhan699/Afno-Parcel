import { useRef, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/useAuth';
import CameraScanner from '../components/CameraScanner';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextAreaField } from '../components/form';
import { adminApi } from '../lib/adminApi';
import { ApiError, api } from '../lib/apiClient';
import { isSuperAdmin } from '../lib/auth';
import { cameraScanningSupported } from '../lib/camera';
import { formatNepalDateTime } from '../lib/format';
import { warehousesForBranch } from '../lib/network';
import { problemSummary } from '../lib/problems';
import {
  MAX_BATCH_SIZE,
  OUTCOME_LABELS,
  REMARKS_MAX,
  SCAN_TYPE_OPTIONS,
  addScans,
  buildBatchRequest,
  buildScanRequest,
  describeResult,
  describeScanError,
  extractTrackingNumber,
  outcomeOf,
  outcomeTone,
  remainingAfterScan,
  summarizeResults,
  validateScanForm,
  validateScanSettings,
  type OutcomeKind,
} from '../lib/scan';
import { useApi } from '../lib/useApi';
import type { BatchScanResponse, MyAssignmentResponse, PagedResponse, ScanResultDto, ScanType, WarehouseResponse } from '../types/api';

type Mode = 'live' | 'batch';

interface LiveEntry {
  id: number;
  code: string;
  state: 'pending' | 'done';
  outcome?: OutcomeKind;
  text?: string;
  trackingNumber?: string;
  at?: string;
}

const MAX_LIVE_ENTRIES = 50;

function Counts({ applied, duplicates, failed }: { applied: number; duplicates: number; failed: number }) {
  return (
    <p role="status" className="flex flex-wrap gap-2 text-sm">
      <span className={`rounded-full px-3 py-1 font-semibold ${outcomeTone('Applied')}`}>{applied} applied</span>
      <span className={`rounded-full px-3 py-1 font-semibold ${outcomeTone('Duplicate')}`}>{duplicates} duplicate</span>
      <span className={`rounded-full px-3 py-1 font-semibold ${outcomeTone('Failed')}`}>{failed} failed</span>
    </p>
  );
}

function OutcomePill({ outcome }: { outcome: OutcomeKind }) {
  return <span className={`whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-semibold ${outcomeTone(outcome)}`}>{OUTCOME_LABELS[outcome]}</span>;
}

export default function ScanPage() {
  const auth = useAuth();
  const superAdmin = isSuperAdmin(auth.user?.roles);
  const warehouses = useApi<PagedResponse<WarehouseResponse>>(`${adminApi.warehouses}?page=1&pageSize=100`);
  const mine = useApi<MyAssignmentResponse>(adminApi.myAssignment);
  const inputRef = useRef<HTMLInputElement>(null);
  const nextId = useRef(1);

  const [mode, setMode] = useState<Mode>('live');
  const [chosenWarehouseId, setWarehouseId] = useState<string | null>(null);
  const [scanType, setScanType] = useState<ScanType | ''>('');
  const [toWarehouseId, setToWarehouseId] = useState('');
  const [remarks, setRemarks] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [entry, setEntry] = useState('');
  const [camera, setCamera] = useState(false);

  // live mode
  const [live, setLive] = useState<LiveEntry[]>([]);

  // batch mode
  const [list, setList] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<BatchScanResponse | null>(null);

  const assignment = mine.status === 'loaded' ? (mine.data.assignment ?? null) : null;
  const assignmentKnown = mine.status === 'loaded' || mine.status === 'error';

  // Until they pick one, scanning starts on the warehouse the person is assigned to.
  const warehouseId = chosenWarehouseId ?? assignment?.warehouseId ?? '';

  const allActive = warehouses.status === 'loaded' ? warehouses.data.items.filter((w) => w.isActive) : [];
  // Staff can scan only within their branch; super admins can scan anywhere.
  const scanWarehouses = !superAdmin && assignment ? warehousesForBranch(allActive, assignment.branchId) : allActive;
  const warehouseOptions = scanWarehouses.map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }));
  const destinationOptions = allActive.filter((w) => w.id !== warehouseId).map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }));
  const chosenType = SCAN_TYPE_OPTIONS.find((o) => o.value === scanType);

  const settings = { scanType, warehouseId, toWarehouseId, remarks };

  function checkSettings(): ScanType | null {
    const found = validateScanSettings(settings);
    setErrors(found);
    if (Object.keys(found).length > 0 || scanType === '') return null;
    return scanType;
  }

  function changeMode(next: Mode) {
    setMode(next);
    setSummary(null);
    setNotice(null);
    setErrors({});
    window.setTimeout(() => inputRef.current?.focus(), 0);
  }

  // ------------------------------------------------------------------------------------ live mode

  function describeRefusal(err: unknown): string {
    if (err instanceof ApiError) return describeScanError(err.status, err.problem.detail ?? problemSummary(err.problem));
    return 'Could not reach the server. Please try again shortly.';
  }

  async function scanLive(raw: string) {
    const code = raw.trim();
    if (!code) return;
    const type = checkSettings();
    if (!type) return;
    setSummary(null);

    const id = nextId.current++;
    const trackingNumber = extractTrackingNumber(code) ?? undefined;
    const keep = (entries: LiveEntry[]) => entries.slice(0, MAX_LIVE_ENTRIES);

    if (!trackingNumber) {
      setLive((cur) => keep([{ id, code, state: 'done', outcome: 'Failed', text: 'This is not an Afno Parcel tracking number or label.' }, ...cur]));
      return;
    }

    setLive((cur) => keep([{ id, code, state: 'pending', trackingNumber }, ...cur]));
    try {
      const result = await api.post<ScanResultDto>(adminApi.scan, buildScanRequest(code, { ...settings, scanType: type }));
      setLive((cur) =>
        cur.map((e) =>
          e.id === id
            ? { ...e, state: 'done', outcome: outcomeOf(result), text: describeResult(result), trackingNumber: result.trackingNumber || trackingNumber, at: result.scannedAtUtc ?? undefined }
            : e,
        ),
      );
    } catch (err) {
      setLive((cur) => cur.map((e) => (e.id === id ? { ...e, state: 'done', outcome: 'Failed', text: describeRefusal(err) } : e)));
    }
  }

  function handleLiveSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const raw = entry;
    setEntry('');
    void scanLive(raw);
    inputRef.current?.focus();
  }

  const liveSummary = summarizeResults(
    live.filter((e) => e.state === 'done').map((e) => ({ outcome: e.outcome ?? 'Failed', succeeded: e.outcome !== 'Failed' })),
  );

  // ----------------------------------------------------------------------------------- batch mode

  function addToList(raw: string) {
    const result = addScans(list, raw);
    setList(result.list);
    setOutcome(null);

    const problems: string[] = [];
    if (result.invalid.length > 0) problems.push(`Not a tracking number: ${result.invalid.join(', ')}`);
    if (result.duplicates.length > 0) problems.push(`Already in the list: ${result.duplicates.join(', ')}`);
    if (result.rejectedFull.length > 0) problems.push(`The list is full (${MAX_BATCH_SIZE} parcels). Submit these first.`);
    setNotice(problems.length > 0 ? problems.join(' · ') : null);
  }

  // A scanner "types" the code and presses Enter, which submits this small form.
  function handleAdd(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!entry.trim()) return;
    addToList(entry);
    setEntry('');
    inputRef.current?.focus();
  }

  async function handleBatchSubmit() {
    const form = { scanType, warehouseId, toWarehouseId, remarks, trackingNumbers: list };
    const found = validateScanForm(form);
    setErrors(found);
    setSummary(null);
    if (Object.keys(found).length > 0 || scanType === '') return;

    setBusy(true);
    try {
      const response = await api.post<BatchScanResponse>(adminApi.scanBatch, buildBatchRequest({ ...form, scanType }));
      setOutcome(response);
      // Keep only the parcels that were not recorded, so they can be fixed and sent again.
      setList(remainingAfterScan(list, response.results));
    } catch (err) {
      setSummary(describeRefusal(err));
    } finally {
      setBusy(false);
    }
  }

  // ------------------------------------------------------------------------------------- camera

  function handleCameraCode(raw: string) {
    if (mode === 'live') void scanLive(raw);
    else addToList(raw);
  }

  const cameraAvailable = cameraScanningSupported();
  const batchSummary = outcome ? summarizeResults(outcome.results) : null;

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">Warehouse scan</h1>
        <p className="mt-1 text-sm text-slate-600">
          Choose the warehouse and what you are doing, then scan labels with a scanner, the camera or the keyboard. Each scan is saved in the parcel&apos;s history; scanning the same parcel twice never records it twice.
        </p>
      </div>

      {summary && <Alert>{summary}</Alert>}
      {warehouses.status === 'error' && <Alert>Could not load the warehouses. {warehouses.message}</Alert>}
      {warehouses.status === 'loaded' && warehouseOptions.length === 0 && (
        <Alert tone="info">
          {assignment || superAdmin ? 'There are no active warehouses to scan at.' : 'There are no warehouses to scan at yet. A branch manager can add one.'}
        </Alert>
      )}
      {assignmentKnown && !assignment && !superAdmin && (
        <Alert tone="info">You are not assigned to a branch yet, so scans will be refused. Ask a branch manager to assign you.</Alert>
      )}
      {assignment && (
        <p className="text-sm text-slate-600">
          Assigned to <strong>{assignment.branchName}</strong>
          {assignment.warehouseName ? <> · {assignment.warehouseName}</> : null}.
        </p>
      )}

      <section aria-label="Scan settings" className="grid gap-4 rounded-xl border border-slate-200 bg-white p-5 shadow-sm sm:grid-cols-2">
        <SelectField
          id="warehouse"
          label="Warehouse"
          required
          placeholder="Choose a warehouse"
          value={warehouseId}
          onChange={(value) => {
            setWarehouseId(value);
            if (value === toWarehouseId) setToWarehouseId('');
          }}
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
        {scanType === 'Dispatch' && (
          <SelectField
            id="toWarehouse"
            label="Destination warehouse (optional)"
            placeholder="Not specified"
            value={toWarehouseId}
            onChange={setToWarehouseId}
            options={destinationOptions}
            error={errors.toWarehouseId}
            hint="Where these parcels are going. The receiving warehouse will see them as inbound."
            className="sm:col-span-2"
          />
        )}
        <TextAreaField
          id="scanRemarks"
          label="Remarks (optional)"
          rows={2}
          maxLength={REMARKS_MAX}
          className="sm:col-span-2"
          value={remarks}
          onChange={setRemarks}
          error={errors.remarks}
        />
      </section>

      <div role="tablist" aria-label="Scan mode" className="flex gap-2">
        {(
          [
            ['live', 'Scan one by one'],
            ['batch', 'Batch (collect, then submit)'],
          ] as const
        ).map(([value, label]) => (
          <button
            key={value}
            type="button"
            role="tab"
            aria-selected={mode === value}
            onClick={() => changeMode(value)}
            className={`rounded-lg px-4 py-2 text-sm font-semibold ${mode === value ? 'bg-brand-600 text-white' : 'border border-slate-300 bg-white text-slate-700 hover:bg-slate-50'}`}
          >
            {label}
          </button>
        ))}
      </div>

      {camera && cameraAvailable && <CameraScanner onCode={handleCameraCode} onClose={() => setCamera(false)} />}

      {mode === 'live' ? (
        <section aria-labelledby="live-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <h2 id="live-heading" className="text-base font-semibold text-slate-900">
              Scan now
            </h2>
            {live.length > 0 && <Counts applied={liveSummary.applied} duplicates={liveSummary.duplicates} failed={liveSummary.failed} />}
          </div>

          <form onSubmit={handleLiveSubmit} className="mt-3 flex flex-col gap-2 sm:flex-row">
            <label htmlFor="scanEntry" className="sr-only">
              Tracking number or label code
            </label>
            <input
              id="scanEntry"
              ref={inputRef}
              value={entry}
              onChange={(e) => setEntry(e.target.value)}
              autoFocus
              autoComplete="off"
              autoCapitalize="characters"
              spellCheck={false}
              placeholder="Scan a label, or type a tracking number and press Enter"
              className="flex-1 rounded-lg border border-slate-300 bg-white px-4 py-3 font-mono text-lg tracking-wide outline-none focus:border-brand-600 focus:ring-2 focus:ring-brand-100"
            />
            <SecondaryButton type="submit">Scan</SecondaryButton>
            {cameraAvailable && (
              <SecondaryButton type="button" onClick={() => setCamera((on) => !on)}>
                {camera ? 'Stop camera' : 'Use camera'}
              </SecondaryButton>
            )}
          </form>

          {live.length > 0 && (
            <>
              <ul className="mt-4 divide-y divide-slate-100" aria-live="polite">
                {live.map((e) => (
                  <li key={e.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2 text-sm">
                    {e.state === 'pending' ? (
                      <span className="whitespace-nowrap rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold text-slate-600">Sending…</span>
                    ) : (
                      <OutcomePill outcome={e.outcome ?? 'Failed'} />
                    )}
                    <span className="font-mono font-medium">{e.trackingNumber ?? e.code}</span>
                    <span className="text-slate-700">{e.text}</span>
                    {e.at && <span className="text-xs text-slate-500">{formatNepalDateTime(e.at)}</span>}
                  </li>
                ))}
              </ul>
              <div className="mt-3">
                <SecondaryButton type="button" onClick={() => setLive([])}>
                  Clear results
                </SecondaryButton>
              </div>
            </>
          )}
        </section>
      ) : (
        <>
          <section aria-labelledby="scan-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
            <h2 id="scan-heading" className="text-base font-semibold text-slate-900">
              Parcels to scan ({list.length}/{MAX_BATCH_SIZE})
            </h2>

            <form onSubmit={handleAdd} className="mt-3 flex flex-col gap-2 sm:flex-row">
              <label htmlFor="scanEntry" className="sr-only">
                Tracking number or label code
              </label>
              <input
                id="scanEntry"
                ref={inputRef}
                value={entry}
                onChange={(e) => setEntry(e.target.value)}
                autoFocus
                autoComplete="off"
                autoCapitalize="characters"
                spellCheck={false}
                placeholder="Scan or type a tracking number, then press Enter"
                className="flex-1 rounded-lg border border-slate-300 bg-white px-4 py-3 font-mono text-lg tracking-wide outline-none focus:border-brand-600 focus:ring-2 focus:ring-brand-100"
              />
              <SecondaryButton type="submit">Add</SecondaryButton>
              {cameraAvailable && (
                <SecondaryButton type="button" onClick={() => setCamera((on) => !on)}>
                  {camera ? 'Stop camera' : 'Use camera'}
                </SecondaryButton>
              )}
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
              <PrimaryButton type="button" disabled={busy} onClick={() => void handleBatchSubmit()}>
                {busy ? 'Submitting…' : `Submit scans (${list.length})`}
              </PrimaryButton>
              {list.length > 0 && (
                <SecondaryButton type="button" disabled={busy} onClick={() => setList([])}>
                  Clear list
                </SecondaryButton>
              )}
            </div>
          </section>

          {outcome && batchSummary && (
            <section aria-labelledby="results-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 id="results-heading" className="text-base font-semibold text-slate-900">
                  Results
                </h2>
                <Counts applied={batchSummary.applied} duplicates={batchSummary.duplicates} failed={batchSummary.failed} />
              </div>
              {batchSummary.failed > 0 && <p className="mt-2 text-sm text-slate-600">Parcels that failed were not changed and stay in the list above.</p>}
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
                        Details
                      </th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {outcome.results.map((r) => (
                      <tr key={r.trackingNumber}>
                        <td className="py-2 pr-4 font-mono">{r.trackingNumber}</td>
                        <td className="py-2 pr-4">
                          <OutcomePill outcome={outcomeOf(r)} />
                        </td>
                        <td className="py-2 text-slate-700">{describeResult(r)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}
        </>
      )}
    </div>
  );
}
