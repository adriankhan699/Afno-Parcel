import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import AssignAgentDialog from '../../components/AssignAgentDialog';
import Dialog from '../../components/Dialog';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextAreaField, TextField } from '../../components/form';
import { TableShell, Th, WaitBadge, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import ProofViewer, { ProofChips } from '../../components/ProofViewer';
import { ApiError, api } from '../../lib/apiClient';
import {
  CANCEL_REASON_MAX_LENGTH,
  assignmentLabel,
  assignmentQuery,
  assignmentTone,
  buildDeliveryQuery,
  emptyAssignmentFilters,
  emptyReadyFilters,
  readyQuery,
  validateCancelReason,
  type AssignmentFilters,
  type ReadyFilters,
} from '../../lib/delivery';
import { deliveryApi } from '../../lib/deliveryApi';
import { formatNepalDateTime, formatNpr } from '../../lib/format';
import { serviceLabel } from '../../lib/labels';
import { isRangeInvalid } from '../../lib/network';
import { problemSummary } from '../../lib/problems';
import { useApi } from '../../lib/useApi';
import { useManagerScope } from '../../lib/useManagerScope';
import type { DeliveryAgentResponse, DeliveryAssignmentResponse, DeliveryProofResponse, PagedResponse, ReadyParcelResponse } from '../../types/api';

type Tab = 'ready' | 'assignments';

const STATUS_FILTER_OPTIONS = [
  { value: 'Open', label: 'Open (not finished)' },
  { value: 'Assigned', label: 'New' },
  { value: 'Accepted', label: 'Accepted' },
  { value: 'InProgress', label: 'Out for delivery' },
  { value: 'Completed', label: 'Delivered' },
  { value: 'Failed', label: 'Failed' },
  { value: 'Cancelled', label: 'Cancelled' },
];

/** Giving parcels to delivery agents, and following what became of them. */
export default function DeliveryAdminPage() {
  const scope = useManagerScope();
  const [tab, setTab] = useState<Tab>('ready');
  const [notice, setNotice] = useState<string | null>(null);
  // Bumped after an assignment is made or cancelled, so both lists refresh.
  const [reloadKey, setReloadKey] = useState(0);
  const reload = () => setReloadKey((k) => k + 1);

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold">Delivery</h2>
        <p className="mt-1 text-sm text-slate-600">
          Give parcels that are ready for their final leg to a delivery agent. The agent sees them on their phone and can start only when you have given them the parcel.
        </p>
      </div>

      {scope.unassignedManager && <Alert tone="info">You are not assigned to a branch yet, so you cannot give parcels to agents. Ask a super admin to assign you.</Alert>}
      {notice && <Alert tone="success">{notice}</Alert>}

      <div role="tablist" aria-label="Delivery" className="flex gap-1 border-b border-slate-200">
        {(
          [
            ['ready', 'Ready to assign'],
            ['assignments', 'Assignments'],
          ] as const
        ).map(([key, label]) => (
          <button
            key={key}
            id={`delivery-tab-${key}`}
            role="tab"
            type="button"
            aria-selected={tab === key}
            aria-controls={`delivery-panel-${key}`}
            onClick={() => setTab(key)}
            className={`border-b-2 px-3 py-2 text-sm font-medium ${tab === key ? 'border-brand-600 text-brand-700' : 'border-transparent text-slate-600 hover:text-slate-900'}`}
          >
            {label}
          </button>
        ))}
      </div>

      <div role="tabpanel" id={`delivery-panel-${tab}`} aria-labelledby={`delivery-tab-${tab}`}>
        {tab === 'ready' ? (
          <ReadyTab
            reloadKey={reloadKey}
            limitToBranchId={scope.limitToBranchId}
            branchOptions={scope.branchOptions}
            superAdmin={scope.superAdmin}
            onAssigned={(message) => {
              setNotice(message);
              reload();
            }}
          />
        ) : (
          <AssignmentsTab
            reloadKey={reloadKey}
            onChanged={(message) => {
              setNotice(message);
              reload();
            }}
          />
        )}
      </div>
    </div>
  );
}

interface ReadyTabProps {
  reloadKey: number;
  limitToBranchId: string | null;
  branchOptions: { value: string; label: string }[];
  superAdmin: boolean;
  onAssigned: (message: string) => void;
}

function ReadyTab({ reloadKey, limitToBranchId, branchOptions, superAdmin, onAssigned }: ReadyTabProps) {
  const [draft, setDraft] = useState<ReadyFilters>(emptyReadyFilters);
  const [applied, setApplied] = useState<ReadyFilters>(emptyReadyFilters);
  const [page, setPage] = useState(1);
  const [assigning, setAssigning] = useState<ReadyParcelResponse | null>(null);

  const result = useApi<PagedResponse<ReadyParcelResponse>>(`${deliveryApi.ready}${buildDeliveryQuery(readyQuery(applied, page))}`, reloadKey);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyReadyFilters());
    setApplied(emptyReadyFilters());
    setPage(1);
  }

  return (
    <div className="space-y-4">
      <form onSubmit={handleSubmit} role="search" aria-label="Filter parcels ready to assign" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField id="ready-search" label="Search" type="search" placeholder="Tracking number, receiver or district" value={draft.search} onChange={(search) => setDraft((d) => ({ ...d, search }))} />
          {superAdmin && (
            <SelectField id="ready-branch" label="Branch" placeholder="Any branch" value={draft.branchId} onChange={(branchId) => setDraft((d) => ({ ...d, branchId }))} options={branchOptions} />
          )}
        </div>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit">Apply filters</PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Clear
          </SecondaryButton>
        </div>
      </form>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading parcels…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No parcels are waiting for an agent.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <thead className={theadClass}>
                <tr>
                  <Th>Tracking number</Th>
                  <Th>Now at</Th>
                  <Th>Receiver</Th>
                  <Th>Service</Th>
                  <Th right>Cash to collect</Th>
                  <Th>Waiting</Th>
                  <Th right>Action</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((p) => (
                  <tr key={p.parcelId} className="hover:bg-slate-50">
                    <td className="px-4 py-3">
                      <Link to={`/parcels/${p.parcelId}`} className="font-mono font-medium text-brand-700 underline">
                        {p.trackingNumber}
                      </Link>
                      {p.failedAttempts > 0 && <p className="text-xs font-semibold text-red-700">{p.failedAttempts} failed attempt{p.failedAttempts === 1 ? '' : 's'}</p>}
                    </td>
                    <td className="px-4 py-3">
                      {p.location}
                      <p className="text-xs text-slate-500">{p.statusName}</p>
                    </td>
                    <td className="px-4 py-3">
                      {p.receiverName}
                      <p className="text-xs text-slate-500">
                        {p.receiverMunicipality}, {p.receiverDistrict}
                      </p>
                    </td>
                    <td className="px-4 py-3">{serviceLabel(p.serviceType)}</td>
                    <td className="px-4 py-3 text-right tabular-nums">{p.codAmount > 0 ? formatNpr(p.codAmount) : '—'}</td>
                    <td className="px-4 py-3">
                      <WaitBadge hours={p.waitingHours} />
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <button type="button" className="text-sm font-medium text-brand-700 underline" onClick={() => setAssigning(p)}>
                        Assign to agent
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="parcel" onPage={setPage} />
          </>
        )}
      </div>

      <AssignAgentDialog parcel={assigning} limitToBranchId={limitToBranchId} branchOptions={branchOptions} onClose={() => setAssigning(null)} onSaved={onAssigned} />
    </div>
  );
}

function AssignmentsTab({ reloadKey, onChanged }: { reloadKey: number; onChanged: (message: string) => void }) {
  const [draft, setDraft] = useState<AssignmentFilters>(emptyAssignmentFilters);
  const [applied, setApplied] = useState<AssignmentFilters>(emptyAssignmentFilters);
  const [page, setPage] = useState(1);
  const [cancelling, setCancelling] = useState<DeliveryAssignmentResponse | null>(null);
  const [viewing, setViewing] = useState<DeliveryProofResponse | null>(null);

  const agents = useApi<PagedResponse<DeliveryAgentResponse>>(`${deliveryApi.agents}${buildDeliveryQuery({ page: 1, pageSize: 100 })}`);
  const result = useApi<PagedResponse<DeliveryAssignmentResponse>>(`${deliveryApi.assignments}${buildDeliveryQuery(assignmentQuery(applied, page))}`, reloadKey);
  const set = (patch: Partial<AssignmentFilters>) => setDraft((d) => ({ ...d, ...patch }));
  const rangeInvalid = isRangeInvalid(draft.from, draft.to);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (rangeInvalid) return;
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyAssignmentFilters());
    setApplied(emptyAssignmentFilters());
    setPage(1);
  }

  return (
    <div className="space-y-4">
      <form onSubmit={handleSubmit} role="search" aria-label="Filter assignments" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <SelectField id="assignment-status" label="Status" placeholder="Any status" value={draft.status} onChange={(status) => set({ status })} options={STATUS_FILTER_OPTIONS} />
          <SelectField
            id="assignment-agent"
            label="Agent"
            placeholder="Any agent"
            value={draft.deliveryAgentId}
            onChange={(deliveryAgentId) => set({ deliveryAgentId })}
            options={agents.status === 'loaded' ? agents.data.items.map((a) => ({ value: a.id, label: `${a.fullName}${a.isActive ? '' : ' (inactive)'}` })) : []}
          />
          <TextField id="assignment-from" label="Given from" type="date" value={draft.from} onChange={(from) => set({ from })} />
          <TextField id="assignment-to" label="Given until" type="date" value={draft.to} onChange={(to) => set({ to })} error={rangeInvalid ? 'The end date cannot be before the start date.' : undefined} />
        </div>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit" disabled={rangeInvalid}>
            Apply filters
          </PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Clear
          </SecondaryButton>
        </div>
      </form>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading assignments…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No assignments found.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <thead className={theadClass}>
                <tr>
                  <Th>Parcel</Th>
                  <Th>Agent</Th>
                  <Th>Status</Th>
                  <Th>Times</Th>
                  <Th right>Cash</Th>
                  <Th>Proof</Th>
                  <Th right>Action</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((a) => (
                  <tr key={a.id} className="align-top hover:bg-slate-50">
                    <td className="px-4 py-3">
                      <Link to={`/parcels/${a.parcelId}`} className="font-mono font-medium text-brand-700 underline">
                        {a.trackingNumber}
                      </Link>
                      <p className="text-xs text-slate-500">
                        {a.receiverName}, {a.receiverDistrict}
                      </p>
                      <p className="text-xs text-slate-500">Attempt {a.attemptNumber}</p>
                    </td>
                    <td className="px-4 py-3">{a.agentName}</td>
                    <td className="px-4 py-3">
                      <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${assignmentTone(a.status)}`}>{assignmentLabel(a.status)}</span>
                      {a.failureReason && <p className="mt-1 max-w-56 text-xs text-red-700">{a.failureReason}</p>}
                    </td>
                    <td className="px-4 py-3 text-xs text-slate-600">
                      <p>Given {formatNepalDateTime(a.assignedAtUtc)}</p>
                      {a.startedAtUtc && <p>Started {formatNepalDateTime(a.startedAtUtc)}</p>}
                      {a.completedAtUtc && <p>Finished {formatNepalDateTime(a.completedAtUtc)}</p>}
                    </td>
                    <td className="px-4 py-3 text-right tabular-nums">
                      {a.codAmount > 0 ? (
                        <>
                          <p>{formatNpr(a.codAmount)}</p>
                          <p className="text-xs text-slate-500">{a.codCollectedAmount ? `Collected ${formatNpr(a.codCollectedAmount)}` : 'Not collected'}</p>
                        </>
                      ) : (
                        '—'
                      )}
                    </td>
                    <td className="px-4 py-3">
                      <ProofChips proofs={a.proofs} onOpen={setViewing} />
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      {(a.status === 'Assigned' || a.status === 'Accepted') && (
                        <button type="button" className="text-sm font-medium text-slate-700 underline" onClick={() => setCancelling(a)}>
                          Take back
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="assignment" onPage={setPage} />
          </>
        )}
      </div>

      <Dialog open={cancelling !== null} onClose={() => setCancelling(null)} title={cancelling ? `Take ${cancelling.trackingNumber} back` : 'Take back'}>
        {cancelling && (
          <CancelBody
            assignment={cancelling}
            onClose={() => setCancelling(null)}
            onSaved={() => onChanged(`${cancelling.trackingNumber} was taken back from ${cancelling.agentName}. It can be given to another agent.`)}
          />
        )}
      </Dialog>
      <ProofViewer proof={viewing} onClose={() => setViewing(null)} />
    </div>
  );
}

function CancelBody({ assignment, onClose, onSaved }: { assignment: DeliveryAssignmentResponse; onClose: () => void; onSaved: () => void }) {
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const problem = validateCancelReason(reason);
    setError(problem);
    setSummary(null);
    if (problem) return;

    setBusy(true);
    try {
      await api.post(deliveryApi.assignmentCancel(assignment.id), { reason: reason.trim() });
      onSaved();
      onClose();
    } catch (err) {
      setSummary(err instanceof ApiError ? (err.status === 403 ? 'You are not allowed to take this parcel back.' : problemSummary(err.problem)) : 'Could not reach the server. Please try again shortly.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={(e) => void handleSubmit(e)} noValidate className="space-y-4">
      <p className="text-sm text-slate-700">
        {assignment.agentName} has not started this delivery, so you can take it back. It will be ready to give to another agent. A reason is saved in the audit trail.
      </p>
      {summary && <Alert>{summary}</Alert>}
      <TextAreaField id="cancel-reason" label="Reason" required maxLength={CANCEL_REASON_MAX_LENGTH} value={reason} onChange={setReason} error={error ?? undefined} />
      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose}>
          Keep it with the agent
        </SecondaryButton>
        <PrimaryButton type="submit" disabled={busy}>
          {busy ? 'Saving…' : 'Take parcel back'}
        </PrimaryButton>
      </div>
    </form>
  );
}
