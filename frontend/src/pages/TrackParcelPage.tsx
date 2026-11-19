import { useState, type FormEvent } from 'react';
import { useNavigate, useParams } from 'react-router';
import { Alert } from '../components/form';
import ProgressTracker from '../components/ProgressTracker';
import Timeline from '../components/Timeline';
import TrackingDetails from '../components/TrackingDetails';
import { formatNepalDateTime } from '../lib/format';
import { serviceLabel, statusLabel, statusTone } from '../lib/labels';
import { timelineFromTracking } from '../lib/status';
import { isValidTrackingNumber, normalizeTrackingNumber } from '../lib/tracking';
import { useApi } from '../lib/useApi';
import type { PublicTrackingResponse } from '../types/api';

function PublicResult({ parcel }: { parcel: PublicTrackingResponse }) {
  return (
    <div className="space-y-6">
      <section className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
        <p className="text-sm text-slate-500">Tracking number</p>
        <h2 className="font-mono text-2xl font-bold tracking-tight">{parcel.trackingNumber}</h2>

        <p className="mt-3 flex flex-wrap items-center gap-2">
          <span className={`rounded-full px-3 py-1 text-sm font-semibold ${statusTone(parcel.status)}`}>
            {parcel.statusName || statusLabel(parcel.status)}
          </span>
          <span className="text-sm text-slate-600">{parcel.statusMessage}</span>
        </p>

        <dl className="mt-4 grid gap-x-8 gap-y-2 text-sm sm:grid-cols-2 lg:grid-cols-4">
          <div>
            <dt className="text-xs text-slate-500">From</dt>
            <dd className="font-medium text-slate-900">{parcel.originArea}</dd>
          </div>
          <div>
            <dt className="text-xs text-slate-500">To</dt>
            <dd className="font-medium text-slate-900">{parcel.destinationArea}</dd>
          </div>
          <div>
            <dt className="text-xs text-slate-500">Delivery type</dt>
            <dd className="font-medium text-slate-900">{serviceLabel(parcel.serviceType)}</dd>
          </div>
          <div>
            <dt className="text-xs text-slate-500">{parcel.deliveredAtUtc ? 'Delivered' : 'Estimated delivery'}</dt>
            <dd className="font-medium text-slate-900">
              {parcel.deliveredAtUtc
                ? formatNepalDateTime(parcel.deliveredAtUtc)
                : parcel.estimatedDeliveryAtUtc
                  ? formatNepalDateTime(parcel.estimatedDeliveryAtUtc)
                  : '—'}
            </dd>
          </div>
        </dl>
      </section>

      <section aria-labelledby="progress-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
        <h2 id="progress-heading" className="mb-4 text-base font-semibold text-slate-900">
          Progress
        </h2>
        <ProgressTracker status={parcel.status} historyStatuses={parcel.events.map((e) => e.status)} />
      </section>

      <section aria-labelledby="history-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
        <h2 id="history-heading" className="mb-4 text-base font-semibold text-slate-900">
          Parcel history
        </h2>
        <Timeline items={timelineFromTracking(parcel.events)} latest="first" label="Parcel history" />
      </section>

      <p className="text-xs text-slate-500">
        Names, phone numbers and addresses are never shown here. Times are in Nepal time.
      </p>

      {/* Keyed by tracking number so the verification state never leaks from one parcel to another. */}
      <TrackingDetails key={parcel.trackingNumber} trackingNumber={parcel.trackingNumber} />
    </div>
  );
}

export default function TrackParcelPage() {
  const { trackingNumber } = useParams();
  const navigate = useNavigate();
  const [input, setInput] = useState(trackingNumber ?? '');
  const [error, setError] = useState<string | null>(null);

  const normalized = trackingNumber ? normalizeTrackingNumber(trackingNumber) : null;
  const formatOk = normalized !== null && isValidTrackingNumber(normalized);
  const result = useApi<PublicTrackingResponse>(formatOk ? `/api/v1/tracking/${encodeURIComponent(normalized)}` : null);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isValidTrackingNumber(input)) {
      setError('Enter a valid tracking number: it starts with AFN and has 14 letters and digits.');
      return;
    }
    setError(null);
    navigate(`/track/${normalizeTrackingNumber(input)}`);
  }

  return (
    <div className="mx-auto max-w-3xl">
      <h1 className="text-2xl font-bold tracking-tight">Track your parcel</h1>
      <p className="mt-2 text-sm text-slate-600">Enter the tracking number printed on your receipt.</p>

      <form onSubmit={handleSubmit} noValidate className="mt-6 flex flex-col gap-3 sm:flex-row">
        <label htmlFor="tracking" className="sr-only">
          Tracking number
        </label>
        <input
          id="tracking"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="e.g. AFNK7M2Q9XT4B3"
          autoComplete="off"
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? 'tracking-error' : undefined}
          className="flex-1 rounded-lg border border-slate-300 bg-white px-4 py-2.5 uppercase tracking-wide outline-none focus:border-brand-600 focus:ring-2 focus:ring-brand-100"
        />
        <button
          type="submit"
          className="rounded-lg bg-brand-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-brand-700"
        >
          Track
        </button>
      </form>
      {error && (
        <p id="tracking-error" role="alert" className="mt-2 text-sm text-brand-700">
          {error}
        </p>
      )}

      <div className="mt-8">
        {trackingNumber && !formatOk && !error && (
          <Alert>That does not look like one of our tracking numbers. Check it against your receipt and try again.</Alert>
        )}
        {formatOk && result.status === 'loading' && <p className="text-sm text-slate-500">Looking up your parcel…</p>}
        {formatOk && result.status === 'error' && (
          <Alert tone={result.httpStatus === 404 ? 'info' : 'error'}>
            {result.httpStatus === 404
              ? 'We could not find a parcel with that tracking number. Check it against your receipt and try again.'
              : result.httpStatus === 429
                ? 'Too many lookups from your connection. Please wait a minute and try again.'
                : result.message}
          </Alert>
        )}
        {formatOk && result.status === 'loaded' && <PublicResult parcel={result.data} />}
      </div>
    </div>
  );
}
