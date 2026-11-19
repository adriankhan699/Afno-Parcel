import { useState, type FormEvent } from 'react';
import { useAuth } from '../../auth/useAuth';
import ConfirmDialog from '../../components/ConfirmDialog';
import Dialog from '../../components/Dialog';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../components/form';
import { ActiveBadge, TableShell, Th, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import { adminApi } from '../../lib/adminApi';
import { ApiError, api } from '../../lib/apiClient';
import { isSuperAdmin } from '../../lib/auth';
import { formatNepalDateTime } from '../../lib/format';
import {
  buildAssignRequest,
  buildCreateStaffRequest,
  buildQuery,
  emptyStaffAccountForm,
  emptyStaffFilters,
  roleLabel,
  staffQuery,
  validateAssignment,
  validateStaffAccountForm,
  warehousesForBranch,
  type StaffAccountForm,
  type StaffFilters,
} from '../../lib/network';
import { problemSummary, problemToFieldErrors } from '../../lib/problems';
import { useApi } from '../../lib/useApi';
import { STAFF_ROLES, type BranchResponse, type PagedResponse, type StaffMemberResponse, type WarehouseResponse } from '../../types/api';

const ASSIGNABLE_ROLES = STAFF_ROLES.map((role) => ({ value: role, label: roleLabel(role) }));

export default function StaffAdminPage() {
  const auth = useAuth();
  const superAdmin = isSuperAdmin(auth.user?.roles);

  const [draft, setDraft] = useState<StaffFilters>(emptyStaffFilters);
  const [applied, setApplied] = useState<StaffFilters>(emptyStaffFilters);
  const [page, setPage] = useState(1);
  const [reloadKey, setReloadKey] = useState(0);
  const [assigning, setAssigning] = useState<StaffMemberResponse | null>(null);
  const [removing, setRemoving] = useState<StaffMemberResponse | null>(null);
  const [creating, setCreating] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const branches = useApi<PagedResponse<BranchResponse>>(`${adminApi.branches}?page=1&pageSize=100`);
  const warehouses = useApi<PagedResponse<WarehouseResponse>>(`${adminApi.warehouses}?page=1&pageSize=100`);
  const result = useApi<PagedResponse<StaffMemberResponse>>(`${adminApi.staff}${buildQuery(staffQuery(applied, page))}`, reloadKey);
  const reload = () => setReloadKey((k) => k + 1);
  const set = (patch: Partial<StaffFilters>) => setDraft((d) => ({ ...d, ...patch }));

  const branchList = branches.status === 'loaded' ? branches.data.items.filter((b) => b.isActive) : [];
  const warehouseList = warehouses.status === 'loaded' ? warehouses.data.items.filter((w) => w.isActive) : [];
  const branchOptions = branchList.map((b) => ({ value: b.id, label: `${b.name} (${b.code})` }));
  const filterWarehouseOptions = warehousesForBranch(warehouseList, draft.branchId).map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }));

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyStaffFilters());
    setApplied(emptyStaffFilters());
    setPage(1);
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">Staff and assignments</h2>
        {superAdmin && (
          <PrimaryButton type="button" onClick={() => setCreating(true)}>
            New staff account
          </PrimaryButton>
        )}
      </div>

      <p className="text-sm text-slate-600">
        Staff can scan parcels only at warehouses of the branch they are assigned to. A staff member with no assignment cannot scan.
      </p>

      {notice && <Alert tone="success">{notice}</Alert>}

      <form onSubmit={handleSubmit} role="search" aria-label="Filter staff" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <TextField id="staff-search" label="Search" type="search" placeholder="Name or email" value={draft.search} onChange={(search) => set({ search })} />
          <SelectField id="staff-role-filter" label="Role" placeholder="Any role" value={draft.role} onChange={(role) => set({ role })} options={ASSIGNABLE_ROLES} />
          <SelectField
            id="staff-branch-filter"
            label="Branch"
            placeholder="Any branch"
            value={draft.branchId}
            disabled={draft.unassigned}
            onChange={(branchId) => set({ branchId, warehouseId: '' })}
            options={branchOptions}
          />
          <SelectField
            id="staff-warehouse-filter"
            label="Warehouse"
            placeholder={draft.branchId ? 'Any warehouse' : 'Choose a branch first'}
            value={draft.warehouseId}
            disabled={draft.unassigned || !draft.branchId}
            onChange={(warehouseId) => set({ warehouseId })}
            options={filterWarehouseOptions}
          />
        </div>
        <label className="mt-4 flex items-center gap-2 text-sm text-slate-700">
          <input type="checkbox" checked={draft.unassigned} onChange={(e) => set({ unassigned: e.target.checked, branchId: '', warehouseId: '' })} />
          Only staff with no assignment
        </label>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit">Apply filters</PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Clear
          </SecondaryButton>
        </div>
      </form>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading staff…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No staff members found.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <thead className={theadClass}>
                <tr>
                  <Th>Name</Th>
                  <Th>Role</Th>
                  <Th>Status</Th>
                  <Th>Assignment</Th>
                  <Th right>Actions</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((s) => (
                  <tr key={s.userId} className="hover:bg-slate-50">
                    <td className="px-4 py-3">
                      <p className="font-medium text-slate-900">{s.fullName}</p>
                      <p className="text-xs text-slate-500">{s.email}</p>
                    </td>
                    <td className="px-4 py-3">{s.roles.map(roleLabel).join(', ')}</td>
                    <td className="px-4 py-3">
                      <ActiveBadge active={s.isActive} />
                    </td>
                    <td className="px-4 py-3">
                      {s.assignment ? (
                        <>
                          <p>{s.assignment.branchName}</p>
                          <p className="text-xs text-slate-500">
                            {s.assignment.warehouseName ? `${s.assignment.warehouseName} · ` : 'Any warehouse of the branch · '}
                            since {formatNepalDateTime(s.assignment.assignedAtUtc)}
                          </p>
                        </>
                      ) : (
                        <span className="rounded-full bg-amber-50 px-2 py-0.5 text-xs font-semibold text-amber-800">Not assigned</span>
                      )}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <button type="button" className="mr-3 text-sm font-medium text-brand-700 underline" onClick={() => setAssigning(s)}>
                        {s.assignment ? 'Reassign' : 'Assign'}
                      </button>
                      {s.assignment && (
                        <button type="button" className="text-sm font-medium text-slate-700 underline" onClick={() => setRemoving(s)}>
                          Remove
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="staff member" onPage={setPage} />
          </>
        )}
      </div>

      <Dialog open={assigning !== null} onClose={() => setAssigning(null)} title={assigning ? `Assign ${assigning.fullName}` : 'Assign staff'}>
        {assigning && (
          <AssignBody
            member={assigning}
            branches={branchList}
            warehouses={warehouseList}
            onClose={() => setAssigning(null)}
            onSaved={() => {
              setNotice(`${assigning.fullName} was assigned.`);
              reload();
            }}
          />
        )}
      </Dialog>

      <ConfirmDialog
        open={removing !== null}
        title={removing ? `Remove ${removing.fullName}'s assignment?` : 'Remove assignment?'}
        message="They will not be able to scan parcels until they are assigned to a branch again."
        confirmLabel="Remove assignment"
        onConfirm={async () => {
          if (!removing) return;
          await api.delete(adminApi.staffAssignment(removing.userId));
          setNotice(`${removing.fullName} no longer has an assignment.`);
          reload();
        }}
        onClose={() => setRemoving(null)}
      />

      <Dialog open={creating} onClose={() => setCreating(false)} title="New staff account">
        <CreateStaffBody
          onClose={() => setCreating(false)}
          onSaved={() => {
            setNotice('Staff account created. Share the password with them securely and ask them to change it.');
            reload();
          }}
        />
      </Dialog>
    </div>
  );
}

function AssignBody({
  member,
  branches,
  warehouses,
  onClose,
  onSaved,
}: {
  member: StaffMemberResponse;
  branches: BranchResponse[];
  warehouses: WarehouseResponse[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState({ branchId: member.assignment?.branchId ?? '', warehouseId: member.assignment?.warehouseId ?? '' });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const warehouseOptions = warehousesForBranch(warehouses, form.branchId).map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }));

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const found = validateAssignment(form, warehouses);
    setErrors(found);
    setSummary(null);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      await api.put<StaffMemberResponse>(adminApi.staffAssignment(member.userId), buildAssignRequest(form));
      onSaved();
      onClose();
    } catch (err) {
      if (err instanceof ApiError) {
        setErrors(problemToFieldErrors(err.problem));
        setSummary(err.status === 403 ? 'You are not allowed to assign staff to this branch.' : problemSummary(err.problem));
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
      <SelectField
        id="assign-branch"
        label="Branch"
        required
        placeholder="Choose a branch"
        value={form.branchId}
        onChange={(branchId) => setForm({ branchId, warehouseId: '' })}
        options={branches.map((b) => ({ value: b.id, label: `${b.name} (${b.code})` }))}
        error={errors.branchId}
      />
      <SelectField
        id="assign-warehouse"
        label="Warehouse (optional)"
        placeholder={form.branchId ? 'Any warehouse of this branch' : 'Choose a branch first'}
        value={form.warehouseId}
        disabled={!form.branchId}
        onChange={(warehouseId) => setForm((f) => ({ ...f, warehouseId }))}
        options={warehouseOptions}
        hint="Only this branch's warehouses are listed."
        error={errors.warehouseId}
      />
      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="submit" disabled={busy}>
          {busy ? 'Saving…' : 'Save assignment'}
        </PrimaryButton>
      </div>
    </form>
  );
}

function CreateStaffBody({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState<StaffAccountForm>(emptyStaffAccountForm);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<StaffAccountForm>) => setForm((f) => ({ ...f, ...patch }));

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const found = validateStaffAccountForm(form);
    setErrors(found);
    setSummary(null);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      await api.post(adminApi.createStaffUser, buildCreateStaffRequest(form));
      onSaved();
      onClose();
    } catch (err) {
      if (err instanceof ApiError) {
        setErrors(problemToFieldErrors(err.problem));
        setSummary(err.status === 403 ? 'Only a super admin can create staff accounts.' : problemSummary(err.problem));
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
      <TextField id="staff-full-name" label="Full name" required autoComplete="off" value={form.fullName} onChange={(fullName) => set({ fullName })} error={errors.fullName} />
      <TextField id="staff-email" label="Email" type="email" required autoComplete="off" value={form.email} onChange={(email) => set({ email })} error={errors.email} />
      <TextField id="staff-phone" label="Phone number (optional)" type="tel" inputMode="tel" value={form.phoneNumber} onChange={(phoneNumber) => set({ phoneNumber })} error={errors.phoneNumber} />
      <SelectField
        id="staff-role"
        label="Role"
        required
        placeholder="Choose a role"
        value={form.role}
        onChange={(role) => set({ role })}
        options={ASSIGNABLE_ROLES.filter((r) => r.value !== 'SuperAdmin')}
        error={errors.role}
      />
      <TextField
        id="staff-password"
        label="Temporary password"
        type="password"
        required
        autoComplete="new-password"
        value={form.password}
        onChange={(password) => set({ password })}
        hint="At least 10 characters with an upper-case letter, a lower-case letter and a digit."
        error={errors.password}
      />
      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="submit" disabled={busy}>
          {busy ? 'Creating…' : 'Create account'}
        </PrimaryButton>
      </div>
    </form>
  );
}
