import { useState } from 'react';
import { ApiError } from '../lib/apiClient';
import { problemSummary } from '../lib/problems';
import Dialog from './Dialog';
import { Alert, PrimaryButton, SecondaryButton } from './form';

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  /** What will happen, in plain words. */
  message: string;
  confirmLabel: string;
  /** Runs the action; throw (an ApiError is shown as its message) to keep the dialog open. */
  onConfirm: () => Promise<void>;
  onClose: () => void;
}

export default function ConfirmDialog({ open, title, message, confirmLabel, onConfirm, onClose }: ConfirmDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title={title}>
      <ConfirmBody message={message} confirmLabel={confirmLabel} onConfirm={onConfirm} onClose={onClose} />
    </Dialog>
  );
}

function ConfirmBody({ message, confirmLabel, onConfirm, onClose }: Omit<ConfirmDialogProps, 'open' | 'title'>) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleConfirm() {
    setBusy(true);
    setError(null);
    try {
      await onConfirm();
      onClose();
    } catch (err) {
      setError(err instanceof ApiError ? (err.status === 403 ? 'You are not allowed to do this.' : problemSummary(err.problem)) : 'Could not reach the server. Please try again shortly.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-700">{message}</p>
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
