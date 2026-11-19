import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { useAuth } from '../../auth/useAuth';
import ConfirmDialog from '../../components/ConfirmDialog';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../components/form';
import { ActiveBadge, CapacityBar, TableShell, Th, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import WarehouseFormDialog from '../../components/WarehouseFormDialog';
import { api } from '../../lib/apiClient';
import { canManageNetwork } from '../../lib/auth';
import { buildQuery, emptyWarehouseFilters, warehouseQuery, type WarehouseFilters } from '../../lib/network';
import { useApi } from '../../lib/useApi';
import type { BranchResponse, PagedResponse, WarehouseResponse } from '../../types/api';

export default function WarehousesAdminPage() {
  const auth = useAuth();
  const manager = canManageNetwork(auth.user?.roles);

  const [draft, setDraft] = useState<WarehouseFilters>(emptyWarehouseFilters);
  const [applied, setApplied] = useState<WarehouseFilters>(emptyWarehouseFilters);
  const [page, setPage] = useState(1);
  const [reloadKey, setReloadKey] = useState(0);
  const [editing, setEditing] = useState<WarehouseResponse | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const [toggling, setToggling] = useState<WarehouseResponse | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const branches = useApi<PagedResponse<BranchResponse>>('/api/v1/branches?page=1&pageSize=100');
  const result = useApi<PagedResponse<WarehouseResponse>>(`/api/v1/warehouses${buildQuery(warehouseQuery(applied, page))}`, reloadKey);
  const reload = () => setReloadKey((k) => k + 1);
  const set = (patch: Partial<WarehouseFilters>) => setDraft((d) => ({ ...d, ...patch }));

  const branchOptions = branches.status === 'loaded' ? branches.data.items.map((b) => ({ value: b.id, label: `${b.name} (${b.code})` })) : [];

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyWarehouseFilters());
    setApplied(emptyWarehouseFilters());
    setPage(1);
  }

  async function toggleActive(warehouse: WarehouseResponse) {
    await api.postEmpty(`/api/v1/warehouses/${warehouse.id}/${warehouse.isActive ? 'deactivate' : 'activate'}`);
    setNotice(`${warehouse.name} is now ${warehouse.isActive ? 'inactive' : 'active'}.`);
    reload();
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">Warehouses</h2>
        {manager && (
          <PrimaryButton
            type="button"
            onClick={() => {
              setEditing(null);
              setFormOpen(true);
            }}
          >
            New warehouse
          </PrimaryButton>
        )}
      </div>

      {notice && <Alert tone="success">{notice}</Alert>}

      <form onSubmit={handleSubmit} role="search" aria-label="Filter warehouses" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-3">
          <TextField id="warehouse-search" label="Search" type="search" placeholder="Code or name" value={draft.search} onChange={(search) => set({ search })} />
          <SelectField id="warehouse-branch-filter" label="Branch" placeholder="All branches" value={draft.branchId} onChange={(branchId) => set({ branchId })} options={branchOptions} />
          {manager && (
            <label className="flex items-end gap-2 pb-2 text-sm text-slate-700">
              <input type="checkbox" checked={draft.includeInactive} onChange={(e) => set({ includeInactive: e.target.checked })} />
              Include inactive warehouses
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
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading warehouses…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No warehouses found.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <thead className={theadClass}>
                <tr>
                  <Th>Code</Th>
                  <Th>Name</Th>
                  <Th>Branch</Th>
                  <Th>District</Th>
                  <Th>Held / capacity</Th>
                  <Th right>Inbound</Th>
                  <Th>Status</Th>
                  <Th right>Actions</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((w) => (
                  <tr key={w.id} className="hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3 font-mono font-medium">{w.code}</td>
                    <td className="px-4 py-3">{w.name}</td>
                    <td className="px-4 py-3">{w.branchName}</td>
                    <td className="whitespace-nowrap px-4 py-3">{w.address.district}</td>
                    <td className="px-4 py-3">
                      <CapacityBar held={w.parcelsHeld} capacity={w.capacityParcels} />
                    </td>
                    <td className="px-4 py-3 text-right">{w.parcelsInbound}</td>
                    <td className="px-4 py-3">
                      <ActiveBadge active={w.isActive} />
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <Link to={`/admin/warehouses/${w.id}/inventory`} className="mr-3 text-sm font-medium text-brand-700 underline">
                        Inventory
                      </Link>
                      {manager && (
                        <>
                          <button
                            type="button"
                            className="mr-3 text-sm font-medium text-brand-700 underline"
                            onClick={() => {
                              setEditing(w);
                              setFormOpen(true);
                            }}
                          >
                            Edit
                          </button>
                          <button type="button" className="text-sm font-medium text-slate-700 underline" onClick={() => setToggling(w)}>
                            {w.isActive ? 'Deactivate' : 'Activate'}
                          </button>
                        </>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="warehouse" onPage={setPage} />
          </>
        )}
      </div>

      <WarehouseFormDialog
        open={formOpen}
        warehouse={editing}
        onClose={() => setFormOpen(false)}
        onSaved={() => {
          setNotice(editing ? 'Warehouse updated.' : 'Warehouse created.');
          reload();
        }}
      />

      <ConfirmDialog
        open={toggling !== null}
        title={toggling?.isActive ? `Deactivate ${toggling.name}?` : `Activate ${toggling?.name ?? ''}?`}
        message={
          toggling?.isActive
            ? 'A deactivated warehouse can no longer receive or dispatch parcels. It cannot be deactivated while it holds parcels or parcels are on their way to it.'
            : 'The warehouse will be available for scanning and staff assignment again.'
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
