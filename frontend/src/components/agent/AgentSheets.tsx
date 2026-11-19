import { useState } from 'react';
import { api } from '../../lib/apiClient';
import {
  FAILURE_PRESETS,
  FAILURE_REMARKS_MAX_LENGTH,
  REASON_MAX_LENGTH,
  buildCollectRequest,
  buildFailRequest,
  emptyFailureForm,
  validateCodEntry,
  validateFailureForm,
  type FailureForm,
  type FailurePreset,
} from '../../lib/delivery';
import { deliveryApi } from '../../lib/deliveryApi';
import { formatNpr } from '../../lib/format';
import type { GuardedAction } from '../../lib/useGuardedAction';
import { useApi } from '../../lib/useApi';
import type { AgentAssignmentResponse, CodCollectionResponse, PagedResponse, WarehouseResponse } from '../../types/api';
import Dialog from '../Dialog';
import { Alert } from '../form';
import { AgentField, AgentTextArea, BigButton } from './AgentBits';

interface SheetProps {
  open: boolean;
  assignment: AgentAssignmentResponse;
  action: GuardedAction;
  onClose: () => void;
  /** Called after the server accepted the action. */
  onDone: () => void;
}

export function FailSheet({ open, assignment, action, onClose, onDone }: SheetProps) {
  return (
    <Dialog open={open} onClose={onClose} title="Delivery failed">
      <FailBody assignment={assignment} action={action} onClose={onClose} onDone={onDone} />
    </Dialog>
  );
}

function FailBody({ assignment, action, onClose, onDone }: Omit<SheetProps, 'open'>) {
  const [form, setForm] = useState<FailureForm>(emptyFailureForm);
  const [errors, setErrors] = useState<Record<string, string>>({});

  async function submit() {
    const found = validateFailureForm(form);
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    const done = await action.run(() => api.post(deliveryApi.fail(assignment.id), buildFailRequest(form)), 'Marked as failed.');
    if (done.ok) {
      onClose();
      onDone();
    }
  }

  return (
    <div className="space-y-4">
      <p className="text-base text-slate-700">Why could you not deliver {assignment.trackingNumber}? This is saved with the parcel.</p>
      {action.error && <Alert>{action.error}</Alert>}

      <fieldset>
        <legend className="mb-2 text-sm font-medium text-slate-700">Reason</legend>
        <div className="flex flex-wrap gap-2">
          {FAILURE_PRESETS.map((preset) => (
            <button
              key={preset}
              type="button"
              aria-pressed={form.preset === preset}
              onClick={() => setForm((f) => ({ ...f, preset: preset as FailurePreset }))}
              className={`min-h-11 rounded-full border px-4 py-2 text-base font-medium ${
                form.preset === preset ? 'border-brand-600 bg-brand-50 text-brand-800' : 'border-slate-300 bg-white text-slate-700'
              }`}
            >
              {preset}
            </button>
          ))}
        </div>
        {errors.preset && (
          <p role="alert" className="mt-1 text-sm text-red-700">
            {errors.preset}
          </p>
        )}
      </fieldset>

      <AgentTextArea
        id="fail-detail"
        label={form.preset === 'Other' ? 'What happened? (required)' : 'More detail (optional)'}
        value={form.detail}
        onChange={(detail) => setForm((f) => ({ ...f, detail }))}
        maxLength={REASON_MAX_LENGTH}
        rows={2}
        error={errors.detail}
      />
      <AgentTextArea
        id="fail-remarks"
        label="Remarks for the branch (optional)"
        value={form.remarks}
        onChange={(remarks) => setForm((f) => ({ ...f, remarks }))}
        maxLength={FAILURE_REMARKS_MAX_LENGTH}
        rows={2}
        error={errors.remarks}
      />

      <div className="grid grid-cols-2 gap-3">
        <BigButton variant="secondary" disabled={action.busy} onClick={onClose}>
          Cancel
        </BigButton>
        <BigButton variant="danger" disabled={action.busy} onClick={() => void submit()}>
          {action.busy ? 'Saving…' : 'Mark failed'}
        </BigButton>
      </div>
    </div>
  );
}

export function CollectCashSheet({ open, assignment, action, onClose, onDone }: SheetProps) {
  return (
    <Dialog open={open} onClose={onClose} title="Collect cash">
      <CollectBody assignment={assignment} action={action} onClose={onClose} onDone={onDone} />
    </Dialog>
  );
}

function CollectBody({ assignment, action, onClose, onDone }: Omit<SheetProps, 'open'>) {
  const [text, setText] = useState('');
  const entry = validateCodEntry(text, assignment.codAmount);
  const showProblem = text.trim() !== '' && !entry.ok;

  async function submit() {
    if (!entry.ok || entry.amount === null) return;
    const amount = entry.amount;
    const done = await action.run(() => api.post<CodCollectionResponse>(deliveryApi.collectCod(assignment.id), buildCollectRequest(amount)), 'Cash recorded.');
    if (done.ok) {
      onClose();
      onDone();
    }
  }

  return (
    <div className="space-y-4">
      <p className="rounded-xl bg-amber-50 p-4 text-center">
        <span className="block text-sm text-amber-900">Cash due from the receiver</span>
        <span className="block text-3xl font-bold text-amber-950">{formatNpr(assignment.codAmount)}</span>
      </p>
      {action.error && <Alert>{action.error}</Alert>}
      <AgentField
        id="cod-amount"
        label="Cash you received (NPR)"
        inputMode="decimal"
        autoComplete="off"
        placeholder={String(assignment.codAmount)}
        value={text}
        onChange={setText}
        error={showProblem ? entry.error : undefined}
        hint="Count the notes, then type the amount. It must be exactly what is due."
      />
      <div className="grid grid-cols-2 gap-3">
        <BigButton variant="secondary" disabled={action.busy} onClick={onClose}>
          Cancel
        </BigButton>
        <BigButton disabled={action.busy || !entry.ok} onClick={() => void submit()}>
          {action.busy ? 'Saving…' : 'I have the cash'}
        </BigButton>
      </div>
    </div>
  );
}

export function ReturnSheet({ open, assignment, action, onClose, onDone }: SheetProps) {
  return (
    <Dialog open={open} onClose={onClose} title="Return to warehouse">
      <ReturnBody assignment={assignment} action={action} onClose={onClose} onDone={onDone} />
    </Dialog>
  );
}

function ReturnBody({ assignment, action, onClose, onDone }: Omit<SheetProps, 'open'>) {
  const warehouses = useApi<PagedResponse<WarehouseResponse>>('/api/v1/warehouses?page=1&pageSize=100');
  const [warehouseId, setWarehouseId] = useState('');
  const [remarks, setRemarks] = useState('');
  const [error, setError] = useState<string | null>(null);

  const options = warehouses.status === 'loaded' ? warehouses.data.items.filter((w) => w.isActive) : [];

  async function submit() {
    if (!warehouseId) {
      setError('Choose the warehouse you are taking the parcel to.');
      return;
    }
    setError(null);
    const done = await action.run(
      () => api.post(deliveryApi.returnToWarehouse(assignment.id), { warehouseId, ...(remarks.trim() ? { remarks: remarks.trim() } : {}) }),
      'Parcel returned to the warehouse.',
    );
    if (done.ok) {
      onClose();
      onDone();
    }
  }

  return (
    <div className="space-y-4">
      <p className="text-base text-slate-700">Hand {assignment.trackingNumber} back at a warehouse. It goes back into the network for another attempt.</p>
      {action.error && <Alert>{action.error}</Alert>}
      {warehouses.status === 'error' && <Alert>Could not load the warehouses. {warehouses.message}</Alert>}
      <div>
        <label htmlFor="return-warehouse" className="mb-1 block text-sm font-medium text-slate-700">
          Warehouse
        </label>
        <select
          id="return-warehouse"
          value={warehouseId}
          onChange={(e) => setWarehouseId(e.target.value)}
          aria-invalid={error ? true : undefined}
          className="min-h-12 w-full rounded-xl border border-slate-300 bg-white px-4 py-3 text-base"
        >
          <option value="">{warehouses.status === 'loading' ? 'Loading…' : 'Choose a warehouse'}</option>
          {options.map((w) => (
            <option key={w.id} value={w.id}>
              {w.name} ({w.code})
            </option>
          ))}
        </select>
        {error && (
          <p role="alert" className="mt-1 text-sm text-red-700">
            {error}
          </p>
        )}
      </div>
      <AgentTextArea id="return-remarks" label="Remarks (optional)" value={remarks} onChange={setRemarks} maxLength={FAILURE_REMARKS_MAX_LENGTH} rows={2} />
      <div className="grid grid-cols-2 gap-3">
        <BigButton variant="secondary" disabled={action.busy} onClick={onClose}>
          Cancel
        </BigButton>
        <BigButton disabled={action.busy} onClick={() => void submit()}>
          {action.busy ? 'Saving…' : 'Return parcel'}
        </BigButton>
      </div>
    </div>
  );
}
