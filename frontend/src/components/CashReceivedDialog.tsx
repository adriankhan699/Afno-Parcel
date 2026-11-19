import { useState } from 'react';
import { api } from '../lib/apiClient';
import { formatNpr } from '../lib/format';
import { NOTE_MAX_LENGTH, buildOfficeCashRequest, validateCashReceived } from '../lib/payment';
import { paymentApi } from '../lib/paymentApi';
import { usePaymentAction } from '../lib/usePaymentAction';
import type { PaymentResponse } from '../types/api';
import Dialog from './Dialog';
import { Alert, PrimaryButton, SecondaryButton, TextAreaField, TextField } from './form';

interface CashReceivedDialogProps {
  open: boolean;
  parcelId: string;
  trackingNumber: string;
  /** The delivery charge due, in rupees. Only exactly this amount is accepted. */
  due: number;
  onClose: () => void;
  /** Called after the server recorded the cash. */
  onDone: () => void;
}

/** Counter staff recording the cash they took for a parcel: the amount is shown, typed, reviewed and only then sent. */
export default function CashReceivedDialog({ open, ...rest }: CashReceivedDialogProps) {
  return (
    <Dialog open={open} onClose={rest.onClose} title="Record cash received">
      <CashBody {...rest} />
    </Dialog>
  );
}

function CashBody({ parcelId, trackingNumber, due, onClose, onDone }: Omit<CashReceivedDialogProps, 'open'>) {
  const [amountText, setAmountText] = useState('');
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | undefined>();
  const [amount, setAmount] = useState<number | null>(null);
  const action = usePaymentAction();

  function review() {
    const entry = validateCashReceived(amountText, due);
    if (!entry.ok || entry.amount === null) {
      setError(entry.error);
      return;
    }
    setError(undefined);
    setAmount(entry.amount);
  }

  async function confirm() {
    if (amount === null) return;
    const done = await action.run(() => api.post<PaymentResponse>(paymentApi.officeCash, buildOfficeCashRequest(parcelId, amount, note)));
    if (done.ok) {
      onClose();
      onDone();
    }
  }

  if (amount !== null) {
    return (
      <div className="space-y-4">
        <p className="text-sm text-slate-700">
          Record <strong>{formatNpr(amount)}</strong> received in cash for parcel <span className="font-mono">{trackingNumber}</span>? The payment will be marked as paid and a receipt becomes available. This cannot be undone.
        </p>
        {action.error && <Alert>{action.error}</Alert>}
        <div className="flex justify-end gap-2">
          <SecondaryButton type="button" onClick={() => setAmount(null)} disabled={action.busy}>
            Back
          </SecondaryButton>
          <PrimaryButton type="button" onClick={() => void confirm()} disabled={action.busy}>
            {action.busy ? 'Recording…' : 'Yes, record it'}
          </PrimaryButton>
        </div>
      </div>
    );
  }

  return (
    <form
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        review();
      }}
      className="space-y-4"
    >
      <p className="rounded-lg bg-slate-50 p-3 text-sm text-slate-700">
        Amount due for <span className="font-mono">{trackingNumber}</span>: <strong className="text-base">{formatNpr(due)}</strong>
      </p>
      <TextField
        id="cash-amount"
        label="Cash received (Rs.)"
        required
        inputMode="decimal"
        autoComplete="off"
        value={amountText}
        onChange={setAmountText}
        error={error}
        hint="Enter exactly the amount due. Give any change back first."
      />
      <TextAreaField id="cash-note" label="Note (optional)" rows={2} maxLength={NOTE_MAX_LENGTH} value={note} onChange={setNote} />
      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="submit">Review</PrimaryButton>
      </div>
    </form>
  );
}
