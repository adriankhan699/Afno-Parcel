import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { Alert, PrimaryButton, SecondaryButton, SelectField } from '../../components/form';
import { TableShell, Th, WaitBadge, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import { adminApi } from '../../lib/adminApi';
import { formatNepalDateTime } from '../../lib/format';
import { formatWeight, serviceLabel } from '../../lib/labels';
import { buildQuery, emptyWaitingFilters, waitingQuery, warehousesForBranch, type WaitingFilters } from '../../lib/network';
import { useApi } from '../../lib/useApi';
import type { BranchResponse, PagedResponse, WaitingKind, WaitingParcelDto, WarehouseResponse } from '../../types/api';

interface SectionProps {
  kind: WaitingKind;
  title: string;
  description: string;
  where: string;
  filters: WaitingFilters;
}

function WaitingSection({ kind, title, description, where, filters }: SectionProps) {
  const [page, setPage] = useState(1);
  // A new filter set starts again from page one.
  const key = `${filters.branchId}|${filters.warehouseId}`;
  const [seenKey, setSeenKey] = useState(key);
  if (seenKey !== key) {
    setSeenKey(key);
    setPage(1);
  }

  const result = useApi<PagedResponse<WaitingParcelDto>>(`${adminApi.waitingParcels}${buildQuery(waitingQuery({ ...filters, kind }, seenKey === key ? page : 1))}`);
  const headingId = `waiting-${kind.toLowerCase()}`;

  return (
    <section aria-labelledby={headingId} className="space-y-3">
      <div>
        <h3 id={headingId} className="text-base font-semibold">
          {title}
          {result.status === 'loaded' && <span className="ml-2 text-sm font-normal text-slate-500">({result.data.totalCount})</span>}
        </h3>
        <p className="text-sm text-slate-600">{description}</p>
      </div>
      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">Nothing is waiting. Well done.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <thead className={theadClass}>
                <tr>
                  <Th>Tracking number</Th>
                  <Th>Status</Th>
                  <Th>{where}</Th>
                  <Th>Service</Th>
                  <Th>Destination</Th>
                  <Th right>Qty</Th>
                  <Th right>Weight</Th>
                  <Th>Waiting since</Th>
                  <Th>Waiting</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((p) => (
                  <tr key={p.parcelId} className="hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3">
                      <Link to={`/parcels/${p.parcelId}`} className="font-mono font-medium text-brand-700 underline">
                        {p.trackingNumber}
                      </Link>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">{p.statusName}</td>
                    <td className="px-4 py-3">{p.location}</td>
                    <td className="whitespace-nowrap px-4 py-3">{serviceLabel(p.serviceType)}</td>
                    <td className="whitespace-nowrap px-4 py-3">{p.destinationDistrict}</td>
                    <td className="px-4 py-3 text-right">{p.quantity}</td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">{formatWeight(p.weightKg)}</td>
                    <td className="whitespace-nowrap px-4 py-3">{formatNepalDateTime(p.waitingSinceUtc)}</td>
                    <td className="px-4 py-3">
                      <WaitBadge hours={p.waitingHours} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="parcel" onPage={setPage} />
          </>
        )}
      </div>
    </section>
  );
}

export default function WaitingParcelsPage() {
  const [draft, setDraft] = useState<WaitingFilters>(emptyWaitingFilters);
  const [applied, setApplied] = useState<WaitingFilters>(emptyWaitingFilters);
  const branches = useApi<PagedResponse<BranchResponse>>(`${adminApi.branches}?page=1&pageSize=100`);
  const warehouses = useApi<PagedResponse<WarehouseResponse>>(`${adminApi.warehouses}?page=1&pageSize=100`);

  const branchOptions = branches.status === 'loaded' ? branches.data.items.map((b) => ({ value: b.id, label: `${b.name} (${b.code})` })) : [];
  const warehouseOptions =
    warehouses.status === 'loaded' ? warehousesForBranch(warehouses.data.items, draft.branchId).map((w) => ({ value: w.id, label: `${w.name} (${w.code})` })) : [];

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setApplied(draft);
  }

  function handleReset() {
    setDraft(emptyWaitingFilters());
    setApplied(emptyWaitingFilters());
  }

  return (
    <div className="space-y-8">
      <h2 className="text-lg font-semibold">Waiting parcels</h2>

      <form onSubmit={handleSubmit} role="search" aria-label="Filter waiting parcels" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2">
          <SelectField
            id="waiting-branch"
            label="Branch"
            placeholder="All branches"
            value={draft.branchId}
            onChange={(branchId) => setDraft((d) => ({ ...d, branchId, warehouseId: '' }))}
            options={branchOptions}
          />
          <SelectField
            id="waiting-warehouse"
            label="Warehouse"
            placeholder={draft.branchId ? 'All warehouses of the branch' : 'Choose a branch first'}
            value={draft.warehouseId}
            disabled={!draft.branchId}
            onChange={(warehouseId) => setDraft((d) => ({ ...d, warehouseId }))}
            options={warehouseOptions}
          />
        </div>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit">Apply filters</PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Clear
          </SecondaryButton>
        </div>
      </form>

      <WaitingSection
        kind="Pickup"
        title="Waiting for pickup"
        description="Booked parcels that have not yet been collected from the sender. Anything waiting a day or more is highlighted."
        where="Branch"
        filters={applied}
      />
      <WaitingSection
        kind="Dispatch"
        title="Waiting for dispatch"
        description="Parcels sitting in a warehouse that have not yet been dispatched onward."
        where="Warehouse"
        filters={applied}
      />
    </div>
  );
}
