import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { useAuth } from '../auth/useAuth';
import FindByScan from '../components/FindByScan';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../components/form';
import { formatNepalDateTime, formatNpr } from '../lib/format';
import { CHANNEL_LABELS, PAYMENT_LABELS, SERVICE_LABELS, STATUS_LABELS, paymentLabel, serviceLabel, statusLabel, statusTone } from '../lib/labels';
import {
  emptyFilters,
  filtersToQuery,
  isDateRangeInvalid,
  toQueryString,
  type ParcelFilters,
} from '../lib/parcelQuery';
import { useApi } from '../lib/useApi';
import { PAYMENT_METHODS, PARCEL_STATUSES, SERVICE_TYPES, type PagedResponse, type ParcelSummaryResponse } from '../types/api';

export default function ParcelsPage() {
  const auth = useAuth();
  const [draft, setDraft] = useState<ParcelFilters>(emptyFilters);
  const [applied, setApplied] = useState<ParcelFilters>(emptyFilters);
  const [page, setPage] = useState(1);

  const result = useApi<PagedResponse<ParcelSummaryResponse>>(`/api/v1/parcels${toQueryString(filtersToQuery(applied, page))}`);
  const rangeInvalid = isDateRangeInvalid(draft);
  const set = (patch: Partial<ParcelFilters>) => setDraft((d) => ({ ...d, ...patch }));

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (rangeInvalid) return;
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyFilters());
    setApplied(emptyFilters());
    setPage(1);
  }

  const showWho = auth.isStaff;

  return (
    <div>
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-2xl font-bold tracking-tight">{auth.isStaff ? 'Parcels' : 'My parcels'}</h1>
          <p className="mt-1 text-sm text-slate-600">
            {auth.isStaff ? 'Search and filter all bookings.' : 'Your bookings, newest first.'}
          </p>
        </div>
        {(auth.isCustomer || auth.isCounterStaff) && (
          <Link to="/book" className="rounded-lg bg-brand-600 px-4 py-2 text-sm font-semibold text-white hover:bg-brand-700">
            {auth.isCounterStaff ? 'New counter booking' : 'Book a parcel'}
          </Link>
        )}
      </div>

      {auth.isStaff && <FindByScan className="mt-6 max-w-xl" />}

      <form onSubmit={handleSubmit} noValidate role="search" aria-label="Filter parcels" className="mt-6 rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <TextField
            id="search"
            label="Search"
            type="search"
            placeholder="Tracking no., name or phone"
            className="sm:col-span-2"
            value={draft.search}
            onChange={(search) => set({ search })}
          />
          <SelectField
            id="status"
            label="Status"
            placeholder="Any status"
            value={draft.status}
            onChange={(status) => set({ status })}
            options={PARCEL_STATUSES.map((s) => ({ value: s, label: STATUS_LABELS[s] ?? s }))}
          />
          <SelectField
            id="serviceType"
            label="Delivery type"
            placeholder="Any type"
            value={draft.serviceType}
            onChange={(serviceType) => set({ serviceType })}
            options={SERVICE_TYPES.map((s) => ({ value: s, label: SERVICE_LABELS[s] }))}
          />
          <SelectField
            id="paymentMethod"
            label="Payment method"
            placeholder="Any method"
            value={draft.paymentMethod}
            onChange={(paymentMethod) => set({ paymentMethod })}
            options={PAYMENT_METHODS.map((m) => ({ value: m, label: PAYMENT_LABELS[m] }))}
          />
          <SelectField
            id="cod"
            label="Cash on delivery"
            placeholder="Any"
            value={draft.cod}
            onChange={(cod) => set({ cod: cod as ParcelFilters['cod'] })}
            options={[
              { value: 'yes', label: 'Money to collect' },
              { value: 'no', label: 'Nothing to collect' },
            ]}
          />
          {auth.isStaff && (
            <SelectField
              id="channel"
              label="Booked via"
              placeholder="Any channel"
              value={draft.channel}
              onChange={(channel) => set({ channel })}
              options={Object.entries(CHANNEL_LABELS).map(([value, label]) => ({ value, label }))}
            />
          )}
          <TextField id="bookedFrom" label="Booked from" type="date" value={draft.bookedFrom} onChange={(bookedFrom) => set({ bookedFrom })} />
          <TextField
            id="bookedTo"
            label="Booked to"
            type="date"
            value={draft.bookedTo}
            onChange={(bookedTo) => set({ bookedTo })}
            error={rangeInvalid ? '“To” must be on or after “From”.' : undefined}
          />
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

      <div className="mt-6" aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading parcels…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">
            No parcels found{applied.search || applied.status ? ' for these filters' : ''}.
          </p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <div className="overflow-x-auto rounded-xl border border-slate-200 bg-white shadow-sm">
              <table className="min-w-full text-left text-sm">
                <thead className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
                  <tr>
                    <th scope="col" className="px-4 py-3">Tracking no.</th>
                    <th scope="col" className="px-4 py-3">Booked</th>
                    {showWho && <th scope="col" className="px-4 py-3">Sender</th>}
                    <th scope="col" className="px-4 py-3">Receiver</th>
                    <th scope="col" className="px-4 py-3">Delivery</th>
                    <th scope="col" className="px-4 py-3">Status</th>
                    <th scope="col" className="px-4 py-3 text-right">Charge</th>
                    <th scope="col" className="px-4 py-3 text-right">To collect</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {result.data.items.map((p) => (
                    <tr key={p.id} className="hover:bg-slate-50">
                      <td className="whitespace-nowrap px-4 py-3 font-mono font-medium">
                        <Link to={`/parcels/${p.id}`} className="text-brand-700 underline">
                          {p.trackingNumber}
                        </Link>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-slate-600">{formatNepalDateTime(p.bookedAtUtc)}</td>
                      {showWho && <td className="px-4 py-3">{p.senderName}</td>}
                      <td className="px-4 py-3">
                        {p.receiverName}
                        <span className="block text-xs text-slate-500">
                          {p.receiverMunicipality}, {p.receiverDistrict}
                        </span>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3">
                        {serviceLabel(p.serviceType)}
                        <span className="block text-xs text-slate-500">{paymentLabel(p.paymentMethod)}</span>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3">
                        <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${statusTone(p.status)}`}>
                          {statusLabel(p.status)}
                        </span>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-right">{formatNpr(p.deliveryCharge)}</td>
                      <td className="whitespace-nowrap px-4 py-3 text-right">{p.codAmount > 0 ? formatNpr(p.codAmount) : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <nav aria-label="Pagination" className="mt-4 flex items-center justify-between gap-3 text-sm">
              <p className="text-slate-600">
                Page {result.data.page} of {Math.max(result.data.totalPages, 1)} · {result.data.totalCount} parcel
                {result.data.totalCount === 1 ? '' : 's'}
              </p>
              <div className="flex gap-2">
                <SecondaryButton type="button" disabled={result.data.page <= 1} onClick={() => setPage((p) => Math.max(1, p - 1))}>
                  Previous
                </SecondaryButton>
                <SecondaryButton type="button" disabled={!result.data.hasNextPage} onClick={() => setPage((p) => p + 1)}>
                  Next
                </SecondaryButton>
              </div>
            </nav>
          </>
        )}
      </div>
    </div>
  );
}
