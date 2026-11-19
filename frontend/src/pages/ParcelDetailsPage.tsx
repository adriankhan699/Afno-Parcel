import { useRef, useState, type FormEvent, type ReactNode } from 'react';
import { Link, useLocation, useParams } from 'react-router';
import { useAuth } from '../auth/useAuth';
import { Alert, PrimaryButton, SecondaryButton, TextAreaField, TextField } from '../components/form';
import PriceBreakdown from '../components/PriceBreakdown';
import StatusUpdatePanel from '../components/StatusUpdatePanel';
import Timeline from '../components/Timeline';
import { ApiError, api } from '../lib/apiClient';
import { canUpdateStatus } from '../lib/auth';
import { formatAddress, formatNepalDateTime, formatNpr } from '../lib/format';
import { formatWeight, paymentLabel, serviceLabel, statusLabel, statusTone } from '../lib/labels';
import { problemSummary } from '../lib/problems';
import { timelineFromHistory } from '../lib/status';
import { useApi } from '../lib/useApi';
import type { BookingPartyDto, ParcelResponse } from '../types/api';

function Card({ title, children, className = '' }: { title: string; children: ReactNode; className?: string }) {
  return (
    <section className={`rounded-xl border border-slate-200 bg-white p-5 shadow-sm ${className}`}>
      <h2 className="text-base font-semibold text-slate-900">{title}</h2>
      <div className="mt-3 text-sm">{children}</div>
    </section>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex justify-between gap-4 py-1">
      <dt className="text-slate-500">{label}</dt>
      <dd className="text-right text-slate-900">{children}</dd>
    </div>
  );
}

function Party({ party }: { party: BookingPartyDto }) {
  return (
    <div>
      <p className="font-medium text-slate-900">{party.fullName}</p>
      <p className="text-slate-600">☎ {party.phoneNumber}</p>
      <p className="mt-1 text-slate-600">{formatAddress(party.address)}</p>
      {party.addressInstructions && <p className="mt-1 text-xs text-slate-500">Instructions: {party.addressInstructions}</p>}
    </div>
  );
}

export default function ParcelDetailsPage() {
  const { id = '' } = useParams();
  const location = useLocation();
  const auth = useAuth();
  const fetched = useApi<ParcelResponse>(id ? `/api/v1/parcels/${encodeURIComponent(id)}` : null);

  // After an action the server returns the updated parcel; show that instead of the first copy.
  const [updated, setUpdated] = useState<ParcelResponse | null>(null);
  const parcel = updated && updated.id === id ? updated : fetched.status === 'loaded' ? fetched.data : null;

  const justBooked = (location.state as { justBooked?: boolean } | null)?.justBooked === true;

  const dialogRef = useRef<HTMLDialogElement>(null);
  const [reason, setReason] = useState('');
  const [reasonError, setReasonError] = useState<string | undefined>();
  const [actionError, setActionError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [paymentReference, setPaymentReference] = useState('');

  async function run(action: () => Promise<ParcelResponse>) {
    setBusy(true);
    setActionError(null);
    try {
      setUpdated(await action());
      return true;
    } catch (err) {
      setActionError(err instanceof ApiError ? problemSummary(err.problem) : 'Could not reach the server. Please try again shortly.');
      return false;
    } finally {
      setBusy(false);
    }
  }

  /** Reloads the parcel after a status change so the history, status and allowed next steps are up to date. */
  async function reload() {
    setUpdated(await api.get<ParcelResponse>(`/api/v1/parcels/${encodeURIComponent(id)}`));
  }

  async function handleCancel(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const trimmed = reason.trim();
    if (trimmed.length < 5 || trimmed.length > 300) {
      setReasonError('Give a reason of 5 to 300 characters.');
      return;
    }
    setReasonError(undefined);
    const ok = await run(() => api.post<ParcelResponse>(`/api/v1/parcels/${id}/cancel`, { reason: trimmed }));
    if (ok) {
      setReason('');
    }
    dialogRef.current?.close();
  }

  async function handleConfirmPayment(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const reference = paymentReference.trim();
    const ok = await run(() => api.post<ParcelResponse>(`/api/v1/parcels/${id}/confirm-payment`, { reference: reference || null }));
    if (ok) setPaymentReference('');
  }

  if (fetched.status === 'loading' && !parcel) return <p className="text-sm text-slate-500">Loading parcel…</p>;
  if (fetched.status === 'error' && !parcel) {
    return (
      <div className="space-y-4">
        <Alert>{fetched.message}</Alert>
        <Link to="/parcels" className="text-sm font-medium text-brand-700 underline">
          Back to parcels
        </Link>
      </div>
    );
  }
  if (!parcel) return null;

  const canConfirmPayment =
    auth.isCounterStaff && parcel.paymentStatus === 'Pending' && parcel.paymentMethod !== 'CashOnDelivery' && parcel.status !== 'Cancelled';
  const trackingHref = `/track/${parcel.trackingNumber}`;
  const dims =
    parcel.lengthCm && parcel.widthCm && parcel.heightCm ? `${parcel.lengthCm} × ${parcel.widthCm} × ${parcel.heightCm} cm` : null;

  return (
    <div className="space-y-6">
      <nav aria-label="Breadcrumb" className="text-sm">
        <Link to="/parcels" className="text-brand-700 underline">
          ← All parcels
        </Link>
      </nav>

      {justBooked && (
        <Alert tone="success">
          <strong>Booking confirmed.</strong> Your tracking number is{' '}
          <span className="font-mono font-semibold">{parcel.trackingNumber}</span>. Keep it to track your parcel.
        </Alert>
      )}
      {actionError && <Alert>{actionError}</Alert>}

      <header className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-sm text-slate-500">Tracking number</p>
          <h1 className="font-mono text-2xl font-bold tracking-tight">{parcel.trackingNumber}</h1>
          <p className="mt-2 flex flex-wrap items-center gap-2 text-sm text-slate-600">
            <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${statusTone(parcel.status)}`}>{statusLabel(parcel.status)}</span>
            <span>Booked {formatNepalDateTime(parcel.bookedAtUtc)}</span>
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Link
            to={`/parcels/${parcel.id}/receipt`}
            className="rounded-lg bg-brand-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-brand-700"
          >
            View receipt
          </Link>
          <Link
            to={trackingHref}
            className="rounded-lg border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-50"
          >
            Public tracking page
          </Link>
          {parcel.canCancel && (
            <SecondaryButton type="button" disabled={busy} onClick={() => dialogRef.current?.showModal()}>
              Cancel booking
            </SecondaryButton>
          )}
        </div>
      </header>

      <div className="grid gap-6 lg:grid-cols-2">
        <Card title="Sender">
          <Party party={parcel.sender} />
        </Card>
        <Card title="Receiver">
          <Party party={parcel.receiver} />
        </Card>

        <Card title="Parcel">
          <dl>
            <Row label="Contents">{parcel.description}</Row>
            <Row label="Pieces">{parcel.quantity}</Row>
            <Row label="Weight">{formatWeight(parcel.weightKg)}</Row>
            {dims && <Row label="Size">{dims}</Row>}
            {parcel.declaredValue > 0 && <Row label="Declared value">{formatNpr(parcel.declaredValue)}</Row>}
            <Row label="Delivery type">{serviceLabel(parcel.serviceType)}</Row>
            <Row label="Booked via">{parcel.channel}</Row>
            <Row label="Hand-over branch">{parcel.originBranch.name}</Row>
            {parcel.destinationBranch && <Row label="Delivering branch">{parcel.destinationBranch.name}</Row>}
            {parcel.estimatedDeliveryAtUtc && <Row label="Estimated delivery">{formatNepalDateTime(parcel.estimatedDeliveryAtUtc)}</Row>}
            {parcel.deliveredAtUtc && <Row label="Delivered">{formatNepalDateTime(parcel.deliveredAtUtc)}</Row>}
            {parcel.notes && <Row label="Notes">{parcel.notes}</Row>}
          </dl>
        </Card>

        <Card title="Charges & payment">
          <PriceBreakdown breakdown={parcel.price} />
          <dl className="mt-3 border-t border-slate-100 pt-3">
            <Row label="Payment method">{paymentLabel(parcel.paymentMethod)}</Row>
            <Row label="Payment status">{parcel.paymentStatus ?? '—'}</Row>
            {parcel.paymentReference && <Row label="Reference">{parcel.paymentReference}</Row>}
            <Row label="To collect on delivery">{parcel.codAmount > 0 ? formatNpr(parcel.codAmount) : 'Nothing'}</Row>
          </dl>
          {canConfirmPayment && (
            <form onSubmit={handleConfirmPayment} className="mt-4 flex flex-wrap items-end gap-2 border-t border-slate-100 pt-4">
              <TextField
                id="paymentReference"
                label="Payment reference (optional)"
                maxLength={100}
                className="min-w-48 flex-1"
                value={paymentReference}
                onChange={setPaymentReference}
              />
              <PrimaryButton type="submit" disabled={busy}>
                Mark as paid
              </PrimaryButton>
            </form>
          )}
        </Card>
      </div>

      {auth.isAuthenticated && canUpdateStatus(auth.user?.roles) && parcel.allowedNextStatuses.length > 0 && (
        <StatusUpdatePanel parcelId={parcel.id} allowedStatuses={parcel.allowedNextStatuses} onChanged={reload} />
      )}

      <Card title="Journey">
        <Timeline items={timelineFromHistory(parcel.history)} latest="last" label="Parcel history" />
      </Card>

      <dialog
        ref={dialogRef}
        aria-labelledby="cancel-title"
        className="w-full max-w-md rounded-xl p-0 shadow-xl backdrop:bg-black/40"
      >
        <form onSubmit={handleCancel} noValidate className="space-y-4 p-6">
          <h2 id="cancel-title" className="text-lg font-semibold">
            Cancel this booking?
          </h2>
          <p className="text-sm text-slate-600">
            Parcel <span className="font-mono">{parcel.trackingNumber}</span> will be cancelled. This cannot be undone.
          </p>
          <TextAreaField
            id="cancel-reason"
            label="Reason"
            required
            maxLength={300}
            value={reason}
            onChange={setReason}
            error={reasonError}
          />
          <div className="flex justify-end gap-2">
            <SecondaryButton type="button" onClick={() => dialogRef.current?.close()}>
              Keep booking
            </SecondaryButton>
            <PrimaryButton type="submit" disabled={busy}>
              {busy ? 'Cancelling…' : 'Cancel booking'}
            </PrimaryButton>
          </div>
        </form>
      </dialog>
    </div>
  );
}
