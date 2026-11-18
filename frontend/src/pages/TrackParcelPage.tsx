import { useState, type FormEvent } from 'react';
import { useNavigate, useParams } from 'react-router';
import { isValidTrackingNumber, normalizeTrackingNumber } from '../lib/tracking';

export default function TrackParcelPage() {
  const { trackingNumber } = useParams();
  const navigate = useNavigate();
  const [input, setInput] = useState(trackingNumber ?? '');
  const [error, setError] = useState<string | null>(null);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isValidTrackingNumber(input)) {
      setError('Enter a valid tracking number (8–20 letters and digits).');
      return;
    }
    setError(null);
    navigate(`/track/${normalizeTrackingNumber(input)}`);
  }

  return (
    <div className="mx-auto max-w-xl">
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
          placeholder="e.g. NP12345678"
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

      {trackingNumber && !error && (
        <div className="mt-8 rounded-xl border border-dashed border-slate-300 bg-white p-6 text-sm text-slate-600">
          <p className="font-medium text-slate-900">Tracking {normalizeTrackingNumber(trackingNumber)}</p>
          <p className="mt-1">
            Parcel tracking goes live with the Parcels module in the next phase. This page is wired and ready.
          </p>
        </div>
      )}
    </div>
  );
}
