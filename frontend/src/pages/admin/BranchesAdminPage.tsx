import { useState, type FormEvent } from 'react';
import { useAuth } from '../../auth/useAuth';
import BranchFormDialog from '../../components/BranchFormDialog';
import ConfirmDialog from '../../components/ConfirmDialog';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../components/form';
import { ActiveBadge, TableShell, Th, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import { api } from '../../lib/apiClient';
import { canManageNetwork } from '../../lib/auth';
import { buildQuery, branchQuery, emptyBranchFilters, type BranchFilters } from '../../lib/network';
import { useApi } from '../../lib/useApi';
import type { BranchResponse, PagedResponse } from '../../types/api';

export default function BranchesAdminPage() {
  const auth = useAuth();
  const manager = canManageNetwork(auth.user?.roles);

  const [draft, setDraft] = useState<BranchFilters>(emptyBranchFilters);
  const [applied, setApplied] = useState<BranchFilters>(emptyBranchFilters);
  const [page, setPage] = useState(1);
  const [reloadKey, setReloadKey] = useState(0);
  const [editing, setEditing] = useState<BranchResponse | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const [toggling, setToggling] = useState<BranchResponse | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const result = useApi<PagedResponse<BranchResponse>>(`/api/v1/branches${buildQuery(branchQuery(applied, page))}`, reloadKey);
  const reload = () => setReloadKey((k) => k + 1);
  const set = (patch: Partial<BranchFilters>) => setDraft((d) => ({ ...d, ...patch }));

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyBranchFilters());
    setApplied(emptyBranchFilters());
    setPage(1);
  }

  async function toggleActive(branch: BranchResponse) {
    await api.postEmpty(`/api/v1/branches/${branch.id}/${branch.isActive ? 'deactivate' : 'activate'}`);
    setNotice(`${branch.name} is now ${branch.isActive ? 'inactive' : 'active'}.`);
    reload();
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">Branches</h2>
        {manager && (
          <PrimaryButton
            type="button"
            onClick={() => {
              setEditing(null);
              setFormOpen(true);
            }}
          >
            New branch
          </PrimaryButton>
        )}
      </div>

      {notice && <Alert tone="success">{notice}</Alert>}

      <form onSubmit={handleSubmit} role="search" aria-label="Filter branches" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-3">
          <TextField id="branch-search" label="Search" type="search" placeholder="Code, name or district" value={draft.search} onChange={(search) => set({ search })} />
          <SelectField
            id="branch-type-filter"
            label="Type"
            placeholder="Any type"
            value={draft.type}
            onChange={(type) => set({ type })}
            options={[
              { value: 'Branch', label: 'Branch' },
              { value: 'HeadOffice', label: 'Head office' },
            ]}
          />
          {manager && (
            <label className="flex items-end gap-2 pb-2 text-sm text-slate-700">
              <input type="checkbox" checked={draft.includeInactive} onChange={(e) => set({ includeInactive: e.target.checked })} />
              Include inactive branches
            </label>
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
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading branches…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No branches found.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <thead className={theadClass}>
                <tr>
                  <Th>Code</Th>
                  <Th>Name</Th>
                  <Th>Type</Th>
                  <Th>District</Th>
                  <Th>Phone</Th>
                  <Th>Status</Th>
                  <Th right>Warehouses</Th>
                  <Th right>Staff</Th>
                  {manager && <Th right>Actions</Th>}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((b) => (
                  <tr key={b.id} className="hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3 font-mono font-medium">{b.code}</td>
                    <td className="px-4 py-3">{b.name}</td>
                    <td className="whitespace-nowrap px-4 py-3">{b.type === 'HeadOffice' ? 'Head office' : 'Branch'}</td>
                    <td className="whitespace-nowrap px-4 py-3">{b.address.district}</td>
                    <td className="whitespace-nowrap px-4 py-3">{b.phoneNumber}</td>
                    <td className="px-4 py-3">
                      <ActiveBadge active={b.isActive} />
                    </td>
                    <td className="px-4 py-3 text-right">{b.warehouseCount}</td>
                    <td className="px-4 py-3 text-right">{b.staffCount}</td>
                    {manager && (
                      <td className="whitespace-nowrap px-4 py-3 text-right">
                        <button
                          type="button"
                          className="mr-3 text-sm font-medium text-brand-700 underline"
                          onClick={() => {
                            setEditing(b);
                            setFormOpen(true);
                          }}
                        >
                          Edit
                        </button>
                        <button type="button" className="text-sm font-medium text-slate-700 underline" onClick={() => setToggling(b)}>
                          {b.isActive ? 'Deactivate' : 'Activate'}
                        </button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="branch" onPage={setPage} />
          </>
        )}
      </div>

      <BranchFormDialog
        open={formOpen}
        branch={editing}
        onClose={() => setFormOpen(false)}
        onSaved={() => {
          setNotice(editing ? 'Branch updated.' : 'Branch created.');
          reload();
        }}
      />

      <ConfirmDialog
        open={toggling !== null}
        title={toggling?.isActive ? `Deactivate ${toggling.name}?` : `Activate ${toggling?.name ?? ''}?`}
        message={
          toggling?.isActive
            ? 'An inactive branch is hidden from customers and cannot take bookings. It cannot be deactivated while it still has active warehouses or holds parcels.'
            : 'The branch will appear again in the branch list and can take bookings.'
        }
        confirmLabel={toggling?.isActive ? 'Deactivate' : 'Activate'}
        onConfirm={async () => {
          if (toggling) await toggleActive(toggling);
        }}
        onClose={() => setToggling(null)}
      />
    </div>
  );
}
