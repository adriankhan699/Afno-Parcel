import type { ReactNode } from 'react';
import { Link, useParams } from 'react-router';
import { Alert, PrimaryButton } from '../components/form';
import { formatAddress, formatNepalDateTime, formatNpr } from '../lib/format';
import { formatWeight, paymentLabel, serviceLabel, statusLabel } from '../lib/labels';
import { useApi } from '../lib/useApi';
import type { BookingPartyDto, ReceiptResponse } from '../types/api';

function PartyBlock({ title, party }: { title: string; party: BookingPartyDto }) {
  return (
    <section>
      <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-500">{title}</h2>
      <p className="mt-1 font-medium">{party.fullName}</p>
      <p>{party.phoneNumber}</p>
      <p className="text-slate-700">{formatAddress(party.address)}</p>
      {party.addressInstructions && <p className="text-xs text-slate-500">Instructions: {party.addressInstructions}</p>}
    </section>
  );
}

function Line({ label, children, strong = false }: { label: string; children: ReactNode; strong?: boolean }) {
  return (
    <tr className={strong ? 'border-t-2 border-slate-800 font-bold' : ''}>
      <th scope="row" className="py-1 pr-4 text-left font-normal text-slate-700">
        {strong ? <span className="font-bold">{label}</span> : label}
      </th>
      <td className="py-1 text-right">{children}</td>
    </tr>
  );
}

export default function ReceiptPage() {
  const { id = '' } = useParams();
  const result = useApi<ReceiptResponse>(id ? `/api/v1/parcels/${encodeURIComponent(id)}/receipt` : null);

  if (result.status === 'loading' || result.status === 'idle') return <p className="text-sm text-slate-500">Loading receipt…</p>;
  if (result.status === 'error') {
    return (
      <div className="space-y-4">
        <Alert>{result.message}</Alert>
        <Link to="/parcels" className="text-sm font-medium text-brand-700 underline">
          Back to parcels
        </Link>
      </div>
    );
  }

  const r = result.data;
  const p = r.parcel;
  const dims = p.lengthCm && p.widthCm && p.heightCm ? `${p.lengthCm} × ${p.widthCm} × ${p.heightCm} cm` : null;

  return (
    <div>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3 print:hidden">
        <Link to={`/parcels/${p.id}`} className="text-sm text-brand-700 underline">
          ← Back to parcel
        </Link>
        <PrimaryButton type="button" onClick={() => window.print()}>
          Print receipt
        </PrimaryButton>
      </div>

      <article
        aria-label={`Receipt ${r.receiptNumber}`}
        className="mx-auto max-w-3xl space-y-6 rounded-xl border border-slate-300 bg-white p-8 text-sm text-slate-900 shadow-sm print:max-w-none print:rounded-none print:border-0 print:p-0 print:shadow-none"
      >
        <header className="flex flex-wrap items-start justify-between gap-4 border-b border-slate-300 pb-4">
          <div>
            <p className="text-xl font-bold">{r.companyName}</p>
            {r.companyPhone && <p className="text-slate-600">☎ {r.companyPhone}</p>}
            {r.companyTaxNumber && <p className="text-slate-600">PAN/VAT: {r.companyTaxNumber}</p>}
          </div>
          <div className="text-right">
            <p className="text-lg font-semibold uppercase tracking-wide">Booking receipt</p>
            <p>
              Receipt no. <span className="font-mono">{r.receiptNumber}</span>
            </p>
            <p className="text-slate-600">{formatNepalDateTime(p.bookedAtUtc)}</p>
          </div>
        </header>

        <section className="rounded-lg border border-slate-300 p-4 text-center">
          <p className="text-xs uppercase tracking-wide text-slate-500">Tracking number</p>
          <p className="font-mono text-2xl font-bold tracking-widest">{p.trackingNumber}</p>
          <p className="mt-1 text-xs text-slate-600">
            Status: {statusLabel(p.status)}
            {p.status === 'Cancelled' && ' — this booking has been cancelled'}
          </p>
        </section>

        <div className="grid gap-6 sm:grid-cols-2">
          <PartyBlock title="Sender" party={p.sender} />
          <PartyBlock title="Receiver" party={p.receiver} />
        </div>

        <section>
          <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Parcel</h2>
          <dl className="mt-1 grid gap-x-6 gap-y-1 sm:grid-cols-2">
            <div>
              <dt className="inline text-slate-500">Contents: </dt>
              <dd className="inline">{p.description}</dd>
            </div>
            <div>
              <dt className="inline text-slate-500">Pieces: </dt>
              <dd className="inline">{p.quantity}</dd>
            </div>
            <div>
              <dt className="inline text-slate-500">Weight: </dt>
              <dd className="inline">
                {formatWeight(p.weightKg)} (charged {formatWeight(p.price.chargeableWeightKg)})
              </dd>
            </div>
            {dims && (
              <div>
                <dt className="inline text-slate-500">Size: </dt>
                <dd className="inline">{dims}</dd>
              </div>
            )}
            <div>
              <dt className="inline text-slate-500">Delivery type: </dt>
              <dd className="inline">{serviceLabel(p.serviceType)}</dd>
            </div>
            <div>
              <dt className="inline text-slate-500">Handed over at: </dt>
              <dd className="inline">{p.originBranch.name}</dd>
            </div>
            {p.declaredValue > 0 && (
              <div>
                <dt className="inline text-slate-500">Declared value: </dt>
                <dd className="inline">{formatNpr(p.declaredValue)}</dd>
              </div>
            )}
            {p.estimatedDeliveryAtUtc && (
              <div>
                <dt className="inline text-slate-500">Estimated delivery: </dt>
                <dd className="inline">{formatNepalDateTime(p.estimatedDeliveryAtUtc)}</dd>
              </div>
            )}
          </dl>
        </section>

        <section>
          <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Charges (NPR)</h2>
          <table className="mt-1 w-full">
            <tbody>
              <Line label="Base charge">{formatNpr(p.price.baseCharge)}</Line>
              <Line label="Extra weight charge">{formatNpr(p.price.weightCharge)}</Line>
              {p.price.codFee > 0 && <Line label="Cash-on-delivery fee">{formatNpr(p.price.codFee)}</Line>}
              <Line label={`VAT (${Number(p.price.vatPercent)}%)`}>{formatNpr(p.price.vatAmount)}</Line>
              <Line label="Delivery charge" strong>
                {formatNpr(p.price.totalCharge)}
              </Line>
            </tbody>
          </table>
          <dl className="mt-3 grid gap-x-6 gap-y-1 sm:grid-cols-2">
            <div>
              <dt className="inline text-slate-500">Payment method: </dt>
              <dd className="inline">{paymentLabel(p.paymentMethod)}</dd>
            </div>
            <div>
              <dt className="inline text-slate-500">Payment status: </dt>
              <dd className="inline">{p.paymentStatus ?? '—'}</dd>
            </div>
            {p.paymentReference && (
              <div>
                <dt className="inline text-slate-500">Reference: </dt>
                <dd className="inline">{p.paymentReference}</dd>
              </div>
            )}
          </dl>
          {p.codAmount > 0 && (
            <p className="mt-3 rounded-lg border border-slate-300 p-3 font-semibold">
              Amount to collect from receiver on delivery: {formatNpr(p.codAmount)}
            </p>
          )}
        </section>

        <footer className="border-t border-slate-300 pt-4 text-xs text-slate-500">
          Keep this receipt and the tracking number. Track your parcel at any time on our website.
        </footer>
      </article>
    </div>
  );
}
