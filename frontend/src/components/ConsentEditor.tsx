import { useState, type ReactNode } from 'react';
import { formatNepalDateTime } from '../lib/format';
import { useApi } from '../lib/useApi';
import { useNotificationAction } from '../lib/useNotificationAction';
import type { NotificationPreferencesResponse, UpdateNotificationPreferencesRequest } from '../types/api';
import { Alert, PrimaryButton } from './form';
import ToggleRow from './ToggleRow';

interface ConsentEditorProps {
  /** Where the current choices are read from. */
  loadPath: string;
  /** Saves both choices and returns what the server now holds. */
  save: (body: UpdateNotificationPreferencesRequest) => Promise<NotificationPreferencesResponse>;
  /** Makes the element ids unique when two editors could be on one page. */
  idPrefix: string;
  smsLabel: string;
  whatsAppLabel: string;
  smsDescription?: ReactNode;
  whatsAppDescription?: ReactNode;
  /** Shown after a successful save. */
  savedMessage: string;
}

/** The SMS and WhatsApp switches with a save button and the date of each last decision. Both choices are off until someone says yes. */
export default function ConsentEditor(props: ConsentEditorProps) {
  const result = useApi<NotificationPreferencesResponse>(props.loadPath);
  if (result.status === 'loading' || result.status === 'idle') return <p className="text-sm text-slate-500">Loading choices…</p>;
  if (result.status === 'error') return <Alert>{result.message}</Alert>;
  return <ConsentForm {...props} initial={result.data} />;
}

function decisionLine(on: boolean, decidedAtUtc: string | null | undefined): string {
  if (!decidedAtUtc) return 'No decision recorded yet, so this is off.';
  return `${on ? 'Switched on' : 'Switched off'} on ${formatNepalDateTime(decidedAtUtc)}.`;
}

function ConsentForm({ initial, save, idPrefix, smsLabel, whatsAppLabel, smsDescription, whatsAppDescription, savedMessage }: ConsentEditorProps & { initial: NotificationPreferencesResponse }) {
  const action = useNotificationAction();
  const [saved, setSaved] = useState(initial);
  const [sms, setSms] = useState(initial.sms);
  const [whatsApp, setWhatsApp] = useState(initial.whatsApp);

  const changed = sms !== saved.sms || whatsApp !== saved.whatsApp;

  async function handleSave() {
    const done = await action.run(() => save({ sms, whatsApp }), savedMessage);
    if (done.ok) {
      setSaved(done.value);
      setSms(done.value.sms);
      setWhatsApp(done.value.whatsApp);
    }
  }

  return (
    <div className="space-y-4">
      <div className="space-y-3">
        <ToggleRow
          id={`${idPrefix}-sms`}
          label={smsLabel}
          checked={sms}
          onChange={setSms}
          description={
            <>
              {smsDescription}
              <span className="block text-slate-500">{decisionLine(saved.sms, saved.smsDecidedAtUtc)}</span>
            </>
          }
        />
        <ToggleRow
          id={`${idPrefix}-whatsapp`}
          label={whatsAppLabel}
          checked={whatsApp}
          onChange={setWhatsApp}
          description={
            <>
              {whatsAppDescription}
              <span className="block text-slate-500">{decisionLine(saved.whatsApp, saved.whatsAppDecidedAtUtc)}</span>
            </>
          }
        />
      </div>
      <div aria-live="polite" className="space-y-3">
        {action.error && <Alert>{action.error}</Alert>}
        {action.notice && <Alert tone="success">{action.notice}</Alert>}
      </div>
      <PrimaryButton type="button" disabled={action.busy || !changed} onClick={() => void handleSave()}>
        {action.busy ? 'Saving…' : 'Save choices'}
      </PrimaryButton>
    </div>
  );
}
