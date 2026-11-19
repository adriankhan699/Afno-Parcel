import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { useAuth } from '../auth/useAuth';
import { ApiError, api, apiRequest } from '../lib/apiClient';
import { formatNepalDateTime, formatNpr } from '../lib/format';
import { paymentLabel, serviceLabel } from '../lib/labels';
import { timelineFromTracking } from '../lib/status';
import { clearTrackingAccess, isValidOtp, normalizeOtp, readTrackingAccess, storeTrackingAccess } from '../lib/trackingAccess';
import type {
  DetailedTrackingResponse,
  TrackingAccessResponse,
  TrackingOtpRequestedResponse,
  VerifyTrackingOtpRequest,
} from '../types/api';
import { Alert, PrimaryButton, SecondaryButton, TextField } from './form';
import Timeline from './Timeline';

type DetailsState =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'loaded'; data: DetailedTrackingResponse }
  | { kind: 'error'; message: string };

const NETWORK_ERROR = 'Could not reach the server. Please try again shortly.';

function detailsPath(trackingNumber: string): string {
  return `/api/v1/tracking/${encodeURIComponent(trackingNumber)}/details`;
}

function loadWithToken(trackingNumber: string, token: string): Promise<DetailedTrackingResponse> {
  // The receiver's token replaces the sign-in: do not send the bearer token alongside it.
  return apiRequest<DetailedTrackingResponse>(detailsPath(trackingNumber), {
    method: 'GET',
    headers: { 'X-Tracking-Access': token },
    skipAuth: true,
  });
}

/** What to tell a visitor for each way the details request can fail. Never echoes server internals. */
function describeError(err: unknown, context: 'token' | 'signedIn'): string {
  if (!(err instanceof ApiError)) return NETWORK_ERROR;
  switch (err.status) {
    case 401:
      return context === 'token'
        ? 'Your verification has expired. Please verify with a new code.'
        : 'Please verify as the receiver, or sign in to see the full details.';
    case 404:
      return context === 'signedIn'
        ? 'This parcel is not linked to your account. If you are the receiver, verify with a one-time code instead.'
        : 'We could not find details for this tracking number.';
    case 429:
      return 'Too many attempts. Please wait a few minutes and try again.';
    default:
      return NETWORK_ERROR;
  }
}

function DetailsCard({ data }: { data: DetailedTrackingResponse }) {
  return (
    <div className="space-y-4">
      <dl className="grid gap-x-8 gap-y-2 text-sm sm:grid-cols-2">
        <Row label="Receiver">{data.receiverName}</Row>
        <Row label="Delivering to">{data.receiverArea}</Row>
        <Row label="Delivery type">{serviceLabel(data.serviceType)}</Row>
        <Row label="Payment">{paymentLabel(data.paymentMethod)}</Row>
        {data.codAmount > 0 && <Row label="Amount to pay on delivery">{formatNpr(data.codAmount)}</Row>}
        <Row label="Booked">{formatNepalDateTime(data.bookedAtUtc)}</Row>
        {data.estimatedDeliveryAtUtc && !data.deliveredAtUtc && (
          <Row label="Estimated delivery">{formatNepalDateTime(data.estimatedDeliveryAtUtc)}</Row>
        )}
        {data.deliveredAtUtc && <Row label="Delivered">{formatNepalDateTime(data.deliveredAtUtc)}</Row>}
      </dl>
      <div>
        <h3 className="mb-2 text-sm font-semibold text-slate-900">Full history</h3>
        <Timeline items={timelineFromTracking(data.events)} latest="first" label="Detailed parcel history" />
      </div>
    </div>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className="font-medium text-slate-900">{children}</dd>
    </div>
  );
}

/**
 * The part of the tracking page that goes beyond what is public. Two ways in: sign in as the sender or staff, or prove
 * you are the receiver with a one-time code sent to the receiver's phone.
 */
export default function TrackingDetails({ trackingNumber }: { trackingNumber: string }) {
  const auth = useAuth();

  // A token from an earlier verification in this tab lets the receiver come straight back to the details.
  const [storedToken] = useState(() => readTrackingAccess(trackingNumber));
  const [state, setState] = useState<DetailsState>(() => (storedToken ? { kind: 'loading' } : { kind: 'idle' }));

  const [otpSent, setOtpSent] = useState<TrackingOtpRequestedResponse | null>(null);
  const [code, setCode] = useState('');
  const [codeError, setCodeError] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!storedToken) return;
    let cancelled = false;
    loadWithToken(trackingNumber, storedToken)
      .then((data) => {
        if (!cancelled) setState({ kind: 'loaded', data });
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) clearTrackingAccess(trackingNumber);
        setState({ kind: 'error', message: describeError(err, 'token') });
      });
    return () => {
      cancelled = true;
    };
  }, [trackingNumber, storedToken]);

  async function handleSignedInLoad() {
    setState({ kind: 'loading' });
    try {
      setState({ kind: 'loaded', data: await api.get<DetailedTrackingResponse>(detailsPath(trackingNumber)) });
    } catch (err) {
      setState({ kind: 'error', message: describeError(err, 'signedIn') });
    }
  }

  async function handleRequestCode() {
    setBusy(true);
    setCodeError(undefined);
    try {
      // The answer is the same whether or not the number exists, so we show it as it is.
      setOtpSent(await apiRequest<TrackingOtpRequestedResponse>(`/api/v1/tracking/${encodeURIComponent(trackingNumber)}/otp`, {
        method: 'POST',
        skipAuth: true,
      }));
      setCode('');
    } catch (err) {
      setCodeError(describeError(err, 'token'));
    } finally {
      setBusy(false);
    }
  }

  async function handleVerify(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isValidOtp(code)) {
      setCodeError('Enter the 6-digit code.');
      return;
    }
    setBusy(true);
    setCodeError(undefined);
    try {
      const access = await api.post<TrackingAccessResponse>(
        `/api/v1/tracking/${encodeURIComponent(trackingNumber)}/verify`,
        { code: normalizeOtp(code) } satisfies VerifyTrackingOtpRequest,
        undefined,
        { skipAuth: true },
      );
      storeTrackingAccess(trackingNumber, access.accessToken, access.expiresAtUtc);
      setCode('');
      setOtpSent(null);
      setState({ kind: 'loading' });
      try {
        setState({ kind: 'loaded', data: await loadWithToken(trackingNumber, access.accessToken) });
      } catch (err) {
        setState({ kind: 'error', message: describeError(err, 'token') });
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 400) {
        setCodeError(err.problem.errors?.Code?.[0] ?? 'That code is not correct or has expired.');
      } else {
        setCodeError(describeError(err, 'token'));
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="details-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
      <h2 id="details-heading" className="text-base font-semibold text-slate-900">
        Full details
      </h2>

      {state.kind === 'loaded' && (
        <div className="mt-4">
          <DetailsCard data={state.data} />
        </div>
      )}

      {state.kind === 'loading' && <p className="mt-3 text-sm text-slate-500">Loading details…</p>}

      {state.kind !== 'loaded' && state.kind !== 'loading' && (
        <div className="mt-3 space-y-5">
          <p className="text-sm text-slate-600">
            More detail (the receiver, the delivery area, any amount to pay and the full history) is only shown to the
            receiver or to the person who sent the parcel.
          </p>

          {state.kind === 'error' && <Alert>{state.message}</Alert>}

          {auth.isAuthenticated && (
            <div>
              <PrimaryButton type="button" onClick={() => void handleSignedInLoad()}>
                Show full details (signed in)
              </PrimaryButton>
              <p className="mt-1 text-xs text-slate-500">Works for parcels you sent, and for branch staff.</p>
            </div>
          )}

          <div className={auth.isAuthenticated ? 'border-t border-slate-100 pt-5' : ''}>
            <h3 className="text-sm font-semibold text-slate-900">Are you the receiver?</h3>
            <p className="mt-1 text-sm text-slate-600">
              We will send a 6-digit code by SMS to the phone number the sender gave for the receiver.
            </p>

            {!otpSent ? (
              <div className="mt-3">
                <SecondaryButton type="button" disabled={busy} onClick={() => void handleRequestCode()}>
                  {busy ? 'Sending…' : 'Send me a code'}
                </SecondaryButton>
                {codeError && (
                  <p role="alert" className="mt-2 text-sm text-red-700">
                    {codeError}
                  </p>
                )}
              </div>
            ) : (
              <form onSubmit={(e) => void handleVerify(e)} noValidate className="mt-3 space-y-3">
                <Alert tone="info">
                  {otpSent.message} The code works for {otpSent.validForMinutes} minutes.
                </Alert>
                <TextField
                  id="otp"
                  label="6-digit code"
                  value={code}
                  onChange={(value) => setCode(normalizeOtp(value))}
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  maxLength={6}
                  error={codeError}
                  className="max-w-48"
                />
                <div className="flex flex-wrap gap-2">
                  <PrimaryButton type="submit" disabled={busy}>
                    {busy ? 'Checking…' : 'Verify'}
                  </PrimaryButton>
                  <SecondaryButton type="button" disabled={busy} onClick={() => void handleRequestCode()}>
                    Send a new code
                  </SecondaryButton>
                </div>
              </form>
            )}
          </div>
        </div>
      )}
    </section>
  );
}
