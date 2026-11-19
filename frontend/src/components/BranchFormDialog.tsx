import { useState, type FormEvent } from 'react';
import { ApiError, api } from '../lib/apiClient';
import {
  addressToForm,
  buildCreateBranchRequest,
  buildUpdateBranchRequest,
  emptyBranchForm,
  validateBranchForm,
  type BranchForm,
} from '../lib/network';
import { problemSummary, problemToFieldErrors } from '../lib/problems';
import { useApi } from '../lib/useApi';
import type { BranchResponse, ServiceAreaResponse } from '../types/api';
import AddressFields from './AddressFields';
import Dialog from './Dialog';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from './form';

interface BranchFormDialogProps {
  open: boolean;
  /** The branch being edited, or null to create a new one. */
  branch: BranchResponse | null;
  onClose: () => void;
  onSaved: () => void;
}

function initialForm(branch: BranchResponse | null): BranchForm {
  if (!branch) return emptyBranchForm();
  return {
    ...addressToForm(branch.address),
    code: branch.code,
    name: branch.name,
    type: branch.type === 'HeadOffice' ? 'HeadOffice' : 'Branch',
    phoneNumber: branch.phoneNumber,
    email: branch.email ?? '',
  };
}

export default function BranchFormDialog({ open, branch, onClose, onSaved }: BranchFormDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title={branch ? `Edit ${branch.name}` : 'New branch'} wide>
      <BranchForm branch={branch} onClose={onClose} onSaved={onSaved} />
    </Dialog>
  );
}

function BranchForm({ branch, onClose, onSaved }: Omit<BranchFormDialogProps, 'open'>) {
  const areas = useApi<ServiceAreaResponse[]>('/api/v1/service-areas');
  const isNew = branch === null;
  const [form, setForm] = useState<BranchForm>(() => initialForm(branch));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<BranchForm>) => setForm((f) => ({ ...f, ...patch }));

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const found = validateBranchForm(form, isNew);
    setErrors(found);
    setSummary(null);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      if (isNew) {
        await api.post<BranchResponse>('/api/v1/branches', buildCreateBranchRequest(form));
      } else {
        await api.put<BranchResponse>(`/api/v1/branches/${branch.id}`, buildUpdateBranchRequest(form));
      }
      onSaved();
      onClose();
    } catch (err) {
      if (err instanceof ApiError) {
        setErrors(problemToFieldErrors(err.problem));
        setSummary(err.status === 403 ? 'You are not allowed to change this branch.' : problemSummary(err.problem));
      } else {
        setSummary('Could not reach the server. Please try again shortly.');
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={(e) => void handleSubmit(e)} noValidate className="space-y-4">
      {summary && <Alert>{summary}</Alert>}
      {areas.status === 'error' && <Alert>Could not load the list of districts. {areas.message}</Alert>}

      <div className="grid gap-4 sm:grid-cols-2">
        <TextField
          id="branch-code"
          label="Code"
          required
          maxLength={10}
          disabled={!isNew}
          hint={isNew ? 'Letters and digits, e.g. KTM01. Cannot be changed later.' : 'The code cannot be changed.'}
          value={form.code}
          onChange={(code) => set({ code })}
          error={errors.code}
        />
        <SelectField
          id="branch-type"
          label="Type"
          required
          disabled={!isNew}
          value={form.type}
          onChange={(type) => set({ type: type === 'HeadOffice' ? 'HeadOffice' : 'Branch' })}
          options={[
            { value: 'Branch', label: 'Branch' },
            { value: 'HeadOffice', label: 'Head office' },
          ]}
          error={errors.type}
        />
        <TextField
          id="branch-name"
          label="Name"
          required
          maxLength={150}
          className="sm:col-span-2"
          value={form.name}
          onChange={(name) => set({ name })}
          error={errors.name}
        />
        <TextField
          id="branch-phone"
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
          id="branch-email"
          label="Email (optional)"
          type="email"
          value={form.email}
          onChange={(email) => set({ email })}
          error={errors.email}
        />
      </div>

      <fieldset>
        <legend className="mb-2 text-sm font-semibold text-slate-800">Address</legend>
        <AddressFields
          idPrefix="branch"
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
          {busy ? 'Saving…' : isNew ? 'Create branch' : 'Save changes'}
        </PrimaryButton>
      </div>
    </form>
  );
}
