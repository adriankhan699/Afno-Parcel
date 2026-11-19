import { useState } from 'react';
import { Alert, SecondaryButton } from '../../../components/form';
import NotificationStateChip from '../../../components/NotificationStateChip';
import Pagination from '../../../components/Pagination';
import { api } from '../../../lib/apiClient';
import { formatNepalDateTime } from '../../../lib/format';
import { SIMULATE_LABELS, channelLabel, describeMessageState, mockMessagesPath } from '../../../lib/notification';
import { notificationApi } from '../../../lib/notificationApi';
import { useApi } from '../../../lib/useApi';
import { useNotificationAction } from '../../../lib/useNotificationAction';
import {
  SIMULATED_STATUSES,
  type MockProviderMessageResponse,
  type NotificationMessageResponse,
  type PagedResponse,
  type SimulateStatusRequest,
  type SimulatedStatus,
} from '../../../types/api';

/** What the mock provider "sent", with buttons that make it report back like a real provider would. Super admins only. */
export default function TestInboxTab() {
  const [page, setPage] = useState(1);
  const [reloadKey, setReloadKey] = useState(0);
  const [reported, setReported] = useState<{ providerMessageId: string; message: NotificationMessageResponse; status: SimulatedStatus } | null>(null);
  const action = useNotificationAction();
  const result = useApi<PagedResponse<MockProviderMessageResponse>>(mockMessagesPath(page), reloadKey);

  async function report(providerMessageId: string, status: SimulatedStatus) {
    setReported(null);
    const body: SimulateStatusRequest = { status, error: status === 'Failed' ? 'Simulated failure from the test inbox' : null };
    const done = await action.run(() => api.post<NotificationMessageResponse>(notificationApi.mockStatus(providerMessageId), body));
    if (done.ok) setReported({ providerMessageId, message: done.value, status });
  }

  return (
    <div className="space-y-4">
      <div role="note" className="rounded-xl border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900">
        <p className="font-semibold">Mock provider: nothing here was really sent.</p>
        <p className="mt-1">
          These are the messages our test provider caught instead of sending. Use the buttons to make it report back, as a real provider’s delivery report would, and watch the message change state.
        </p>
      </div>

      <div className="flex items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">Test inbox</h2>
        <SecondaryButton type="button" onClick={() => setReloadKey((k) => k + 1)} disabled={result.status === 'loading'}>
          Refresh
        </SecondaryButton>
      </div>

      <div aria-live="polite" className="space-y-3">
        {action.error && <Alert>{action.error}</Alert>}
        {reported && (
          <Alert tone="success">
            Reported “{reported.status.toLowerCase()}” for <span className="font-mono">{reported.providerMessageId}</span>. The message now shows:{' '}
            <strong>{describeMessageState(reported.message).label}</strong>.
          </Alert>
        )}
      </div>

      {(result.status === 'loading' || result.status === 'idle') && <p className="text-sm text-slate-500">Loading the test inbox…</p>}
      {result.status === 'error' && <Alert>{result.message}</Alert>}
      {result.status === 'loaded' && result.data.items.length === 0 && (
        <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">
          The test inbox is empty. Book a parcel (with updates switched on) to see a message arrive.
        </p>
      )}
      {result.status === 'loaded' && result.data.items.length > 0 && (
        <>
          <ul className="space-y-3">
            {result.data.items.map((m) => (
              <li key={m.id} className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-slate-500">
                  <span>
                    <span className="font-semibold text-slate-700">{channelLabel(m.channel)}</span> to <span className="font-mono">{m.toMasked}</span>
                  </span>
                  <span>{formatNepalDateTime(m.createdAtUtc)}</span>
                </div>
                <p className="mt-2 whitespace-pre-wrap rounded-lg bg-slate-50 p-3 text-sm text-slate-900">{m.body}</p>
                <p className="mt-2 break-all text-xs text-slate-500">
                  Message id <span className="font-mono">{m.providerMessageId}</span>
                  {m.templateName && (
                    <>
                      {' '}· Template <span className="font-mono">{m.templateName}</span>
                    </>
                  )}
                </p>
                {m.parameters.length > 0 && (
                  <p className="mt-1 text-xs text-slate-500">
                    Fields: <span className="font-mono">{m.parameters.map((p, i) => `{{${i + 1}}}=${p}`).join('  ')}</span>
                  </p>
                )}
                <div className="mt-3 flex flex-wrap items-center gap-2">
                  {SIMULATED_STATUSES.map((status) => (
                    <SecondaryButton key={status} type="button" disabled={action.busy} onClick={() => void report(m.providerMessageId, status)}>
                      {SIMULATE_LABELS[status]}
                    </SecondaryButton>
                  ))}
                  {reported?.providerMessageId === m.providerMessageId && <NotificationStateChip message={reported.message} />}
                </div>
              </li>
            ))}
          </ul>
          <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="message" onPage={setPage} />
        </>
      )}
    </div>
  );
}
