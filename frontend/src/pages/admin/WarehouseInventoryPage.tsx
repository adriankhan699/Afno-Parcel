import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router';
import { Alert, PrimaryButton, SecondaryButton, TextField } from '../../components/form';
import { CapacityBar, TableShell, Th, WaitBadge, theadClass } from '../../components/NetworkBits';
import Pagination from '../../components/Pagination';
import { adminApi } from '../../lib/adminApi';
import { formatNepalDateTime } from '../../lib/format';
import { formatWeight, serviceLabel } from '../../lib/labels';
import { buildQuery, inventoryQuery, utilisation } from '../../lib/network';
import { useApi } from '../../lib/useApi';
import type { InventoryParcelDto, WarehouseInventoryResponse } from '../../types/api';

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <p className="text-xs uppercase tracking-wide text-slate-500">{label}</p>
      <p className="mt-1 text-2xl font-semibold text-slate-900">{value}</p>
    </div>
  );
}

function percentText(inventory: WarehouseInventoryResponse): string {
  const percent = inventory.utilisationPercent ?? utilisation(inventory.parcelsHeld, inventory.capacityParcels);
  return percent === null ? '—' : `${percent}%`;
}

function ParcelRows({ parcels, sinceLabel }: { parcels: InventoryParcelDto[]; sinceLabel: string }) {
  return (
    <TableShell>
      <thead className={theadClass}>
        <tr>
          <Th>Tracking number</Th>
          <Th>Status</Th>
          <Th>Service</Th>
          <Th>Destination</Th>
          <Th right>Qty</Th>
          <Th right>Weight</Th>
          <Th>{sinceLabel}</Th>
          <Th>Waiting</Th>
        </tr>
      </thead>
      <tbody className="divide-y divide-slate-100">
        {parcels.map((p) => (
          <tr key={p.parcelId} className="hover:bg-slate-50">
            <td className="whitespace-nowrap px-4 py-3">
              <Link to={`/parcels/${p.parcelId}`} className="font-mono font-medium text-brand-700 underline">
                {p.trackingNumber}
              </Link>
            </td>
            <td className="whitespace-nowrap px-4 py-3">{p.statusName}</td>
            <td className="whitespace-nowrap px-4 py-3">{serviceLabel(p.serviceType)}</td>
            <td className="whitespace-nowrap px-4 py-3">{p.destinationDistrict}</td>
            <td className="px-4 py-3 text-right">{p.quantity}</td>
            <td className="whitespace-nowrap px-4 py-3 text-right">{formatWeight(p.weightKg)}</td>
            <td className="whitespace-nowrap px-4 py-3">{formatNepalDateTime(p.sinceUtc)}</td>
            <td className="px-4 py-3">
              <WaitBadge hours={p.waitingHours} />
            </td>
          </tr>
        ))}
      </tbody>
    </TableShell>
  );
}

export default function WarehouseInventoryPage() {
  const { id } = useParams();
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);

  const result = useApi<WarehouseInventoryResponse>(id ? `${adminApi.warehouseInventory(id)}${buildQuery(inventoryQuery(search, page))}` : null);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSearch(draft);
    setPage(1);
  }

  function handleClear() {
    setDraft('');
    setSearch('');
    setPage(1);
  }

  return (
    <div className="space-y-6">
      <p>
        <Link to="/admin/warehouses" className="text-sm text-brand-700 underline">
          ← All warehouses
        </Link>
      </p>

      {result.status === 'loading' && <p className="text-sm text-slate-500">Loading inventory…</p>}
      {result.status === 'error' && <Alert>{result.message}</Alert>}

      {result.status === 'loaded' && (
        <>
          <h2 className="text-lg font-semibold">{result.data.warehouseName} · inventory</h2>

          <div className="grid gap-4 sm:grid-cols-4">
            <Stat label="Held now" value={result.data.parcelsHeld} />
            <Stat label="On their way here" value={result.data.parcelsInbound} />
            <Stat label="Capacity" value={result.data.capacityParcels ?? 'Not set'} />
            <Stat label="Utilisation" value={percentText(result.data)} />
          </div>
          <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <CapacityBar held={result.data.parcelsHeld} capacity={result.data.capacityParcels} />
          </div>

          <section aria-labelledby="held-heading" className="space-y-3">
            <h3 id="held-heading" className="text-base font-semibold">
              Parcels held here
            </h3>
            <form onSubmit={handleSubmit} role="search" aria-label="Search held parcels" className="flex flex-wrap items-end gap-3">
              <TextField id="inventory-search" label="Tracking number" type="search" placeholder="AFN…" value={draft} onChange={setDraft} className="w-64" />
              <PrimaryButton type="submit">Search</PrimaryButton>
              <SecondaryButton type="button" onClick={handleClear}>
                Clear
              </SecondaryButton>
            </form>
            {result.data.held.items.length === 0 ? (
              <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">
                {search ? 'No held parcel matches your search.' : 'No parcels are held at this warehouse.'}
              </p>
            ) : (
              <>
                <ParcelRows parcels={result.data.held.items} sinceLabel="Arrived" />
                <Pagination
                  page={result.data.held.page}
                  totalPages={result.data.held.totalPages}
                  totalCount={result.data.held.totalCount}
                  hasNextPage={result.data.held.hasNextPage}
                  noun="parcel"
                  onPage={setPage}
                />
              </>
            )}
          </section>

          <section aria-labelledby="inbound-heading" className="space-y-3">
            <h3 id="inbound-heading" className="text-base font-semibold">
              On their way here
            </h3>
            {result.data.inbound.length === 0 ? (
              <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No parcels are heading to this warehouse.</p>
            ) : (
              <ParcelRows parcels={result.data.inbound} sinceLabel="Dispatched" />
            )}
          </section>
        </>
      )}
    </div>
  );
}
