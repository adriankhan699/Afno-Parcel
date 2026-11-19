import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import { useAuth } from '../auth/useAuth';
import { Alert, PrimaryButton, SelectField, TextAreaField, TextField } from '../components/form';
import PartyFields from '../components/PartyFields';
import PriceBreakdown from '../components/PriceBreakdown';
import { ApiError, api } from '../lib/apiClient';
import {
  LIMITS,
  buildBookParcelRequest,
  buildQuoteRequest,
  emptyBookingForm,
  isPrepaid,
  validateBookingForm,
  type BookingForm,
  type FormErrors,
} from '../lib/booking';
import { formatNepalDateTime } from '../lib/format';
import { bookingConsentRequest, chosenChannelsLabel, withBookingConsent, type ConsentChoices, type ConsentOutcome } from '../lib/notification';
import { PAYMENT_LABELS, SERVICE_HINTS, SERVICE_LABELS } from '../lib/labels';
import { problemSummary, problemToFieldErrors } from '../lib/problems';
import { errorMessage, useApi } from '../lib/useApi';
import {
  PAYMENT_METHODS,
  SERVICE_TYPES,
  type BranchResponse,
  type PagedResponse,
  type ParcelResponse,
  type PaymentMethod,
  type QuoteResponse,
  type ServiceAreaResponse,
  type ServiceType,
} from '../types/api';

function Section({ title, description, children }: { title: string; description?: string; children: ReactNode }) {
  return (
    <fieldset className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
      <legend className="px-2 text-base font-semibold text-slate-900">{title}</legend>
      {description && <p className="mb-4 text-sm text-slate-600">{description}</p>}
      {children}
    </fieldset>
  );
}

/** "Receiver.Address.District" style keys become element ids like "receiver-district" for focusing the first error. */
function fieldId(errorKey: string): string {
  return errorKey.replace(/\.address\./, '.').replace(/\./g, '-');
}

type QuoteView =
  | { kind: 'incomplete' }
  | { kind: 'pending' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; quote: QuoteResponse };

export default function BookParcelPage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const isCounter = auth.isCounterStaff;

  const areas = useApi<ServiceAreaResponse[]>('/api/v1/service-areas');
  const branches = useApi<PagedResponse<BranchResponse>>('/api/v1/branches?page=1&pageSize=50');
  const areaList = areas.status === 'loaded' ? areas.data : [];
  const branchList = branches.status === 'loaded' ? branches.data.items.filter((b) => b.isActive) : [];

  const [form, setForm] = useState<BookingForm>(() => {
    const initial = emptyBookingForm();
    if (!isCounter && auth.user) {
      initial.sender.fullName = auth.user.fullName;
      initial.sender.phoneNumber = auth.user.phoneNumber ?? '';
    }
    return initial;
  });
  // Updates by SMS or WhatsApp are off unless the sender says yes. This is not part of the booking itself: it is sent right after.
  const [consent, setConsent] = useState<ConsentChoices>({ sms: false, whatsApp: false });
  const [errors, setErrors] = useState<FormErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const alertRef = useRef<HTMLDivElement>(null);

  // With a single branch there is nothing to choose, so it is used automatically.
  const onlyBranchId = branchList.length === 1 ? (branchList[0]?.id ?? '') : '';
  const effectiveForm: BookingForm =
    form.originBranchId || !onlyBranchId ? form : { ...form, originBranchId: onlyBranchId };

  const update = (patch: Partial<BookingForm>) => setForm((f) => ({ ...f, ...patch }));

  // --- live price estimate (informational only: the server recalculates when the booking is made) ---
  const quoteRequest = buildQuoteRequest(effectiveForm);
  const quoteKey = quoteRequest ? JSON.stringify(quoteRequest) : null;
  const [debouncedKey, setDebouncedKey] = useState<string | null>(null);
  const [quoteResult, setQuoteResult] = useState<{ key: string; quote?: QuoteResponse; error?: string } | null>(null);

  useEffect(() => {
    if (quoteKey === null) return;
    const timer = setTimeout(() => setDebouncedKey(quoteKey), 500);
    return () => clearTimeout(timer);
  }, [quoteKey]);

  useEffect(() => {
    if (debouncedKey === null) return;
    const controller = new AbortController();
    api
      .post<QuoteResponse>('/api/v1/pricing/quote', JSON.parse(debouncedKey) as unknown, controller.signal)
      .then((quote) => setQuoteResult({ key: debouncedKey, quote }))
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setQuoteResult({ key: debouncedKey, error: errorMessage(err, 'The price could not be calculated right now.') });
      });
    return () => controller.abort();
  }, [debouncedKey]);

  let quoteView: QuoteView;
  if (quoteKey === null) quoteView = { kind: 'incomplete' };
  else if (quoteResult?.key === quoteKey) {
    quoteView = quoteResult.quote ? { kind: 'ready', quote: quoteResult.quote } : { kind: 'error', message: quoteResult.error ?? '' };
  } else quoteView = { kind: 'pending' };

  useEffect(() => {
    if (formError) alertRef.current?.focus();
  }, [formError]);

  /**
   * The consent choices are sent with the booking itself (see withBookingConsent), so by the time the parcel exists they are
   * already recorded. This is what the parcel page is told to confirm.
   */
  function consentOutcome(): ConsentOutcome | undefined {
    return bookingConsentRequest(consent) ? { kind: 'saved', channels: chosenChannelsLabel(consent) } : undefined;
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (submitting) return;

    const found = validateBookingForm(effectiveForm);
    setErrors(found);
    const firstKey = Object.keys(found)[0];
    if (firstKey) {
      setFormError(problemSummary({ errors: Object.fromEntries(Object.keys(found).map((k) => [k, [found[k] ?? '']])) }));
      setTimeout(() => document.getElementById(fieldId(firstKey))?.focus(), 0);
      return;
    }
    setFormError(null);

    setSubmitting(true);
    try {
      const request = withBookingConsent(buildBookParcelRequest(effectiveForm, { allowPaymentReference: isCounter }), consent);
      const parcel = await api.post<ParcelResponse>('/api/v1/parcels', request);
      navigate(`/parcels/${parcel.id}`, { state: { justBooked: true, consent: consentOutcome() } });
    } catch (err) {
      if (err instanceof ApiError) {
        const fields = problemToFieldErrors(err.problem);
        setErrors(fields);
        setFormError(problemSummary(err.problem));
        const first = Object.keys(fields)[0];
        if (first) setTimeout(() => document.getElementById(fieldId(first))?.focus(), 0);
      } else {
        setFormError('Could not reach the server. Please try again shortly.');
      }
      setSubmitting(false);
    }
  }

  const prepaid = isPrepaid(form.paymentMethod);

  return (
    <div>
      <h1 className="text-2xl font-bold tracking-tight">{isCounter ? 'New counter booking' : 'Book a parcel'}</h1>
      <p className="mt-2 text-sm text-slate-600">
        {isCounter
          ? 'Book a parcel for a walk-in customer. Enter the sender’s details as given at the counter.'
          : 'Tell us who is sending and who receives. You will see the price before you confirm.'}
      </p>

      {(areas.status === 'error' || branches.status === 'error') && (
        <div className="mt-4">
          <Alert>
            {areas.status === 'error' ? areas.message : branches.status === 'error' ? branches.message : ''}
          </Alert>
        </div>
      )}

      <form onSubmit={handleSubmit} noValidate className="mt-6 grid gap-6 lg:grid-cols-3">
        <div className="space-y-6 lg:col-span-2">
          {formError && (
            <div ref={alertRef} tabIndex={-1} className="outline-none">
              <Alert>{formError}</Alert>
            </div>
          )}

          <Section title="1. Sender" description={isCounter ? 'The walk-in customer handing over the parcel.' : 'Who is sending the parcel (pre-filled from your account).'}>
            <PartyFields
              prefix="sender"
              who="Sender"
              value={form.sender}
              onChange={(sender) => update({ sender })}
              errors={errors}
              areas={areaList}
            />
            <fieldset className="mt-5 rounded-lg border border-slate-200 p-4">
              <legend className="px-1 text-sm font-medium text-slate-700">Updates about this parcel (optional)</legend>
              <div className="space-y-2">
                <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-800">
                  <input
                    id="consent-sms"
                    type="checkbox"
                    checked={consent.sms}
                    onChange={(event) => setConsent((c) => ({ ...c, sms: event.target.checked }))}
                    className="h-4 w-4 accent-brand-600"
                  />
                  Send me SMS updates
                </label>
                <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-800">
                  <input
                    id="consent-whatsapp"
                    type="checkbox"
                    checked={consent.whatsApp}
                    onChange={(event) => setConsent((c) => ({ ...c, whatsApp: event.target.checked }))}
                    className="h-4 w-4 accent-brand-600"
                  />
                  Send me WhatsApp updates
                </label>
              </div>
              <p className="mt-3 text-xs text-slate-500">
                {isCounter
                  ? 'Tick only if the sender agrees now, at the counter. We message only about this parcel’s journey, never advertising, and the sender can stop at any time (on WhatsApp by replying STOP).'
                  : 'We use your phone number only to tell you about your parcels, never for advertising. You can change this any time on the Parcel updates page, and on WhatsApp by replying STOP.'}
              </p>
            </fieldset>
          </Section>

          <Section title="2. Receiver" description="Where the parcel is going. We deliver to the districts listed as served.">
            <PartyFields
              prefix="receiver"
              who="Receiver"
              value={form.receiver}
              onChange={(receiver) => update({ receiver })}
              errors={errors}
              areas={areaList}
              requireServiceable
            />
          </Section>

          <Section title="3. Parcel details">
            <div className="grid gap-4 sm:grid-cols-2">
              <TextField
                id="description"
                label="What is inside?"
                required
                maxLength={LIMITS.description}
                className="sm:col-span-2"
                value={form.description}
                onChange={(description) => update({ description })}
                error={errors.description}
              />
              <TextField
                id="quantity"
                label="Number of pieces"
                required
                inputMode="numeric"
                value={form.quantity}
                onChange={(quantity) => update({ quantity })}
                error={errors.quantity}
              />
              <TextField
                id="weightKg"
                label="Total weight (kg)"
                required
                inputMode="decimal"
                placeholder="e.g. 2.5"
                value={form.weightKg}
                onChange={(weightKg) => update({ weightKg })}
                error={errors.weightKg}
              />
              <div className="sm:col-span-2">
                <p className="mb-1 text-sm font-medium text-slate-700">Size in cm (optional)</p>
                <div className="grid gap-4 sm:grid-cols-3">
                  <TextField id="lengthCm" label="Length" inputMode="decimal" value={form.lengthCm} onChange={(lengthCm) => update({ lengthCm })} error={errors.lengthCm} />
                  <TextField id="widthCm" label="Width" inputMode="decimal" value={form.widthCm} onChange={(widthCm) => update({ widthCm })} error={errors.widthCm} />
                  <TextField id="heightCm" label="Height" inputMode="decimal" value={form.heightCm} onChange={(heightCm) => update({ heightCm })} error={errors.heightCm} />
                </div>
                <p className="mt-1 text-xs text-slate-500">
                  Give all three or none. Bulky parcels are charged by size (length × width × height ÷ 5000) if that is more than the weight.
                </p>
              </div>
              <TextField
                id="declaredValue"
                label="Declared value in NPR (optional)"
                inputMode="decimal"
                value={form.declaredValue}
                onChange={(declaredValue) => update({ declaredValue })}
                error={errors.declaredValue}
              />
              <TextAreaField
                id="notes"
                label="Notes (optional)"
                rows={2}
                maxLength={LIMITS.notes}
                className="sm:col-span-2"
                value={form.notes}
                onChange={(notes) => update({ notes })}
                error={errors.notes}
              />
            </div>
          </Section>

          <Section title="4. Service & payment">
            <div className="grid gap-4 sm:grid-cols-2">
              <SelectField
                id="originBranchId"
                label="Hand-over branch"
                required
                className="sm:col-span-2"
                placeholder={branches.status === 'loading' ? 'Loading branches…' : 'Choose a branch'}
                value={effectiveForm.originBranchId}
                onChange={(originBranchId) => update({ originBranchId })}
                options={branchList.map((b) => ({ value: b.id, label: `${b.name} — ${b.address.district}` }))}
                hint="Price is calculated from this branch’s area."
                error={errors.originBranchId}
              />

              <fieldset className="sm:col-span-2">
                <legend className="mb-1 text-sm font-medium text-slate-700">Delivery type</legend>
                <div className="grid gap-2 sm:grid-cols-3">
                  {SERVICE_TYPES.map((type) => (
                    <label
                      key={type}
                      className={`cursor-pointer rounded-lg border p-3 text-sm ${
                        form.serviceType === type ? 'border-brand-600 bg-brand-50' : 'border-slate-300 bg-white hover:bg-slate-50'
                      }`}
                    >
                      <input
                        id={type === 'Standard' ? 'serviceType' : `serviceType-${type}`}
                        type="radio"
                        name="serviceType"
                        value={type}
                        checked={form.serviceType === type}
                        onChange={() => update({ serviceType: type as ServiceType })}
                        className="mr-2 accent-brand-600"
                      />
                      <span className="font-medium">{SERVICE_LABELS[type]}</span>
                      <span className="mt-1 block text-xs text-slate-500">{SERVICE_HINTS[type]}</span>
                    </label>
                  ))}
                </div>
                {errors.serviceType && (
                  <p role="alert" className="mt-1 text-xs text-red-700">
                    {errors.serviceType}
                  </p>
                )}
              </fieldset>

              <SelectField
                id="paymentMethod"
                label="Payment method"
                required
                value={form.paymentMethod}
                onChange={(paymentMethod) => update({ paymentMethod: paymentMethod as PaymentMethod })}
                options={PAYMENT_METHODS.map((m) => ({ value: m, label: PAYMENT_LABELS[m] }))}
                hint={
                  form.paymentMethod === 'CashOnDelivery'
                    ? 'The receiver pays the delivery charge when the parcel arrives.'
                    : isCounter
                      ? 'Collect the charge now; the payment is recorded as paid.'
                      : 'You pay the delivery charge before dispatch; the branch confirms your payment on hand-over.'
                }
                error={errors.paymentMethod}
              />
              <TextField
                id="codAmount"
                label="Amount to collect from receiver in NPR (optional)"
                inputMode="decimal"
                value={form.codAmount}
                onChange={(codAmount) => update({ codAmount })}
                hint="Goods money collected on your behalf. A cash-on-delivery fee applies."
                error={errors.codAmount}
              />
              {isCounter && prepaid && (
                <TextField
                  id="paymentReference"
                  label="Payment reference (optional)"
                  maxLength={LIMITS.reference}
                  hint="Wallet transaction id or bank slip number."
                  className="sm:col-span-2"
                  value={form.paymentReference}
                  onChange={(paymentReference) => update({ paymentReference })}
                  error={errors.paymentReference}
                />
              )}
            </div>
          </Section>

          <div className="flex items-center gap-3">
            <PrimaryButton type="submit" disabled={submitting}>
              {submitting ? 'Booking…' : 'Confirm booking'}
            </PrimaryButton>
          </div>
        </div>

        <aside aria-label="Price estimate" className="lg:col-span-1">
          <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm lg:sticky lg:top-4">
            <h2 className="text-base font-semibold">Price estimate</h2>
            <div aria-live="polite" className="mt-3 text-sm">
              {quoteView.kind === 'incomplete' && (
                <p className="text-slate-500">
                  Choose the hand-over branch, then fill in the receiver’s address and the weight to see the price.
                </p>
              )}
              {quoteView.kind === 'pending' && <p className="text-slate-500">Calculating…</p>}
              {quoteView.kind === 'error' && <p className="rounded-lg bg-amber-50 p-3 text-amber-800">{quoteView.message}</p>}
              {quoteView.kind === 'ready' && (
                <>
                  <p className="text-slate-600">
                    {quoteView.quote.originArea ? `${quoteView.quote.originArea} → ` : ''}
                    {quoteView.quote.destinationArea}
                  </p>
                  <PriceBreakdown breakdown={quoteView.quote.breakdown} className="mt-2" />
                  <p className="mt-3 text-slate-600">
                    Estimated delivery:{' '}
                    <span className="font-medium text-slate-900">{formatNepalDateTime(quoteView.quote.estimatedDeliveryAtUtc)}</span>
                    {quoteView.quote.estimatedDeliveryDays > 0 && ` (about ${quoteView.quote.estimatedDeliveryDays} day${quoteView.quote.estimatedDeliveryDays === 1 ? '' : 's'})`}
                  </p>
                </>
              )}
            </div>
            <p className="mt-4 border-t border-slate-100 pt-3 text-xs text-slate-500">
              This is an estimate. The final delivery charge is always calculated by our system when you confirm the booking.
            </p>
          </div>
        </aside>
      </form>
    </div>
  );
}
