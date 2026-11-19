import { useState, type FormEvent } from 'react';
import { ApiError, api } from '../lib/apiClient';
import { statusLabel } from '../lib/labels';
import { problemSummary, problemToFieldErrors } from '../lib/problems';
import { useApi } from '../lib/useApi';
import {
  REMARKS_MAX_LENGTH,
  buildStatusRequest,
  emptyStatusForm,
  locationRequirement,
  remarksRequired,
  resolveLocationKind,
  usesDestinationWarehouse,
  validateStatusForm,
  type LocationKind,
  type StatusForm,
} from '../lib/statusUpdate';
import type { BranchResponse, PagedResponse, ParcelStatusUpdateResponse, WarehouseResponse } from '../types/api';
import { Alert, PrimaryButton, SelectField, TextAreaField } from './form';

interface StatusUpdatePanelProps {
  parcelId: string;
  /** The statuses this user may set right now (see statusesOfferedTo): empty means the panel is not shown. */
  allowedStatuses: readonly string[];
  /** Called after a successful update so the page can reload the parcel (and its history). */
  onChanged: () => Promise<void>;
}

export default function StatusUpdatePanel({ parcelId, allowedStatuses, onChanged }: StatusUpdatePanelProps) {
  const branches = useApi<PagedResponse<BranchResponse>>('/api/v1/branches?page=1&pageSize=50');
  const warehouses = useApi<PagedResponse<WarehouseResponse>>('/api/v1/warehouses?page=1&pageSize=50');

  const [form, setForm] = useState<StatusForm>(() => emptyStatusForm(allowedStatuses[0] ?? ''));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // If the parcel moved on (so the chosen status is no longer allowed), fall back to the first allowed one.
  const status = allowedStatuses.includes(form.status) ? form.status : (allowedStatuses[0] ?? '');
  const requirement = locationRequirement(status);
  const kind = resolveLocationKind(status, form.locationKind);
  const set = (patch: Partial<StatusForm>) => setForm((f) => ({ ...f, ...patch }));

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const current: StatusForm = { ...form, status };
    const found = validateStatusForm(current);
    setErrors(found);
    setSummary(null);
    setSuccess(null);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      const result = await api.post<ParcelStatusUpdateResponse>(`/api/v1/parcels/${parcelId}/status`, buildStatusRequest(current));
      setSuccess(`Status updated: ${result.previousStatusName} → ${result.statusName}.`);
      setForm(emptyStatusForm());
      await onChanged();
    } catch (err) {
      if (err instanceof ApiError) {
        setErrors(problemToFieldErrors(err.problem));
        setSummary(err.status === 403 ? 'Your role is not allowed to set this status.' : problemSummary(err.problem));
      } else {
        setSummary('Could not reach the server. Please try again shortly.');
      }
    } finally {
      setBusy(false);
    }
  }

  const branchOptions = branches.status === 'loaded' ? branches.data.items.filter((b) => b.isActive).map((b) => ({ value: b.id, label: b.name })) : [];
  const warehouseOptions =
    warehouses.status === 'loaded' ? warehouses.data.items.filter((w) => w.isActive).map((w) => ({ value: w.id, label: `${w.name} (${w.code})` })) : [];

  const showKindChoice = requirement === 'either' || requirement === 'optional';

  // Nothing to offer (for example a delivery agent, who delivers through the agent app): show no panel at all.
  if (allowedStatuses.length === 0) return null;

  return (
    <section aria-labelledby="status-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
      <h2 id="status-heading" className="text-base font-semibold text-slate-900">
        Update status
      </h2>
      <p className="mt-1 text-sm text-slate-600">
        Every change is added to the parcel&apos;s history with your name and the time. History can never be edited.
      </p>

      <form onSubmit={(e) => void handleSubmit(e)} noValidate className="mt-4 grid gap-4 sm:grid-cols-2">
        {summary && (
          <div className="sm:col-span-2">
            <Alert>{summary}</Alert>
          </div>
        )}
        {success && (
          <div className="sm:col-span-2">
            <Alert tone="success">{success}</Alert>
          </div>
        )}

        <SelectField
          id="newStatus"
          label="New status"
          required
          value={status}
          onChange={(value) => set({ status: value })}
          options={allowedStatuses.map((s) => ({ value: s, label: statusLabel(s) }))}
          error={errors.status}
        />

        {showKindChoice && (
          <SelectField
            id="locationKind"
            label={requirement === 'either' ? 'Recorded at' : 'Recorded at (optional)'}
            required={requirement === 'either'}
            value={kind}
            onChange={(value) => set({ locationKind: value as LocationKind })}
            options={[
              ...(requirement === 'optional' ? [{ value: '', label: 'Not specified' }] : []),
              { value: 'branch', label: 'A branch' },
              { value: 'warehouse', label: 'A warehouse' },
            ]}
          />
        )}

        {kind === 'branch' && (
          <SelectField
            id="branchId"
            label="Branch"
            required={requirement !== 'optional'}
            placeholder="Choose a branch"
            value={form.branchId}
            onChange={(branchId) => set({ branchId })}
            options={branchOptions}
            error={errors.branchId}
            hint={branches.status === 'error' ? 'Could not load branches.' : undefined}
          />
        )}

        {kind === 'warehouse' && (
          <SelectField
            id="warehouseId"
            label="Warehouse"
            required={requirement !== 'optional'}
            placeholder="Choose a warehouse"
            value={form.warehouseId}
            onChange={(warehouseId) => set({ warehouseId })}
            options={warehouseOptions}
            error={errors.warehouseId}
            hint={warehouses.status === 'error' ? 'Could not load warehouses.' : undefined}
          />
        )}

        {usesDestinationWarehouse(status) && (
          <SelectField
            id="toWarehouseId"
            label="Heading to warehouse (optional)"
            placeholder="Not specified"
            value={form.toWarehouseId}
            onChange={(toWarehouseId) => set({ toWarehouseId })}
            options={warehouseOptions.filter((o) => !(kind === 'warehouse' && o.value === form.warehouseId))}
            error={errors.toWarehouseId}
            hint="The warehouse this parcel is travelling to. It will show as inbound there."
          />
        )}

        <TextAreaField
          id="statusRemarks"
          label="Remarks"
          required={remarksRequired(status)}
          maxLength={REMARKS_MAX_LENGTH}
          className="sm:col-span-2"
          value={form.remarks}
          onChange={(remarks) => set({ remarks })}
          error={errors.remarks}
          hint={remarksRequired(status) ? 'Say why. This is saved in the history.' : 'Optional note for the history.'}
        />

        <div className="sm:col-span-2">
          <PrimaryButton type="submit" disabled={busy}>
            {busy ? 'Saving…' : 'Update status'}
          </PrimaryButton>
        </div>
      </form>
    </section>
  );
}
