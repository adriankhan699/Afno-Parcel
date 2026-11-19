import { Link, useParams } from 'react-router';
import { Alert, PrimaryButton } from '../components/form';
import { adminApi } from '../lib/adminApi';
import { formatNepalDateTime, formatNpr } from '../lib/format';
import { safeImageSrc } from '../lib/label';
import { formatWeight, serviceLabel } from '../lib/labels';
import { useApi } from '../lib/useApi';
import type { ParcelLabelResponse } from '../types/api';

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <dt className="text-[10px] font-semibold uppercase tracking-wide text-slate-500">{label}</dt>
      <dd className="text-sm font-medium text-slate-900">{children}</dd>
    </div>
  );
}

/** A shipping label for one parcel, sized to print on a single sheet or label stock. */
export default function ParcelLabelPage() {
  const { id = '' } = useParams();
  const result = useApi<ParcelLabelResponse>(id ? adminApi.parcelLabel(encodeURIComponent(id)) : null);

  if (result.status === 'loading' || result.status === 'idle') return <p className="text-sm text-slate-500">Loading label…</p>;
  if (result.status === 'error') {
    return (
      <div className="space-y-4">
        <Alert>{result.message}</Alert>
        <Link to={`/parcels/${id}`} className="text-sm font-medium text-brand-700 underline">
          Back to parcel
        </Link>
      </div>
    );
  }

  const label = result.data;
  const barcode = safeImageSrc(label.barcodeDataUri);
  const qr = safeImageSrc(label.qrDataUri);

  return (
    <div>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3 print:hidden">
        <Link to={`/parcels/${id}`} className="text-sm text-brand-700 underline">
          ← Back to parcel
        </Link>
        <PrimaryButton type="button" onClick={() => window.print()}>
          Print label
        </PrimaryButton>
      </div>

      <article
        aria-label={`Label for ${label.trackingNumber}`}
        className="mx-auto max-w-md space-y-4 rounded-xl border-2 border-slate-800 bg-white p-5 text-slate-900 print:max-w-none print:rounded-none print:shadow-none"
      >
        <header className="flex items-start justify-between gap-3 border-b border-slate-300 pb-3">
          <div>
            <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">Afno Parcel</p>
            <p className="text-lg font-bold">{serviceLabel(label.serviceType)}</p>
          </div>
          <p className="text-right text-xs text-slate-600">Booked {formatNepalDateTime(label.bookedAtUtc)}</p>
        </header>

        <section aria-label="Tracking number" className="text-center">
          <p className="font-mono text-2xl font-bold tracking-widest">{label.trackingNumber}</p>
          {barcode ? (
            <img src={barcode} alt={`Barcode for ${label.trackingNumber}`} className="mx-auto mt-2 h-20 w-full max-w-xs object-contain" />
          ) : (
            <p className="mt-2 text-xs text-red-700">The barcode could not be shown. Use the number above.</p>
          )}
        </section>

        <div className="flex items-center gap-4">
          {qr && <img src={qr} alt="QR code that opens the tracking page" className="h-28 w-28 shrink-0 object-contain" />}
          <dl className="grid flex-1 gap-2">
            <Field label="Deliver to">{label.receiverName}</Field>
            <Field label="Area">{label.receiverArea}</Field>
          </dl>
        </div>

        <dl className="grid grid-cols-2 gap-x-4 gap-y-2 border-t border-slate-300 pt-3">
          <Field label="From">{label.originBranch}</Field>
          <Field label="To">{label.destinationArea}</Field>
          <Field label="Pieces">{label.quantity}</Field>
          <Field label="Weight">{formatWeight(label.weightKg)}</Field>
        </dl>

        {label.codAmount > 0 && (
          <p className="rounded-lg border-2 border-slate-800 p-3 text-center text-base font-bold">COLLECT {formatNpr(label.codAmount)} ON DELIVERY</p>
        )}
      </article>
    </div>
  );
}
