import { useState } from 'react';
import { ApiError } from '../lib/apiClient';
import { notificationErrorMessage } from '../lib/notification';
import Dialog from './Dialog';
import { Alert, PrimaryButton, SecondaryButton, TextAreaField } from './form';

interface ReasonActionDialogProps {
  open: boolean;
  title: string;
  /** What will happen, in plain words. */
  message: string;
  confirmLabel: string;
  /** Runs the action with the reason (null when none was given); throw to keep the dialog open and show the error. */
  onConfirm: (reason: string | null) => Promise<void>;
  onClose: () => void;
}

/** A confirm dialog that also asks for an optional reason. It has its own title id so it can sit on top of another dialog. */
export default function ReasonActionDialog({ open, title, message, confirmLabel, onConfirm, onClose }: ReasonActionDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title={title} titleId="reason-dialog-title">
      <Body message={message} confirmLabel={confirmLabel} onConfirm={onConfirm} onClose={onClose} />
    </Dialog>
  );
}

function Body({ message, confirmLabel, onConfirm, onClose }: Omit<ReasonActionDialogProps, 'open' | 'title'>) {
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleConfirm() {
    setBusy(true);
    setError(null);
    try {
      await onConfirm(reason.trim() || null);
      onClose();
    } catch (err) {
      setError(err instanceof ApiError ? notificationErrorMessage(err.status, err.problem.detail) : notificationErrorMessage(undefined, undefined));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-700">{message}</p>
      <TextAreaField id="reason-action-reason" label="Reason (optional)" rows={2} maxLength={300} value={reason} onChange={setReason} hint="Kept in the history of the message." />
      {error && <Alert>{error}</Alert>}
      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose} disabled={busy}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="button" onClick={() => void handleConfirm()} disabled={busy}>
          {busy ? 'Working…' : confirmLabel}
        </PrimaryButton>
      </div>
    </div>
  );
}
