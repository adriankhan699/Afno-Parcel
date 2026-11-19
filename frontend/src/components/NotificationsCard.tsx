import { useState } from 'react';
import { Link } from 'react-router';
import { api } from '../lib/apiClient';
import { formatNepalDateTime } from '../lib/format';
import { channelLabel, templateKeyLabel } from '../lib/notification';
import { notificationApi } from '../lib/notificationApi';
import { useApi } from '../lib/useApi';
import type { NotificationMessageResponse, NotificationPreferencesResponse, UpdateNotificationPreferencesRequest } from '../types/api';
import ConsentEditor from './ConsentEditor';
import { Alert, SecondaryButton } from './form';
import { NotificationStateCell } from './NotificationStateChip';

interface NotificationsCardProps {
  parcelId: string;
  trackingNumber: string;
  /** Counter staff also see the sender's consent form. */
  canSetConsent: boolean;
  /** Super admins and branch managers may open the notifications admin pages. */
  canOpenAdmin: boolean;
}

/** What was sent about this parcel and where each message got to, for staff; counter staff can also record the sender's consent. */
export default function NotificationsCard({ parcelId, trackingNumber, canSetConsent, canOpenAdmin }: NotificationsCardProps) {
  const [reloadKey, setReloadKey] = useState(0);
  const result = useApi<NotificationMessageResponse[]>(notificationApi.forParcel(parcelId), reloadKey);

  return (
    <section aria-labelledby="notifications-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="notifications-heading" className="text-base font-semibold text-slate-900">
          Notifications
        </h2>
        <SecondaryButton type="button" onClick={() => setReloadKey((k) => k + 1)} disabled={result.status === 'loading'}>
          Refresh
        </SecondaryButton>
      </div>
      <div className="mt-3 space-y-6 text-sm">
        <div aria-live="polite">
          {(result.status === 'loading' || result.status === 'idle') && <p className="text-slate-500">Loading notifications…</p>}
          {result.status === 'error' && <Alert>{result.message}</Alert>}
          {result.status === 'loaded' && result.data.length === 0 && (
            <p className="text-slate-600">No SMS or WhatsApp messages have been created for this parcel yet.</p>
          )}
          {result.status === 'loaded' && result.data.length > 0 && (
            <div className="overflow-x-auto rounded-lg border border-slate-200">
              <table className="min-w-full text-left text-sm">
                <caption className="sr-only">Messages about parcel {trackingNumber}</caption>
                <thead className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
                  <tr>
                    <th scope="col" className="px-3 py-2">
                      Created
                    </th>
                    <th scope="col" className="px-3 py-2">
                      Message
                    </th>
                    <th scope="col" className="px-3 py-2">
                      To
                    </th>
                    <th scope="col" className="px-3 py-2">
                      State
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {result.data.map((m) => (
                    <tr key={m.id}>
                      <td className="whitespace-nowrap px-3 py-2 text-slate-700">{formatNepalDateTime(m.createdAtUtc)}</td>
                      <td className="px-3 py-2">
                        <p className="font-medium text-slate-900">{templateKeyLabel(m.templateKey)}</p>
                        <p className="text-xs text-slate-500">{channelLabel(m.channel)}</p>
                      </td>
                      <td className="whitespace-nowrap px-3 py-2">
                        <p className="font-mono text-xs text-slate-700">{m.recipientMasked}</p>
                        <p className="text-xs text-slate-500">{m.audience}</p>
                      </td>
                      <td className="px-3 py-2">
                        <NotificationStateCell message={m} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <p className="mt-2 text-xs text-slate-500">
            “Accepted by provider” only means the provider took the message. It counts as delivered once the provider reports it.
            {canOpenAdmin && (
              <>
                {' '}
                <Link to="/admin/notifications?tab=messages" className="font-medium text-brand-700 underline">
                  All messages
                </Link>
              </>
            )}
          </p>
        </div>

        {canSetConsent && (
          <div className="border-t border-slate-100 pt-4">
            <h3 className="text-sm font-semibold text-slate-900">Sender notification consent</h3>
            <p className="mb-3 mt-1 text-xs text-slate-600">
              Record whether the sender of this parcel agreed, at the counter, to get updates about it. Nothing is sent to the sender until they have said yes.
            </p>
            <ConsentEditor
              loadPath={notificationApi.parcelConsent(parcelId)}
              save={(body: UpdateNotificationPreferencesRequest) => api.put<NotificationPreferencesResponse>(notificationApi.parcelConsent(parcelId), body)}
              idPrefix="sender-consent"
              smsLabel="Sender agrees to SMS updates"
              whatsAppLabel="Sender agrees to WhatsApp updates"
              savedMessage="The sender's choices are saved. A later change here adds a new record; the history is kept."
            />
          </div>
        )}
      </div>
    </section>
  );
}
