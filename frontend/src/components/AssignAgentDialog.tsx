import { useState, type FormEvent } from 'react';
import { ApiError, api } from '../lib/apiClient';
import { assignableAgents, buildDeliveryQuery } from '../lib/delivery';
import { deliveryApi } from '../lib/deliveryApi';
import { formatNpr } from '../lib/format';
import { problemSummary } from '../lib/problems';
import { useApi } from '../lib/useApi';
import type { AssignDeliveryRequest, DeliveryAgentResponse, DeliveryAssignmentResponse, PagedResponse, ReadyParcelResponse } from '../types/api';
import Dialog from './Dialog';
import { Alert, PrimaryButton, SecondaryButton, SelectField, type SelectOption } from './form';

interface AssignAgentDialogProps {
  /** The parcel to give to an agent, or null when the dialog is closed. */
  parcel: ReadyParcelResponse | null;
  /**
   * Set for a branch manager: only that branch's agents are listed (a manager can only assign their own branch's agents).
   * Null for a super admin, who may pick an agent of any branch.
   */
  limitToBranchId: string | null;
  /** The branches a super admin can narrow the agent list to. */
  branchOptions: SelectOption[];
  onClose: () => void;
  onSaved: (message: string) => void;
}

export default function AssignAgentDialog({ parcel, limitToBranchId, branchOptions, onClose, onSaved }: AssignAgentDialogProps) {
  return (
    <Dialog open={parcel !== null} onClose={onClose} title={parcel ? `Give ${parcel.trackingNumber} to an agent` : 'Assign'}>
      {parcel && <AssignBody parcel={parcel} limitToBranchId={limitToBranchId} branchOptions={branchOptions} onClose={onClose} onSaved={onSaved} />}
    </Dialog>
  );
}

function AssignBody({ parcel, limitToBranchId, branchOptions, onClose, onSaved }: Omit<AssignAgentDialogProps, 'parcel'> & { parcel: ReadyParcelResponse }) {
  const [branchFilter, setBranchFilter] = useState('');
  const [agentId, setAgentId] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const branchId = limitToBranchId ?? (branchFilter || undefined);
  const agents = useApi<PagedResponse<DeliveryAgentResponse>>(
    `${deliveryApi.agents}${buildDeliveryQuery({ page: 1, pageSize: 100, isActive: true, isAvailable: true, branchId })}`,
  );
  const options: SelectOption[] =
    agents.status === 'loaded'
      ? assignableAgents(agents.data.items).map((a) => ({
          value: a.id,
          label: `${a.fullName} · ${a.branchName}${a.openAssignments > 0 ? ` · ${a.openAssignments} open` : ''}`,
        }))
      : [];

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!agentId) {
      setError('Choose an agent.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const request: AssignDeliveryRequest = { parcelId: parcel.parcelId, deliveryAgentId: agentId };
      const created = await api.post<DeliveryAssignmentResponse>(deliveryApi.assignments, request);
      onSaved(`${parcel.trackingNumber} was given to ${created.agentName}.`);
      onClose();
    } catch (err) {
      setError(err instanceof ApiError ? (err.status === 403 ? 'You are not allowed to assign this parcel or this agent.' : problemSummary(err.problem)) : 'Could not reach the server. Please try again shortly.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={(e) => void handleSubmit(e)} noValidate className="space-y-4">
      <dl className="rounded-lg bg-slate-50 p-3 text-sm">
        <div className="flex justify-between gap-3">
          <dt className="text-slate-500">Receiver</dt>
          <dd className="text-right font-medium">
            {parcel.receiverName}, {parcel.receiverMunicipality}, {parcel.receiverDistrict}
          </dd>
        </div>
        <div className="flex justify-between gap-3">
          <dt className="text-slate-500">Now at</dt>
          <dd className="font-medium">{parcel.location}</dd>
        </div>
        {parcel.codAmount > 0 && (
          <div className="flex justify-between gap-3">
            <dt className="text-slate-500">Cash to collect</dt>
            <dd className="font-medium">{formatNpr(parcel.codAmount)}</dd>
          </div>
        )}
        {parcel.failedAttempts > 0 && (
          <div className="flex justify-between gap-3">
            <dt className="text-slate-500">Failed attempts so far</dt>
            <dd className="font-medium">{parcel.failedAttempts}</dd>
          </div>
        )}
      </dl>

      {error && <Alert>{error}</Alert>}
      {agents.status === 'error' && <Alert>Could not load the agents. {agents.message}</Alert>}

      {limitToBranchId === null && (
        <SelectField id="assign-branch-filter" label="Agent's branch" placeholder="Any branch" value={branchFilter} onChange={(value) => { setBranchFilter(value); setAgentId(''); }} options={branchOptions} />
      )}
      <SelectField
        id="assign-agent"
        label="Delivery agent"
        required
        placeholder={agents.status === 'loading' ? 'Loading agents…' : options.length === 0 ? 'No available agents' : 'Choose an agent'}
        value={agentId}
        onChange={setAgentId}
        options={options}
        hint="Only active agents who are marked available are listed."
      />

      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="submit" disabled={busy || options.length === 0}>
          {busy ? 'Assigning…' : 'Assign parcel'}
        </PrimaryButton>
      </div>
    </form>
  );
}
