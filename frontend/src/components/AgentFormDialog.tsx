import { useState, type FormEvent } from 'react';
import { ApiError, api } from '../lib/apiClient';
import { agentToForm, buildCreateAgentRequest, buildUpdateAgentRequest, emptyAgentForm, validateAgentForm, type AgentForm } from '../lib/delivery';
import { deliveryApi } from '../lib/deliveryApi';
import { problemSummary, problemToFieldErrors } from '../lib/problems';
import type { DeliveryAgentResponse } from '../types/api';
import Dialog from './Dialog';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField, type SelectOption } from './form';

interface AgentFormDialogProps {
  open: boolean;
  /** The agent being edited, or null to create a new one. */
  agent: DeliveryAgentResponse | null;
  /** The branches the person using this form may put an agent in (a branch manager sees only their own). */
  branchOptions: SelectOption[];
  onClose: () => void;
  onSaved: (message: string) => void;
}

export default function AgentFormDialog({ open, agent, branchOptions, onClose, onSaved }: AgentFormDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title={agent ? `Edit ${agent.fullName}` : 'New delivery agent'} wide>
      <AgentFormBody agent={agent} branchOptions={branchOptions} onClose={onClose} onSaved={onSaved} />
    </Dialog>
  );
}

function AgentFormBody({ agent, branchOptions, onClose, onSaved }: Omit<AgentFormDialogProps, 'open'>) {
  const isNew = agent === null;
  const [form, setForm] = useState<AgentForm>(() => (agent ? agentToForm(agent) : { ...emptyAgentForm(), branchId: branchOptions.length === 1 ? (branchOptions[0]?.value ?? '') : '' }));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<AgentForm>) => setForm((f) => ({ ...f, ...patch }));

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const found = validateAgentForm(form, isNew);
    setErrors(found);
    setSummary(null);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      if (isNew) {
        await api.post<DeliveryAgentResponse>(deliveryApi.agents, buildCreateAgentRequest(form));
        onSaved(`${form.fullName.trim()} can now sign in as a delivery agent. Share the password with them securely.`);
      } else {
        await api.put<DeliveryAgentResponse>(deliveryApi.agent(agent.id), buildUpdateAgentRequest(form));
        onSaved(`${agent.fullName}'s details were saved.`);
      }
      onClose();
    } catch (err) {
      if (err instanceof ApiError) {
        setErrors(problemToFieldErrors(err.problem));
        setSummary(err.status === 403 ? 'You are not allowed to manage agents of this branch.' : problemSummary(err.problem));
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

      <div className="grid gap-4 sm:grid-cols-2">
        <TextField id="agent-name" label="Full name" required disabled={!isNew} maxLength={150} autoComplete="off" value={form.fullName} onChange={(fullName) => set({ fullName })} error={errors.fullName} hint={isNew ? undefined : 'The name cannot be changed here.'} />
        <TextField id="agent-email" label="Email (the agent signs in with it)" type="email" required disabled={!isNew} autoComplete="off" value={form.email} onChange={(email) => set({ email })} error={errors.email} />
        <TextField id="agent-phone" label="Phone number" type="tel" inputMode="tel" required value={form.phoneNumber} onChange={(phoneNumber) => set({ phoneNumber })} error={errors.phoneNumber} />
        <SelectField
          id="agent-branch"
          label="Branch"
          required
          disabled={!isNew}
          placeholder="Choose a branch"
          value={form.branchId}
          onChange={(branchId) => set({ branchId })}
          options={branchOptions}
          error={errors.branchId}
          hint={isNew ? 'The branch this agent rides from.' : 'The branch cannot be changed here.'}
        />
        {isNew && (
          <TextField
            id="agent-password"
            label="Temporary password"
            type="password"
            required
            autoComplete="new-password"
            className="sm:col-span-2"
            value={form.password}
            onChange={(password) => set({ password })}
            hint="At least 10 characters with an upper-case letter, a lower-case letter and a digit."
            error={errors.password}
          />
        )}
        <TextField id="agent-vehicle-type" label="Vehicle type (optional)" maxLength={40} placeholder="Motorbike" value={form.vehicleType} onChange={(vehicleType) => set({ vehicleType })} error={errors.vehicleType} />
        <TextField id="agent-vehicle-number" label="Vehicle number (optional)" maxLength={30} value={form.vehicleNumber} onChange={(vehicleNumber) => set({ vehicleNumber })} error={errors.vehicleNumber} />
        <TextField id="agent-license" label="Licence number (optional)" maxLength={40} className="sm:col-span-2" value={form.licenseNumber} onChange={(licenseNumber) => set({ licenseNumber })} error={errors.licenseNumber} />
      </div>

      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="submit" disabled={busy}>
          {busy ? 'Saving…' : isNew ? 'Create agent' : 'Save changes'}
        </PrimaryButton>
      </div>
    </form>
  );
}
