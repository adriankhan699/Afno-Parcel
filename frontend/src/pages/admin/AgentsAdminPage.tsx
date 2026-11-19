import { useState, type FormEvent } from 'react';
import AgentFormDialog from '../../components/AgentFormDialog';
import ConfirmDialog from '../../components/ConfirmDialog';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../components/form';
import { ActiveBadge, TableShell, Th, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import { api } from '../../lib/apiClient';
import { agentQuery, buildDeliveryQuery, emptyAgentFilters, type AgentFilters } from '../../lib/delivery';
import { deliveryApi } from '../../lib/deliveryApi';
import { useGuardedAction } from '../../lib/useGuardedAction';
import { useApi } from '../../lib/useApi';
import { useManagerScope } from '../../lib/useManagerScope';
import type { DeliveryAgentResponse, PagedResponse } from '../../types/api';

/** Delivery agents: who they are, which branch they ride from, whether they can take parcels, and how many they hold. */
export default function AgentsAdminPage() {
  const scope = useManagerScope();
  const [draft, setDraft] = useState<AgentFilters>(emptyAgentFilters);
  const [applied, setApplied] = useState<AgentFilters>(emptyAgentFilters);
  const [page, setPage] = useState(1);
  const [reloadKey, setReloadKey] = useState(0);
  const [editing, setEditing] = useState<DeliveryAgentResponse | null>(null);
  const [creating, setCreating] = useState(false);
  const [switching, setSwitching] = useState<{ agent: DeliveryAgentResponse; activate: boolean } | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const availability = useGuardedAction();

  const result = useApi<PagedResponse<DeliveryAgentResponse>>(`${deliveryApi.agents}${buildDeliveryQuery(agentQuery(applied, page))}`, reloadKey);
  const reload = () => setReloadKey((k) => k + 1);
  const set = (patch: Partial<AgentFilters>) => setDraft((d) => ({ ...d, ...patch }));

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyAgentFilters());
    setApplied(emptyAgentFilters());
    setPage(1);
  }

  async function toggleAvailability(agent: DeliveryAgentResponse) {
    setNotice(null);
    const done = await availability.run(() => api.put(deliveryApi.agentAvailability(agent.id), { isAvailable: !agent.isAvailable }));
    if (done.ok) setNotice(`${agent.fullName} is now ${agent.isAvailable ? 'unavailable' : 'available'}.`);
    reload();
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">Delivery agents</h2>
        <PrimaryButton type="button" onClick={() => setCreating(true)} disabled={scope.branchOptions.length === 0}>
          New delivery agent
        </PrimaryButton>
      </div>

      <p className="text-sm text-slate-600">
        Agents see only the parcels you give them, on their phone. Switch an agent off when they leave: they can no longer sign in to the agent app or take parcels.
        {!scope.superAdmin && ' You manage the agents of your own branch.'}
      </p>
      {scope.unassignedManager && <Alert tone="info">You are not assigned to a branch yet, so you cannot manage agents. Ask a super admin to assign you.</Alert>}
      {notice && <Alert tone="success">{notice}</Alert>}
      {availability.error && <Alert>{availability.error}</Alert>}

      <form onSubmit={handleSubmit} role="search" aria-label="Filter delivery agents" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <TextField id="agent-search" label="Search" type="search" placeholder="Name, phone or vehicle number" value={draft.search} onChange={(search) => set({ search })} />
          <SelectField id="agent-branch-filter" label="Branch" placeholder="Any branch" value={draft.branchId} onChange={(branchId) => set({ branchId })} options={scope.branchOptions} />
          <SelectField
            id="agent-status-filter"
            label="Account"
            placeholder="Active and inactive"
            value={draft.status}
            onChange={(status) => set({ status: status === 'active' || status === 'inactive' ? status : '' })}
            options={[
              { value: 'active', label: 'Active only' },
              { value: 'inactive', label: 'Inactive only' },
            ]}
          />
          <SelectField
            id="agent-availability-filter"
            label="Availability"
            placeholder="Any"
            value={draft.availability}
            onChange={(availabilityValue) => set({ availability: availabilityValue === 'available' || availabilityValue === 'unavailable' ? availabilityValue : '' })}
            options={[
              { value: 'available', label: 'Available' },
              { value: 'unavailable', label: 'Not available' },
            ]}
          />
        </div>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit">Apply filters</PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Clear
          </SecondaryButton>
        </div>
      </form>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading agents…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No delivery agents found.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <thead className={theadClass}>
                <tr>
                  <Th>Agent</Th>
                  <Th>Branch</Th>
                  <Th>Vehicle</Th>
                  <Th>Account</Th>
                  <Th>Availability</Th>
                  <Th right>Open</Th>
                  <Th right>Actions</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((a) => (
                  <tr key={a.id} className="hover:bg-slate-50">
                    <td className="px-4 py-3">
                      <p className="font-medium text-slate-900">{a.fullName}</p>
                      <p className="text-xs text-slate-500">{a.email ?? 'No email'}</p>
                      <p className="text-xs text-slate-500">{a.phoneNumber}</p>
                    </td>
                    <td className="px-4 py-3">{a.branchName}</td>
                    <td className="px-4 py-3">
                      {a.vehicleType || a.vehicleNumber ? (
                        <>
                          <p>{a.vehicleType ?? '—'}</p>
                          <p className="text-xs text-slate-500">{a.vehicleNumber ?? ''}</p>
                        </>
                      ) : (
                        <span className="text-slate-500">—</span>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      <ActiveBadge active={a.isActive} />
                    </td>
                    <td className="px-4 py-3">
                      <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${a.isAvailable ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-200 text-slate-600'}`}>
                        {a.isAvailable ? 'Available' : 'Not available'}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-right tabular-nums">{a.openAssignments}</td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <button type="button" className="mr-3 text-sm font-medium text-brand-700 underline" onClick={() => setEditing(a)}>
                        Edit
                      </button>
                      {a.isActive && (
                        <button type="button" disabled={availability.busy} className="mr-3 text-sm font-medium text-slate-700 underline disabled:opacity-60" onClick={() => void toggleAvailability(a)}>
                          {a.isAvailable ? 'Mark unavailable' : 'Mark available'}
                        </button>
                      )}
                      <button type="button" className="text-sm font-medium text-slate-700 underline" onClick={() => setSwitching({ agent: a, activate: !a.isActive })}>
                        {a.isActive ? 'Deactivate' : 'Activate'}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="agent" onPage={setPage} />
          </>
        )}
      </div>

      <AgentFormDialog
        open={creating || editing !== null}
        agent={editing}
        branchOptions={scope.branchOptions}
        onClose={() => {
          setCreating(false);
          setEditing(null);
        }}
        onSaved={(message) => {
          setNotice(message);
          reload();
        }}
      />

      <ConfirmDialog
        open={switching !== null}
        title={switching ? `${switching.activate ? 'Activate' : 'Deactivate'} ${switching.agent.fullName}?` : 'Change account'}
        message={
          switching?.activate
            ? 'The agent can sign in again and can be given parcels.'
            : 'The agent can no longer sign in to the agent app or be given parcels. This is refused while they still have open deliveries: take those parcels back first.'
        }
        confirmLabel={switching?.activate ? 'Activate agent' : 'Deactivate agent'}
        onConfirm={async () => {
          if (!switching) return;
          const { agent, activate } = switching;
          await api.postEmpty(activate ? deliveryApi.agentActivate(agent.id) : deliveryApi.agentDeactivate(agent.id));
          setNotice(`${agent.fullName} was ${activate ? 'activated' : 'deactivated'}.`);
          reload();
        }}
        onClose={() => setSwitching(null)}
      />
    </div>
  );
}
