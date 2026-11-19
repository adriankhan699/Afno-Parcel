import { useState } from 'react';
import { api } from '../lib/apiClient';
import { formatNepalDateTime, formatNpr } from '../lib/format';
import {
  REMARKS_MAX_LENGTH,
  buildResolveRequest,
  buildVerifyRequest,
  emptyVerifyForm,
  settlementStatusLabel,
  settlementStatusTone,
  validateCount,
  validateResolveRemarks,
  validateVerifyForm,
  varianceText,
  varianceTone,
  type VerifyForm,
} from '../lib/payment';
import { paymentApi } from '../lib/paymentApi';
import { useApi } from '../lib/useApi';
import { usePaymentAction } from '../lib/usePaymentAction';
import type { SettlementResponse } from '../types/api';
import Dialog from './Dialog';
import { Alert, PrimaryButton, SecondaryButton, TextAreaField, TextField } from './form';

export function SettlementStatusChip({ status }: { status: string }) {
  return <span className={`whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-semibold ${settlementStatusTone(status)}`}>{settlementStatusLabel(status)}</span>;
}

// ---- details ---------------------------------------------------------------------------------------------------------

interface DetailProps {
  settlementId: string | null;
  onClose: () => void;
}

/** One hand-in with the parcels whose cash it contains. */
export function SettlementDetailDialog({ settlementId, onClose }: DetailProps) {
  return (
    <Dialog open={settlementId !== null} onClose={onClose} title="Cash hand-in" wide>
      {settlementId && <DetailBody settlementId={settlementId} onClose={onClose} />}
    </Dialog>
  );
}

function DetailBody({ settlementId, onClose }: { settlementId: string; onClose: () => void }) {
  const result = useApi<SettlementResponse>(paymentApi.settlement(settlementId));

  if (result.status === 'loading' || result.status === 'idle') return <p className="text-sm text-slate-500">Loading…</p>;
  if (result.status === 'error') return <Alert>{result.message}</Alert>;
  const s = result.data;

  return (
    <div className="space-y-4 text-sm">
      <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-2">
        <div>
          <dt className="text-slate-500">Reference</dt>
          <dd className="font-mono">{s.referenceNumber}</dd>
        </div>
        <div>
          <dt className="text-slate-500">Status</dt>
          <dd>
            <SettlementStatusChip status={s.status} />
          </dd>
        </div>
        <div>
          <dt className="text-slate-500">Agent</dt>
          <dd>{s.agentName ?? '—'}</dd>
        </div>
        <div>
          <dt className="text-slate-500">Branch</dt>
          <dd>{s.branchName}</dd>
        </div>
        <div>
          <dt className="text-slate-500">Cash handed in</dt>
          <dd className="font-semibold tabular-nums">{formatNpr(s.totalAmount)}</dd>
        </div>
        <div>
          <dt className="text-slate-500">Cash counted</dt>
          <dd className="tabular-nums">
            {s.countedAmount === null || s.countedAmount === undefined ? 'Not counted yet' : formatNpr(s.countedAmount)}
            {s.variance !== null && s.variance !== undefined && <span className={`ml-2 font-medium ${varianceTone(s.variance)}`}>({varianceText(s.variance)})</span>}
          </dd>
        </div>
        {s.submittedAtUtc && (
          <div>
            <dt className="text-slate-500">Handed in</dt>
            <dd>{formatNepalDateTime(s.submittedAtUtc)}</dd>
          </div>
        )}
        {s.verifiedAtUtc && (
          <div>
            <dt className="text-slate-500">Counted</dt>
            <dd>{formatNepalDateTime(s.verifiedAtUtc)}</dd>
          </div>
        )}
        {s.remarks && (
          <div className="sm:col-span-2">
            <dt className="text-slate-500">Remarks</dt>
            <dd className="whitespace-pre-wrap">{s.remarks}</dd>
          </div>
        )}
      </dl>

      <div className="overflow-x-auto rounded-lg border border-slate-200">
        <table className="min-w-full text-left text-sm">
          <caption className="sr-only">Parcels in this hand-in</caption>
          <thead className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
            <tr>
              <th scope="col" className="px-3 py-2">
                Parcel
              </th>
              <th scope="col" className="px-3 py-2">
                Collected
              </th>
              <th scope="col" className="px-3 py-2 text-right">
                Due
              </th>
              <th scope="col" className="px-3 py-2 text-right">
                Collected
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {s.collections.map((c) => (
              <tr key={c.parcelId}>
                <td className="px-3 py-2 font-mono text-xs">{c.trackingNumber}</td>
                <td className="whitespace-nowrap px-3 py-2 text-slate-700">{formatNepalDateTime(c.collectedAtUtc)}</td>
                <td className="px-3 py-2 text-right tabular-nums">{formatNpr(c.expectedAmount)}</td>
                <td className="px-3 py-2 text-right tabular-nums">{formatNpr(c.collectedAmount)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="flex justify-end">
        <SecondaryButton type="button" onClick={onClose}>
          Close
        </SecondaryButton>
      </div>
    </div>
  );
}

// ---- verifying the count ---------------------------------------------------------------------------------------------

interface ActionProps {
  settlement: SettlementResponse | null;
  onClose: () => void;
  /** Called with the settlement the server returned. */
  onDone: (updated: SettlementResponse) => void;
}

export function VerifySettlementDialog({ settlement, onClose, onDone }: ActionProps) {
  return (
    <Dialog open={settlement !== null} onClose={onClose} title="Verify the cash count">
      {settlement && <VerifyBody settlement={settlement} onClose={onClose} onDone={onDone} />}
    </Dialog>
  );
}

function VerifyBody({ settlement, onClose, onDone }: { settlement: SettlementResponse; onClose: () => void; onDone: (s: SettlementResponse) => void }) {
  const [form, setForm] = useState<VerifyForm>(emptyVerifyForm);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const action = usePaymentAction();
  const entry = validateCount(form.counted, settlement.totalAmount);

  async function submit() {
    const found = validateVerifyForm(form, settlement.totalAmount);
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    const done = await action.run(() => api.post<SettlementResponse>(paymentApi.settlementVerify(settlement.id), buildVerifyRequest(form)));
    if (done.ok) {
      onClose();
      onDone(done.value);
    }
  }

  return (
    <form
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        void submit();
      }}
      className="space-y-4"
    >
      <p className="text-sm text-slate-700">
        {settlement.agentName ?? 'The agent'} handed in <strong>{settlement.collectionCount}</strong> collection{settlement.collectionCount === 1 ? '' : 's'} ({settlement.referenceNumber}). Count the cash and enter what you actually received.
      </p>
      <p className="rounded-lg bg-slate-50 p-3 text-sm">
        Expected total: <strong className="text-base tabular-nums">{formatNpr(settlement.totalAmount)}</strong>
      </p>
      {action.error && <Alert>{action.error}</Alert>}
      <TextField
        id="count-amount"
        label="Cash counted (Rs.)"
        required
        inputMode="decimal"
        autoComplete="off"
        value={form.counted}
        onChange={(counted) => setForm((f) => ({ ...f, counted }))}
        error={errors.counted}
      />
      {entry.ok && !entry.matches && (
        <Alert tone="info">
          This is {varianceText(entry.variance)}. A count that differs from the expected total does not settle the cash: the hand-in becomes <strong>disputed</strong>, and you need to explain the difference below.
        </Alert>
      )}
      {entry.ok && entry.matches && <Alert tone="success">This matches the expected total. The hand-in will be accepted.</Alert>}
      <TextAreaField
        id="count-remarks"
        label={entry.ok && !entry.matches ? 'Explain the difference' : 'Remarks (optional)'}
        required={entry.ok && !entry.matches}
        rows={3}
        maxLength={REMARKS_MAX_LENGTH}
        value={form.remarks}
        onChange={(remarks) => setForm((f) => ({ ...f, remarks }))}
        error={errors.remarks}
      />
      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose} disabled={action.busy}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="submit" disabled={action.busy}>
          {action.busy ? 'Saving…' : entry.ok && !entry.matches ? 'Record the difference' : 'Accept the count'}
        </PrimaryButton>
      </div>
    </form>
  );
}

// ---- resolving a dispute ---------------------------------------------------------------------------------------------

export function ResolveSettlementDialog({ settlement, onClose, onDone }: ActionProps) {
  return (
    <Dialog open={settlement !== null} onClose={onClose} title="Resolve the disputed count">
      {settlement && <ResolveBody settlement={settlement} onClose={onClose} onDone={onDone} />}
    </Dialog>
  );
}

function ResolveBody({ settlement, onClose, onDone }: { settlement: SettlementResponse; onClose: () => void; onDone: (s: SettlementResponse) => void }) {
  const [remarks, setRemarks] = useState('');
  const [error, setError] = useState<string | undefined>();
  const action = usePaymentAction();

  async function submit() {
    const problem = validateResolveRemarks(remarks);
    setError(problem ?? undefined);
    if (problem) return;
    const done = await action.run(() => api.post<SettlementResponse>(paymentApi.settlementResolve(settlement.id), buildResolveRequest(remarks)));
    if (done.ok) {
      onClose();
      onDone(done.value);
    }
  }

  return (
    <form
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        void submit();
      }}
      className="space-y-4"
    >
      <p className="text-sm text-slate-700">
        {settlement.referenceNumber}: expected {formatNpr(settlement.totalAmount)}, counted{' '}
        {settlement.countedAmount === null || settlement.countedAmount === undefined ? 'nothing yet' : formatNpr(settlement.countedAmount)}{' '}
        <span className={`font-medium ${varianceTone(settlement.variance)}`}>({varianceText(settlement.variance)})</span>.
      </p>
      <p className="text-sm text-slate-700">
        Resolving accepts the count as it stands and closes the dispute. Write down how the difference was settled; this is kept on the record.
      </p>
      {action.error && <Alert>{action.error}</Alert>}
      <TextAreaField id="resolve-remarks" label="How was it settled?" required rows={3} maxLength={REMARKS_MAX_LENGTH} value={remarks} onChange={setRemarks} error={error} />
      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose} disabled={action.busy}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="submit" disabled={action.busy}>
          {action.busy ? 'Saving…' : 'Resolve'}
        </PrimaryButton>
      </div>
    </form>
  );
}
