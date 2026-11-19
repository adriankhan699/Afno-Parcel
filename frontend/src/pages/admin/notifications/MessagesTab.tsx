import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';
import { Alert, PrimaryButton, SecondaryButton, SelectField, TextField } from '../../../components/form';
import { TableShell, Th, theadClass } from '../../../components/NetworkBits';
import { NotificationStateCell } from '../../../components/NotificationStateChip';
import Pagination from '../../../components/Pagination';
import { downloadTextFile } from '../../../lib/download';
import { formatNepalDateTime } from '../../../lib/format';
import {
  AUDIENCE_OPTIONS,
  CHANNEL_OPTIONS,
  DELIVERY_OPTIONS,
  STATUS_OPTIONS,
  attemptsLabel,
  channelLabel,
  emptyMessageFilters,
  filtersFromSearch,
  isRetrying,
  messagesCsv,
  messagesFileName,
  messagesPath,
  templateKeyLabel,
  validateDateRange,
  type MessageFilters,
} from '../../../lib/notification';
import { useApi } from '../../../lib/useApi';
import type { NotificationMessageResponse, PagedResponse } from '../../../types/api';
import MessageDetailDialog from './MessageDetailDialog';

/** Every message, newest first: filter, page through, open one to see its history, export the page. */
export default function MessagesTab({ canEdit }: { canEdit: boolean }) {
  const [searchParams] = useSearchParams();
  // The dead-letter link on the overview opens this tab already filtered; read the address once, when the tab opens.
  const [initial] = useState<MessageFilters>(() => filtersFromSearch(searchParams));
  const [draft, setDraft] = useState<MessageFilters>(initial);
  const [applied, setApplied] = useState<MessageFilters>(initial);
  const [page, setPage] = useState(1);
  const [rangeError, setRangeError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [openId, setOpenId] = useState<string | null>(null);

  const result = useApi<PagedResponse<NotificationMessageResponse>>(messagesPath(applied, page), reloadKey);
  const set = (patch: Partial<MessageFilters>) => setDraft((d) => ({ ...d, ...patch }));

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const problem = validateDateRange(draft.from, draft.to);
    setRangeError(problem);
    if (problem) return;
    setApplied(draft);
    setPage(1);
  }

  function handleReset() {
    setDraft(emptyMessageFilters());
    setApplied(emptyMessageFilters());
    setRangeError(null);
    setPage(1);
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-lg font-semibold">Messages</h2>
          <p className="mt-1 text-sm text-slate-600">
            <strong>Status</strong> is what our system has done with a message. <strong>Delivery</strong> is only what the provider reported afterwards. A message the provider accepted is
            not delivered until it says so.
          </p>
        </div>
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <SecondaryButton type="button" onClick={() => downloadTextFile(messagesFileName(result.data.page), messagesCsv(result.data.items))}>
            Download this page as CSV
          </SecondaryButton>
        )}
      </div>

      <form onSubmit={handleSubmit} role="search" aria-label="Filter messages" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <SelectField id="msg-channel" label="Channel" placeholder="Any channel" value={draft.channel} onChange={(channel) => set({ channel })} options={CHANNEL_OPTIONS} />
          <SelectField id="msg-status" label="Status (ours)" placeholder="Any status" value={draft.status} onChange={(status) => set({ status })} options={STATUS_OPTIONS} />
          <SelectField
            id="msg-delivery"
            label="Delivery (from the provider)"
            placeholder="Any"
            value={draft.deliveryStatus}
            onChange={(deliveryStatus) => set({ deliveryStatus })}
            options={DELIVERY_OPTIONS}
          />
          <SelectField id="msg-audience" label="Audience" placeholder="Sender or receiver" value={draft.audience} onChange={(audience) => set({ audience })} options={AUDIENCE_OPTIONS} />
          <TextField id="msg-template" label="Template" placeholder="e.g. booking-created" value={draft.templateKey} onChange={(templateKey) => set({ templateKey })} />
          <TextField id="msg-search" label="Tracking number" type="search" placeholder="Tracking number" value={draft.search} onChange={(search) => set({ search })} />
          <TextField id="msg-from" label="From" type="date" value={draft.from} onChange={(from) => set({ from })} />
          <TextField id="msg-to" label="To" type="date" value={draft.to} onChange={(to) => set({ to })} error={rangeError ?? undefined} />
        </div>
        <div className="mt-4 flex gap-2">
          <PrimaryButton type="submit">Apply filters</PrimaryButton>
          <SecondaryButton type="button" onClick={handleReset}>
            Clear
          </SecondaryButton>
        </div>
      </form>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading messages…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No messages match these filters.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <caption className="sr-only">Notification messages, page {result.data.page}</caption>
              <thead className={theadClass}>
                <tr>
                  <Th>Created</Th>
                  <Th>Message</Th>
                  <Th>To</Th>
                  <Th>State</Th>
                  <Th>Provider message id</Th>
                  <Th>Attempts</Th>
                  <Th>
                    <span className="sr-only">Details</span>
                  </Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((m) => (
                  <tr key={m.id} className="cursor-pointer hover:bg-slate-50" onClick={() => setOpenId(m.id)}>
                    <td className="whitespace-nowrap px-4 py-3 text-slate-700">{formatNepalDateTime(m.createdAtUtc)}</td>
                    <td className="px-4 py-3">
                      <p className="font-medium text-slate-900">{templateKeyLabel(m.templateKey)}</p>
                      <p className="text-xs text-slate-500">
                        {channelLabel(m.channel)} · {m.audience}
                        {m.trackingNumber && <> · <span className="font-mono">{m.trackingNumber}</span></>}
                      </p>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-700">{m.recipientMasked}</td>
                    <td className="px-4 py-3">
                      <NotificationStateCell message={m} />
                    </td>
                    <td className="px-4 py-3 font-mono text-xs text-slate-700">
                      <span className="block max-w-40 truncate" title={m.providerMessageId ?? undefined}>
                        {m.providerMessageId ?? '—'}
                      </span>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-slate-700">
                      {attemptsLabel(m)}
                      {isRetrying(m) && <p className="text-xs text-amber-800">next {formatNepalDateTime(m.nextAttemptAtUtc)}</p>}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <button
                        type="button"
                        className="text-sm font-medium text-brand-700 underline"
                        onClick={(event) => {
                          event.stopPropagation();
                          setOpenId(m.id);
                        }}
                      >
                        Details
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="message" onPage={setPage} />
          </>
        )}
      </div>

      <MessageDetailDialog messageId={openId} canEdit={canEdit} onClose={() => setOpenId(null)} onChanged={() => setReloadKey((k) => k + 1)} />
    </div>
  );
}
