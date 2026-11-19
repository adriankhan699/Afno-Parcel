import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { api } from '../lib/apiClient';
import { formatNepalDateTime, formatNpr } from '../lib/format';
import {
  buildInitiateRequest,
  canRecordCash,
  canShowReceipt,
  paymentStatusLabel,
  paymentStatusTone,
  payableProvider,
  providerLabel,
  resolveRedirect,
  transactionStatusLabel,
  transactionStatusTone,
} from '../lib/payment';
import { paymentApi } from '../lib/paymentApi';
import { paymentLabel } from '../lib/labels';
import { useApi } from '../lib/useApi';
import { usePaymentAction } from '../lib/usePaymentAction';
import type { InitiatePaymentResponse, OnlineProvider, PaymentResponse } from '../types/api';
import CashReceivedDialog from './CashReceivedDialog';
import { Alert, PrimaryButton } from './form';

interface PaymentCardProps {
  parcelId: string;
  trackingNumber: string;
  parcelStatus: string;
  isCounterStaff: boolean;
  /** Called after cash was recorded, so the page can reload the parcel. */
  onChanged: () => void;
}

/** The parcel's payment(s): amount, status, the full transaction history, and the ways to pay now (online or cash at the counter). */
export default function PaymentCard({ parcelId, trackingNumber, parcelStatus, isCounterStaff, onChanged }: PaymentCardProps) {
  const [reloadKey, setReloadKey] = useState(0);
  const result = useApi<PaymentResponse[]>(paymentApi.forParcel(parcelId), reloadKey);

  return (
    <section aria-labelledby="payment-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
      <h2 id="payment-heading" className="text-base font-semibold text-slate-900">
        Payment
      </h2>
      <div className="mt-3 space-y-6 text-sm" aria-live="polite">
        {(result.status === 'loading' || result.status === 'idle') && <p className="text-slate-500">Loading payment…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.length === 0 && <p className="text-slate-600">No payment is due for this parcel.</p>}
        {result.status === 'loaded' &&
          result.data.map((payment) => (
            <PaymentBlock
              key={payment.id}
              payment={payment}
              parcelId={parcelId}
              trackingNumber={trackingNumber}
              parcelStatus={parcelStatus}
              isCounterStaff={isCounterStaff}
              onChanged={() => {
                setReloadKey((k) => k + 1);
                onChanged();
              }}
            />
          ))}
      </div>
    </section>
  );
}

interface PaymentBlockProps extends Omit<PaymentCardProps, 'onChanged'> {
  payment: PaymentResponse;
  onChanged: () => void;
}

function PaymentBlock({ payment, parcelId, trackingNumber, parcelStatus, isCounterStaff, onChanged }: PaymentBlockProps) {
  const navigate = useNavigate();
  const action = usePaymentAction();
  const [cashOpen, setCashOpen] = useState(false);
  const [redirectError, setRedirectError] = useState<string | null>(null);

  const provider = payableProvider(payment);
  const cash = canRecordCash(payment, isCounterStaff, parcelStatus);

  async function pay(chosen: OnlineProvider) {
    setRedirectError(null);
    const done = await action.run(() => api.post<InitiatePaymentResponse>(paymentApi.initiate, buildInitiateRequest(parcelId, chosen)));
    if (!done.ok) return;

    // Only the server's answer to this request is used, and only if it is an address we are willing to open.
    const target = resolveRedirect(done.value.redirectUrl, done.value.testMode, window.location.origin);
    if (target === null) {
      setRedirectError('The payment page address we were given is not one we can trust, so nobody was redirected. Please contact us.');
    } else if (target.kind === 'internal') {
      navigate(target.path);
    } else {
      window.location.assign(target.url);
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-2xl font-bold tabular-nums text-slate-900">{formatNpr(payment.amount)}</p>
          <p className="mt-1 text-slate-600">
            {paymentLabel(payment.method)}
            {payment.paidAtUtc && <> · paid {formatNepalDateTime(payment.paidAtUtc)}</>}
          </p>
          {payment.reference && (
            <p className="text-xs text-slate-500">
              Reference <span className="font-mono">{payment.reference}</span>
            </p>
          )}
        </div>
        <div className="flex flex-col items-end gap-2">
          <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${paymentStatusTone(payment.status)}`}>{paymentStatusLabel(payment.status)}</span>
          {canShowReceipt(payment) && (
            <Link to={`/payments/${payment.id}/receipt`} className="text-sm font-medium text-brand-700 underline">
              Payment receipt{payment.receiptNumber ? ` ${payment.receiptNumber}` : ''}
            </Link>
          )}
        </div>
      </div>

      {action.error && <Alert>{action.error}</Alert>}
      {redirectError && <Alert>{redirectError}</Alert>}

      {provider && (
        <div className="rounded-lg border border-slate-200 bg-slate-50 p-4">
          <p className="font-medium text-slate-900">This parcel is waiting to be paid with {providerLabel(provider)}.</p>
          <p className="mt-1 text-xs text-slate-600">
            Test mode: you will be taken to a simulated payment page of this website. No real money moves. The payment only counts once our server has checked it with the provider.
          </p>
          <PrimaryButton type="button" className="mt-3" disabled={action.busy} onClick={() => void pay(provider)}>
            {action.busy ? 'Starting…' : `Pay with ${providerLabel(provider)}`}
          </PrimaryButton>
        </div>
      )}

      {cash && (
        <div className="rounded-lg border border-slate-200 bg-slate-50 p-4">
          <p className="font-medium text-slate-900">Paying in cash at the office?</p>
          <PrimaryButton type="button" className="mt-3" onClick={() => setCashOpen(true)}>
            Record cash received
          </PrimaryButton>
          <CashReceivedDialog open={cashOpen} parcelId={parcelId} trackingNumber={trackingNumber} due={payment.amount} onClose={() => setCashOpen(false)} onDone={onChanged} />
        </div>
      )}

      <div>
        <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Transaction history</h3>
        {payment.transactions.length === 0 ? (
          <p className="mt-2 text-slate-600">No payment attempts yet.</p>
        ) : (
          <div className="mt-2 overflow-x-auto rounded-lg border border-slate-200">
            <table className="min-w-full text-left text-sm">
              <caption className="sr-only">Payment attempts for parcel {trackingNumber}</caption>
              <thead className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
                <tr>
                  <th scope="col" className="px-3 py-2">
                    Started
                  </th>
                  <th scope="col" className="px-3 py-2">
                    Reference
                  </th>
                  <th scope="col" className="px-3 py-2">
                    Provider
                  </th>
                  <th scope="col" className="px-3 py-2">
                    Status
                  </th>
                  <th scope="col" className="px-3 py-2">
                    Provider reference
                  </th>
                  <th scope="col" className="px-3 py-2 text-right">
                    Amount
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {payment.transactions.map((t) => (
                  <tr key={t.id}>
                    <td className="whitespace-nowrap px-3 py-2 text-slate-700">{formatNepalDateTime(t.createdAtUtc)}</td>
                    <td className="px-3 py-2 font-mono text-xs text-slate-700">{t.reference}</td>
                    <td className="whitespace-nowrap px-3 py-2">{providerLabel(t.provider)}</td>
                    <td className="px-3 py-2">
                      <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${transactionStatusTone(t.status)}`}>{transactionStatusLabel(t.status)}</span>
                      {t.failureReason && <p className="mt-1 max-w-56 text-xs text-red-700">{t.failureReason}</p>}
                    </td>
                    <td className="px-3 py-2 font-mono text-xs text-slate-700">{t.providerReference ?? '—'}</td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">{formatNpr(t.amount)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
