import { useState, type FormEvent } from 'react';
import { ApiError, api } from '../lib/apiClient';
import {
  addressToForm,
  buildCreateWarehouseRequest,
  buildUpdateWarehouseRequest,
  emptyWarehouseForm,
  validateWarehouseForm,
  type WarehouseForm,
} from '../lib/network';
import { problemSummary, problemToFieldErrors } from '../lib/problems';
import { useApi } from '../lib/useApi';
import type { BranchResponse, PagedResponse, ServiceAreaResponse, WarehouseResponse } from '../types/api';
import AddressFields from './AddressFields';
import Dialog from './Dialog';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from './form';

interface WarehouseFormDialogProps {
  open: boolean;
  /** The warehouse being edited, or null to create a new one. */
  warehouse: WarehouseResponse | null;
  onClose: () => void;
  onSaved: () => void;
}

function initialForm(warehouse: WarehouseResponse | null): WarehouseForm {
  if (!warehouse) return emptyWarehouseForm();
  return {
    ...addressToForm(warehouse.address),
    branchId: warehouse.branchId,
    code: warehouse.code,
    name: warehouse.name,
    phoneNumber: warehouse.phoneNumber,
    capacity: warehouse.capacityParcels ? String(warehouse.capacityParcels) : '',
  };
}

export default function WarehouseFormDialog({ open, warehouse, onClose, onSaved }: WarehouseFormDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title={warehouse ? `Edit ${warehouse.name}` : 'New warehouse'} wide>
      <WarehouseFormBody warehouse={warehouse} onClose={onClose} onSaved={onSaved} />
    </Dialog>
  );
}

function WarehouseFormBody({ warehouse, onClose, onSaved }: Omit<WarehouseFormDialogProps, 'open'>) {
  const areas = useApi<ServiceAreaResponse[]>('/api/v1/service-areas');
  const branches = useApi<PagedResponse<BranchResponse>>('/api/v1/branches?page=1&pageSize=100');
  const isNew = warehouse === null;
  const [form, setForm] = useState<WarehouseForm>(() => initialForm(warehouse));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<WarehouseForm>) => setForm((f) => ({ ...f, ...patch }));

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const found = validateWarehouseForm(form, isNew);
    setErrors(found);
    setSummary(null);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      if (isNew) {
        await api.post<WarehouseResponse>('/api/v1/warehouses', buildCreateWarehouseRequest(form));
      } else {
        await api.put<WarehouseResponse>(`/api/v1/warehouses/${warehouse.id}`, buildUpdateWarehouseRequest(form));
      }
      onSaved();
      onClose();
    } catch (err) {
      if (err instanceof ApiError) {
        const fields = problemToFieldErrors(err.problem);
        if (fields.capacityParcels) fields.capacity = fields.capacityParcels;
        setErrors(fields);
        setSummary(err.status === 403 ? 'You are not allowed to change this warehouse.' : problemSummary(err.problem));
      } else {
        setSummary('Could not reach the server. Please try again shortly.');
      }
    } finally {
      setBusy(false);
    }
  }

  const branchOptions =
    branches.status === 'loaded' ? branches.data.items.filter((b) => b.isActive).map((b) => ({ value: b.id, label: `${b.name} (${b.code})` })) : [];

  return (
    <form onSubmit={(e) => void handleSubmit(e)} noValidate className="space-y-4">
      {summary && <Alert>{summary}</Alert>}
      {areas.status === 'error' && <Alert>Could not load the list of districts. {areas.message}</Alert>}

      <div className="grid gap-4 sm:grid-cols-2">
        {isNew ? (
          <SelectField
            id="warehouse-branch"
            label="Operating branch"
            required
            placeholder="Choose a branch"
            value={form.branchId}
            onChange={(branchId) => set({ branchId })}
            options={branchOptions}
            error={errors.branchId}
          />
        ) : (
          <TextField id="warehouse-branch" label="Operating branch" disabled value={warehouse.branchName} onChange={() => undefined} hint="The branch cannot be changed." />
        )}
        <TextField
          id="warehouse-code"
          label="Code"
          required
          maxLength={10}
          disabled={!isNew}
          hint={isNew ? 'Letters and digits, e.g. KTMW1. Cannot be changed later.' : 'The code cannot be changed.'}
          value={form.code}
          onChange={(code) => set({ code })}
          error={errors.code}
        />
        <TextField
          id="warehouse-name"
          label="Name"
          required
          maxLength={150}
          className="sm:col-span-2"
          value={form.name}
          onChange={(name) => set({ name })}
          error={errors.name}
        />
        <TextField
          id="warehouse-phone"
          label="Phone number"
          type="tel"
          inputMode="tel"
          required
          placeholder="01-4123456"
          value={form.phoneNumber}
          onChange={(phoneNumber) => set({ phoneNumber })}
          error={errors.phoneNumber}
        />
        <TextField
          id="warehouse-capacity"
          label="Capacity in parcels (optional)"
          inputMode="numeric"
          value={form.capacity}
          onChange={(capacity) => set({ capacity })}
          error={errors.capacity}
        />
      </div>

      <fieldset>
        <legend className="mb-2 text-sm font-semibold text-slate-800">Address</legend>
        <AddressFields
          idPrefix="warehouse"
          value={form}
          onChange={(address) => set(address)}
          errors={errors}
          areas={areas.status === 'loaded' ? areas.data : []}
        />
      </fieldset>

      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="submit" disabled={busy}>
          {busy ? 'Saving…' : isNew ? 'Create warehouse' : 'Save changes'}
        </PrimaryButton>
      </div>
    </form>
  );
}
