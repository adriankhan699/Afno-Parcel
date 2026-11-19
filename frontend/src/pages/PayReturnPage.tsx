import { useEffect, useReducer, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import { Alert, PrimaryButton } from '../components/form';
import { ApiError, api } from '../lib/apiClient';
import { formatNepalDateTime, formatNpr } from '../lib/format';
import { AUTO_CHECK_DELAY_MS, initialReturnState, nextReturnState, providerLabel } from '../lib/payment';
import { paymentApi } from '../lib/paymentApi';
import type { PaymentCallbackRequest, PaymentStatusResponse } from '../types/api';

/**
 * Where the customer lands after the provider. NOTHING in the address (a "status=success", an amount, a transaction id) is
 * believed: the only thing read from it is our own reference. The server is asked to verify the payment with the provider,
 * and the page shows exactly what the server answers.
 */
export default function PayReturnPage() {
  const { provider = '' } = useParams();
  const [params] = useSearchParams();
  const reference = (params.get('reference') ?? '').trim();

  const [state, dispatch] = useReducer(nextReturnState, initialReturnState);
  // Each round is one question to the server: the first, an automatic repeat, or a press of "Check again".
  const [round, setRound] = useState(0);

  useEffect(() => {
    if (!reference) return;
    const controller = new AbortController();
    dispatch({ type: 'check' });
    const body: PaymentCallbackRequest = { reference };
    api
      .post<PaymentStatusResponse>(paymentApi.callback(provider), body, controller.signal)
      .then((response) => {
        if (!controller.signal.aborted) dispatch({ type: 'answer', expected: { reference, provider }, response });
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) dispatch({ type: 'failure', httpStatus: err instanceof ApiError ? err.status : undefined });
      });
    return () => controller.abort();
  }, [reference, provider, round]);

  useEffect(() => {
    if (!state.autoRetry) return;
    const timer = window.setTimeout(() => setRound((r) => r + 1), AUTO_CHECK_DELAY_MS);
    return () => window.clearTimeout(timer);
  }, [state.autoRetry, state.attempts]);

  if (!reference) {
    return (
      <div className="mx-auto max-w-lg space-y-4">
        <h1 className="text-2xl font-bold tracking-tight">Payment</h1>
        <Alert>This page needs a payment reference. Start the payment again from your parcel.</Alert>
        <Link to="/parcels" className="text-sm font-medium text-brand-700 underline">
          Back to my parcels
        </Link>
      </div>
    );
  }

  const result = state.result;
  return (
    <div className="mx-auto max-w-lg space-y-5">
      <h1 className="text-2xl font-bold tracking-tight">Your {providerLabel(provider)} payment</h1>

      <div aria-live="polite" className="space-y-4">
        {state.phase === 'verifying' && (
          <div role="status" className="rounded-xl border border-slate-200 bg-white p-6 text-center shadow-sm">
            <p className="text-lg font-semibold">Verifying your payment…</p>
            <p className="mt-1 text-sm text-slate-600">We are asking the payment provider, not the web address, whether it went through.</p>
          </div>
        )}

        {state.phase === 'paid' && result && (
          <>
            <Alert tone="success">
              <p className="text-base font-semibold">Payment received</p>
              <p className="mt-1">
                {formatNpr(result.amount)} for parcel <span className="font-mono">{result.trackingNumber}</span>
                {result.completedAtUtc && <> on {formatNepalDateTime(result.completedAtUtc)}</>}.
              </p>
            </Alert>
            <Actions trackingNumber={result.trackingNumber} />
          </>
        )}

        {state.phase === 'failed' && (
          <>
            <Alert>
              <p className="text-base font-semibold">Payment not completed</p>
              <p className="mt-1">{state.message}</p>
            </Alert>
            <Actions trackingNumber={result?.trackingNumber} />
          </>
        )}

        {(state.phase === 'pending' || state.phase === 'unreachable') && (
          <>
            <Alert tone="info">
              <p className="text-base font-semibold">{state.phase === 'pending' ? 'Still waiting for confirmation' : 'Could not check yet'}</p>
              <p className="mt-1">{state.message}</p>
              {state.autoRetry && <p className="mt-1">We will check again automatically.</p>}
            </Alert>
            <PrimaryButton type="button" disabled={state.autoRetry} onClick={() => setRound((r) => r + 1)}>
              Check again
            </PrimaryButton>
            <Actions trackingNumber={result?.trackingNumber} />
          </>
        )}

        {state.phase === 'invalid' && (
          <>
            <Alert>{state.message}</Alert>
            <Actions />
          </>
        )}
      </div>

      <p className="text-xs text-slate-500">
        Reference <span className="font-mono">{reference}</span>. If you were charged but the payment does not show as paid, keep this reference and contact us.
      </p>
    </div>
  );
}

function Actions({ trackingNumber }: { trackingNumber?: string }) {
  return (
    <div className="flex flex-wrap gap-3 text-sm font-medium">
      {trackingNumber && (
        <Link to={`/track/${trackingNumber}`} className="rounded-lg bg-brand-600 px-4 py-2.5 font-semibold text-white hover:bg-brand-700">
          Track this parcel
        </Link>
      )}
      <Link to="/parcels" className="rounded-lg border border-slate-300 bg-white px-4 py-2.5 font-semibold text-slate-700 hover:bg-slate-50">
        Back to my parcels
      </Link>
    </div>
  );
}
