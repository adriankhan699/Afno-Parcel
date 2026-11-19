import { Link, useParams } from 'react-router';
import { Alert, PrimaryButton } from '../components/form';
import { formatNepalDateTime, formatNpr } from '../lib/format';
import { paymentLabel } from '../lib/labels';
import { providerLabel } from '../lib/payment';
import { paymentApi } from '../lib/paymentApi';
import { useApi } from '../lib/useApi';
import type { PaymentReceiptResponse } from '../types/api';

/** The printable receipt of a payment that has been paid. */
export default function PaymentReceiptPage() {
  const { paymentId = '' } = useParams();
  const result = useApi<PaymentReceiptResponse>(paymentId ? paymentApi.receipt(paymentId) : null);

  if (result.status === 'loading' || result.status === 'idle') return <p className="text-sm text-slate-500">Loading receipt…</p>;
  if (result.status === 'error') {
    return (
      <div className="space-y-4">
        <Alert>{result.httpStatus === 422 ? 'This payment has not been paid yet, so there is no receipt.' : result.message}</Alert>
        <Link to="/parcels" className="text-sm font-medium text-brand-700 underline">
          Back to parcels
        </Link>
      </div>
    );
  }

  const r = result.data;
  return (
    <div>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3 print:hidden">
        <Link to="/parcels" className="text-sm text-brand-700 underline">
          ← Back to parcels
        </Link>
        <PrimaryButton type="button" onClick={() => window.print()}>
          Print receipt
        </PrimaryButton>
      </div>

      <article
        aria-label={`Payment receipt ${r.receiptNumber}`}
        className="mx-auto max-w-2xl space-y-6 rounded-xl border border-slate-300 bg-white p-8 text-sm text-slate-900 shadow-sm print:max-w-none print:rounded-none print:border-0 print:p-0 print:shadow-none"
      >
        <header className="flex flex-wrap items-start justify-between gap-4 border-b border-slate-300 pb-4">
          <div>
            <p className="text-xl font-bold">{r.companyName}</p>
            {r.companyPhone && <p className="text-slate-600">☎ {r.companyPhone}</p>}
            {r.companyTaxNumber && <p className="text-slate-600">PAN/VAT: {r.companyTaxNumber}</p>}
          </div>
          <div className="text-right">
            <p className="text-lg font-semibold uppercase tracking-wide">Payment receipt</p>
            <p>
              Receipt no. <span className="font-mono">{r.receiptNumber}</span>
            </p>
            <p className="text-slate-600">{formatNepalDateTime(r.paidAtUtc)}</p>
          </div>
        </header>

        <section className="rounded-lg border border-slate-300 p-4 text-center">
          <p className="text-xs uppercase tracking-wide text-slate-500">Amount received</p>
          <p className="text-3xl font-bold tabular-nums">{formatNpr(r.amount)}</p>
          <p className="mt-1 text-xs text-slate-600">{r.currency}</p>
        </section>

        <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-2">
          <div>
            <dt className="text-slate-500">Received from</dt>
            <dd className="font-medium">{r.payerName}</dd>
          </div>
          <div>
            <dt className="text-slate-500">For parcel</dt>
            <dd className="font-mono font-medium">{r.trackingNumber}</dd>
          </div>
          <div className="sm:col-span-2">
            <dt className="text-slate-500">For</dt>
            <dd>{r.description}</dd>
          </div>
          <div>
            <dt className="text-slate-500">Payment method</dt>
            <dd>{paymentLabel(r.method)}</dd>
          </div>
          <div>
            <dt className="text-slate-500">Paid through</dt>
            <dd>{providerLabel(r.provider)}</dd>
          </div>
          {r.providerReference && (
            <div className="sm:col-span-2">
              <dt className="text-slate-500">Provider transaction reference</dt>
              <dd className="font-mono text-xs">{r.providerReference}</dd>
            </div>
          )}
          {r.receivedBy && (
            <div>
              <dt className="text-slate-500">Received by</dt>
              <dd>{r.receivedBy}</dd>
            </div>
          )}
        </dl>

        <footer className="border-t border-slate-300 pt-4 text-xs text-slate-500">Keep this receipt. It is proof that this delivery charge has been paid.</footer>
      </article>
    </div>
  );
}
