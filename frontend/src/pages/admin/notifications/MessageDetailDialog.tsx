import { useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import Dialog from '../../../components/Dialog';
import { Alert, PrimaryButton, SecondaryButton } from '../../../components/form';
import NotificationStateChip, { NotificationSteps } from '../../../components/NotificationStateChip';
import ReasonActionDialog from '../../../components/ReasonActionDialog';
import { api } from '../../../lib/apiClient';
import { formatNepalDateTime } from '../../../lib/format';
import {
  attemptsLabel,
  canCancelMessage,
  canRequeue,
  channelLabel,
  describeMessageState,
  isRetrying,
  messageNote,
  templateKeyLabel,
} from '../../../lib/notification';
import { notificationApi } from '../../../lib/notificationApi';
import { useApi } from '../../../lib/useApi';
import type { NotificationMessageDetailResponse, NotificationMessageResponse, RequeueNotificationRequest } from '../../../types/api';

interface MessageDetailDialogProps {
  /** The message to show; null = closed. */
  messageId: string | null;
  /** Only a super admin may retry or cancel. */
  canEdit: boolean;
  onClose: () => void;
  /** Called after a retry or a cancel, so the list behind can refresh. */
  onChanged: () => void;
}

export default function MessageDetailDialog({ messageId, canEdit, onClose, onChanged }: MessageDetailDialogProps) {
  return (
    <Dialog open={messageId !== null} onClose={onClose} title="Message details" wide>
      {messageId !== null && <DetailBody key={messageId} messageId={messageId} canEdit={canEdit} onClose={onClose} onChanged={onChanged} />}
    </Dialog>
  );
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex justify-between gap-4 py-1">
      <dt className="text-slate-500">{label}</dt>
      <dd className="text-right text-slate-900">{children}</dd>
    </div>
  );
}

type PendingAction = 'requeue' | 'cancel' | null;

function DetailBody({ messageId, canEdit, onClose, onChanged }: Omit<MessageDetailDialogProps, 'messageId'> & { messageId: string }) {
  const [reloadKey, setReloadKey] = useState(0);
  const [pending, setPending] = useState<PendingAction>(null);
  const [done, setDone] = useState<string | null>(null);
  const result = useApi<NotificationMessageDetailResponse>(notificationApi.message(messageId), reloadKey);

  if (result.status === 'loading' || result.status === 'idle') return <p className="text-sm text-slate-500">Loading message…</p>;
  if (result.status === 'error') {
    return (
      <div className="space-y-4">
        <Alert>{result.message}</Alert>
        <div className="flex justify-end">
          <SecondaryButton type="button" onClick={onClose}>
            Close
          </SecondaryButton>
        </div>
      </div>
    );
  }

  const { message, renderedBody, attempts } = result.data;
  const state = describeMessageState(message);
  const note = messageNote(message);

  async function act(kind: 'requeue' | 'cancel', reason: string | null) {
    const body: RequeueNotificationRequest = { reason };
    const updated = await api.post<NotificationMessageResponse>(kind === 'requeue' ? notificationApi.requeue(messageId) : notificationApi.cancel(messageId), body);
    setDone(kind === 'requeue' ? 'The message is queued again and will be sent shortly.' : 'The message was cancelled and will not be sent.');
    setReloadKey((k) => k + 1);
    onChanged();
    return updated;
  }

  const times: [string, string | null | undefined][] = [
    ['Created', message.createdAtUtc],
    ['Handed to the provider', message.submittedAtUtc],
    ['Reported sent', message.sentAtUtc],
    ['Reported delivered', message.deliveredAtUtc],
    ['Reported read', message.readAtUtc],
    ['Reported failed', message.failedAtUtc],
  ];

  return (
    <div className="space-y-5 text-sm">
      {done && <Alert tone="success">{done}</Alert>}

      <div className="space-y-2">
        <NotificationStateChip message={message} />
        <NotificationSteps message={message} />
        {note && <p className="text-slate-700">{note}</p>}
        {message.status === 'Submitted' && message.deliveryStatus === 'None' && (
          <p className="text-xs text-slate-500">The provider has taken this message. It counts as delivered only after the provider reports that it was.</p>
        )}
        <p className="text-xs text-slate-500">
          Our processing status: <span className="font-medium">{message.status}</span> · The provider reported: <span className="font-medium">{message.deliveryStatus}</span> · Shown as: {state.step}
        </p>
      </div>

      <dl className="rounded-lg border border-slate-200 px-4 py-2">
        <Fact label="Channel">{channelLabel(message.channel)}</Fact>
        <Fact label="To">
          <span className="font-mono text-xs">{message.recipientMasked}</span> ({message.audience})
        </Fact>
        <Fact label="Template">{templateKeyLabel(message.templateKey)}</Fact>
        {message.parcelId && (
          <Fact label="Parcel">
            <Link to={`/parcels/${message.parcelId}`} className="font-mono text-xs text-brand-700 underline">
              {message.trackingNumber ?? 'Open parcel'}
            </Link>
          </Fact>
        )}
        <Fact label="Provider">{message.provider ?? '—'}</Fact>
        <Fact label="Provider message id">
          <span className="break-all font-mono text-xs">{message.providerMessageId ?? '—'}</span>
        </Fact>
        <Fact label="Attempts">{attemptsLabel(message)}</Fact>
        {isRetrying(message) && <Fact label="Next attempt">{formatNepalDateTime(message.nextAttemptAtUtc)}</Fact>}
        {times.map(([label, value]) => value && <Fact key={label} label={label}>{formatNepalDateTime(value)}</Fact>)}
      </dl>

      <div>
        <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Text of the message</h3>
        {renderedBody ? (
          <p className="mt-2 whitespace-pre-wrap rounded-lg bg-slate-50 p-3 text-slate-900">{renderedBody}</p>
        ) : (
          <p className="mt-2 text-slate-600">The text is not available.</p>
        )}
      </div>

      <div>
        <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-500">History</h3>
        {attempts.length === 0 ? (
          <p className="mt-2 text-slate-600">Nothing has been tried yet.</p>
        ) : (
          <div className="mt-2 overflow-x-auto rounded-lg border border-slate-200">
            <table className="min-w-full text-left text-sm">
              <caption className="sr-only">Every attempt to send this message and every report from the provider</caption>
              <thead className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
                <tr>
                  <th scope="col" className="px-3 py-2">
                    Recorded
                  </th>
                  <th scope="col" className="px-3 py-2">
                    Attempt
                  </th>
                  <th scope="col" className="px-3 py-2">
                    Result
                  </th>
                  <th scope="col" className="px-3 py-2">
                    Provider
                  </th>
                  <th scope="col" className="px-3 py-2">
                    Detail
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {attempts.map((a) => (
                  <tr key={a.id}>
                    <td className="whitespace-nowrap px-3 py-2 text-slate-700">
                      {formatNepalDateTime(a.recordedAtUtc)}
                      {a.occurredAtUtc && <p className="text-xs text-slate-500">happened {formatNepalDateTime(a.occurredAtUtc)}</p>}
                    </td>
                    <td className="px-3 py-2">{a.attemptNumber ?? '—'}</td>
                    <td className="px-3 py-2 font-medium">{a.status}</td>
                    <td className="px-3 py-2">
                      {a.provider}
                      {a.providerMessageId && <p className="break-all font-mono text-xs text-slate-500">{a.providerMessageId}</p>}
                    </td>
                    <td className="px-3 py-2 text-slate-700">{a.detail ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="flex flex-wrap items-center justify-end gap-2 border-t border-slate-100 pt-4">
        {canEdit && canRequeue(message) && (
          <PrimaryButton type="button" onClick={() => setPending('requeue')}>
            Retry now
          </PrimaryButton>
        )}
        {canEdit && canCancelMessage(message) && (
          <SecondaryButton type="button" onClick={() => setPending('cancel')}>
            Cancel message
          </SecondaryButton>
        )}
        <SecondaryButton type="button" onClick={onClose}>
          Close
        </SecondaryButton>
      </div>

      <ReasonActionDialog
        open={pending === 'requeue'}
        title="Retry this message now?"
        message="The message goes back in the queue and the system tries to send it again now."
        confirmLabel="Retry now"
        onConfirm={async (reason) => {
          await act('requeue', reason);
        }}
        onClose={() => setPending(null)}
      />
      <ReasonActionDialog
        open={pending === 'cancel'}
        title="Cancel this message?"
        message="The message will not be sent. This cannot be undone."
        confirmLabel="Cancel message"
        onConfirm={async (reason) => {
          await act('cancel', reason);
        }}
        onClose={() => setPending(null)}
      />
    </div>
  );
}
