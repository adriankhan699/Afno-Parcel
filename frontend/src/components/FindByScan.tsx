import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api } from '../lib/apiClient';
import { extractTrackingNumber } from '../lib/scan';
import type { ParcelResponse } from '../types/api';
import { PrimaryButton } from './form';

/**
 * "Find by scan": type or scan a tracking number or a QR code's link and go straight to that parcel.
 * A barcode scanner types the code and presses Enter, which submits this form.
 */
export default function FindByScan({ className = '' }: { className?: string }) {
  const navigate = useNavigate();
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const text = code.trim();
    if (!text) return;
    if (extractTrackingNumber(text) === null) {
      setError('That does not look like a tracking number or a tracking QR code.');
      return;
    }

    setBusy(true);
    setError(null);
    try {
      // The server accepts the raw scanned text and finds the tracking number in it.
      const parcel = await api.get<ParcelResponse>(`/api/v1/parcels/lookup?code=${encodeURIComponent(text)}`);
      setCode('');
      navigate(`/parcels/${parcel.id}`);
    } catch (err) {
      setError(err instanceof ApiError && err.status === 404 ? 'No parcel matches this code.' : err instanceof ApiError ? err.message : 'Could not reach the server.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={(e) => void handleSubmit(e)} role="search" aria-label="Find a parcel by scan" className={className}>
      <div className="flex gap-2">
        <label htmlFor="findByScan" className="sr-only">
          Scan or type a tracking number or QR code
        </label>
        <input
          id="findByScan"
          value={code}
          onChange={(e) => {
            setCode(e.target.value);
            setError(null);
          }}
          autoComplete="off"
          placeholder="Find by scan: tracking no. or QR link"
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? 'findByScan-error' : undefined}
          className="min-w-0 flex-1 rounded-lg border border-slate-300 bg-white px-3 py-2 font-mono text-sm outline-none focus:border-brand-600 focus:ring-2 focus:ring-brand-100"
        />
        <PrimaryButton type="submit" disabled={busy}>
          {busy ? 'Finding…' : 'Find'}
        </PrimaryButton>
      </div>
      {error && (
        <p id="findByScan-error" role="alert" className="mt-1 text-xs text-red-700">
          {error}
        </p>
      )}
    </form>
  );
}
