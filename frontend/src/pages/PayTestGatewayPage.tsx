import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { Alert, PrimaryButton, SecondaryButton } from '../components/form';
import { api } from '../lib/apiClient';
import { formatNpr } from '../lib/format';
import { providerLabel } from '../lib/payment';
import { paymentApi } from '../lib/paymentApi';
import { useApi } from '../lib/useApi';
import { usePaymentAction } from '../lib/usePaymentAction';
import type { TestGatewayCompleteRequest, TestGatewayOutcome, TestGatewaySessionResponse } from '../types/api';

/**
 * The SIMULATED provider page. Real eSewa and Khalti are not connected yet: this page only lets a tester choose how the
 * payment should end, so the whole flow can be tried. The server holds the simulated provider's state and, when the
 * customer comes back, checks it itself: nothing done on this page counts as payment until that check.
 */
export default function PayTestGatewayPage() {
  const { provider = '', reference = '' } = useParams();
  const navigate = useNavigate();
  const action = usePaymentAction();
  const session = useApi<TestGatewaySessionResponse>(reference ? paymentApi.testGateway(reference) : null);
  const [chosen, setChosen] = useState<TestGatewayOutcome | null>(null);

  const returnPath = `/pay/return/${encodeURIComponent(provider)}?reference=${encodeURIComponent(reference)}`;

  async function choose(outcome: TestGatewayOutcome) {
    setChosen(outcome);
    const body: TestGatewayCompleteRequest = { outcome };
    const done = await action.run(() => api.post<TestGatewaySessionResponse>(paymentApi.testGatewayComplete(reference), body));
    if (done.ok) navigate(returnPath);
    else setChosen(null);
  }

  const banner = (
    <div role="note" className="rounded-xl border-4 border-dashed border-amber-500 bg-amber-100 p-5 text-center text-amber-950">
      <p className="text-2xl font-extrabold uppercase tracking-wide">TEST MODE – this is not a real payment</p>
      <p className="mt-1 text-sm">No money moves. Real eSewa and Khalti are not connected yet; this page only simulates a payment provider.</p>
    </div>
  );

  if (session.status === 'loading' || session.status === 'idle') {
    return (
      <div className="mx-auto max-w-lg space-y-4">
        {banner}
        <p className="text-sm text-slate-500">Loading the test payment…</p>
      </div>
    );
  }

  if (session.status === 'error') {
    return (
      <div className="mx-auto max-w-lg space-y-4">
        {banner}
        <Alert>
          {session.httpStatus === 404
            ? 'This test payment does not exist, or the test payment page is switched off on this server.'
            : session.message}
        </Alert>
        <Link to="/parcels" className="text-sm font-medium text-brand-700 underline">
          Back to my parcels
        </Link>
      </div>
    );
  }

  const s = session.data;
  const finished = s.status !== 'Pending';
  const mismatch = s.provider.toLowerCase() !== provider.toLowerCase();

  return (
    <div className="mx-auto max-w-lg space-y-5">
      {banner}

      <section aria-labelledby="gateway-title" className="rounded-xl border border-slate-300 bg-white p-6 shadow-sm">
        <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">Simulated provider</p>
        <h1 id="gateway-title" className="text-xl font-bold">
          {providerLabel(s.provider)} (test)
        </h1>
        <dl className="mt-4 space-y-2 text-sm">
          <div className="flex justify-between gap-4">
            <dt className="text-slate-500">Pay to</dt>
            <dd className="font-medium">{s.merchantName}</dd>
          </div>
          <div className="flex justify-between gap-4">
            <dt className="text-slate-500">Amount</dt>
            <dd className="text-lg font-bold tabular-nums">{formatNpr(s.amount)}</dd>
          </div>
          <div className="flex justify-between gap-4">
            <dt className="text-slate-500">Reference</dt>
            <dd className="font-mono text-xs">{s.reference}</dd>
          </div>
        </dl>

        {mismatch && <div className="mt-4"><Alert>This reference belongs to a different provider. Check the link and try again.</Alert></div>}
        {action.error && <div className="mt-4"><Alert>{action.error}</Alert></div>}

        {finished ? (
          <div className="mt-5 space-y-3">
            <Alert tone="info">This test payment has already been completed in the simulator ({s.status}). Go back to the website to see what the server found.</Alert>
            <Link to={returnPath} className="inline-block rounded-lg bg-brand-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-brand-700">
              Return to the website
            </Link>
          </div>
        ) : (
          <div className="mt-6 grid gap-3">
            <PrimaryButton type="button" className="min-h-12 text-base" disabled={action.busy || mismatch} onClick={() => void choose('Success')}>
              {chosen === 'Success' && action.busy ? 'Paying…' : 'Pay (success)'}
            </PrimaryButton>
            <SecondaryButton type="button" className="min-h-12 text-base" disabled={action.busy || mismatch} onClick={() => void choose('Failure')}>
              {chosen === 'Failure' && action.busy ? 'Failing…' : 'Fail the payment'}
            </SecondaryButton>
            <SecondaryButton type="button" className="min-h-12 text-base" disabled={action.busy || mismatch} onClick={() => void choose('Cancel')}>
              {chosen === 'Cancel' && action.busy ? 'Cancelling…' : 'Cancel'}
            </SecondaryButton>
          </div>
        )}
      </section>

      <p className="text-center text-xs text-slate-500">After you choose, you are sent back to the website. The website then asks our server to check the payment; it does not trust this page.</p>
    </div>
  );
}
