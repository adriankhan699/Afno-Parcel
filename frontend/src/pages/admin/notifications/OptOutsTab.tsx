import { useState, type FormEvent } from 'react';
import ConfirmDialog from '../../../components/ConfirmDialog';
import { Alert, PrimaryButton, SelectField, TextField } from '../../../components/form';
import { TableShell, Th, theadClass } from '../../../components/NetworkBits';
import Pagination from '../../../components/Pagination';
import { api } from '../../../lib/apiClient';
import { formatNepalDateTime } from '../../../lib/format';
import {
  CHANNEL_LABELS,
  CHANNEL_OPTIONS,
  buildOptOutRequest,
  emptyOptOutFilters,
  humanize,
  optOutsPath,
  validateOptOutPhone,
  type OptOutFilters,
} from '../../../lib/notification';
import { notificationApi } from '../../../lib/notificationApi';
import { useApi } from '../../../lib/useApi';
import type { CreateOptOutRequest, OptOutResponse, PagedResponse } from '../../../types/api';

/** The phone numbers that must not be messaged on a channel (for example after replying STOP). Only a super admin can change the list. */
export default function OptOutsTab({ canEdit }: { canEdit: boolean }) {
  const [filters, setFilters] = useState<OptOutFilters>(emptyOptOutFilters);
  const [page, setPage] = useState(1);
  const [reloadKey, setReloadKey] = useState(0);
  const result = useApi<PagedResponse<OptOutResponse>>(optOutsPath(filters, page), reloadKey);

  const [phone, setPhone] = useState('');
  const [channel, setChannel] = useState('');
  const [phoneError, setPhoneError] = useState<string | undefined>();
  const [channelError, setChannelError] = useState<string | undefined>();
  const [toAdd, setToAdd] = useState<CreateOptOutRequest | null>(null);
  const [toRemove, setToRemove] = useState<OptOutResponse | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  function handleAdd(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setNotice(null);
    const phoneProblem = validateOptOutPhone(phone);
    const channelProblem = channel ? undefined : 'Choose the channel.';
    setPhoneError(phoneProblem ?? undefined);
    setChannelError(channelProblem);
    if (phoneProblem || channelProblem) return;
    setToAdd(buildOptOutRequest(phone, channel));
  }

  const reload = () => setReloadKey((k) => k + 1);

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold">Opt-outs</h2>
        <p className="mt-1 text-sm text-slate-600">
          Phone numbers that asked not to be messaged on a channel, for example by replying STOP. Nothing is sent to them on that channel while they are listed.
        </p>
      </div>

      {canEdit && (
        <form onSubmit={handleAdd} noValidate aria-label="Add an opt-out" className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <h3 className="text-sm font-semibold text-slate-900">Stop messaging a number</h3>
          <div className="mt-3 grid items-start gap-4 sm:grid-cols-3">
            <TextField id="optout-phone" label="Phone number" type="tel" inputMode="tel" placeholder="+9779812345678" value={phone} onChange={setPhone} error={phoneError} />
            <SelectField id="optout-channel" label="Channel" placeholder="Choose a channel" value={channel} onChange={setChannel} options={CHANNEL_OPTIONS} error={channelError} />
            <div className="sm:pt-6">
              <PrimaryButton type="submit">Add opt-out…</PrimaryButton>
            </div>
          </div>
        </form>
      )}

      {notice && <Alert tone="success">{notice}</Alert>}

      <div className="flex flex-wrap items-end gap-4">
        <SelectField
          id="optout-filter-channel"
          label="Channel"
          className="w-48"
          placeholder="Any channel"
          value={filters.channel}
          onChange={(value) => {
            setFilters((f) => ({ ...f, channel: value }));
            setPage(1);
          }}
          options={CHANNEL_OPTIONS}
        />
        <label className="flex items-center gap-2 pb-2 text-sm text-slate-700">
          <input
            type="checkbox"
            checked={!filters.activeOnly}
            onChange={(event) => {
              setFilters((f) => ({ ...f, activeOnly: !event.target.checked }));
              setPage(1);
            }}
            className="h-4 w-4 accent-brand-600"
          />
          Also show the ones that were removed
        </label>
      </div>

      <div aria-live="polite">
        {result.status === 'loading' && <p className="text-sm text-slate-500">Loading opt-outs…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.items.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">No opt-outs to show.</p>
        )}
        {result.status === 'loaded' && result.data.items.length > 0 && (
          <>
            <TableShell>
              <caption className="sr-only">Phone numbers that opted out, page {result.data.page}</caption>
              <thead className={theadClass}>
                <tr>
                  <Th>Phone</Th>
                  <Th>Channel</Th>
                  <Th>How</Th>
                  <Th>Since</Th>
                  <Th>Status</Th>
                  {canEdit && (
                    <Th>
                      <span className="sr-only">Actions</span>
                    </Th>
                  )}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {result.data.items.map((o) => (
                  <tr key={o.id} className="hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-800">{o.phoneMasked}</td>
                    <td className="px-4 py-3">{CHANNEL_LABELS[o.channel] ?? o.channel}</td>
                    <td className="px-4 py-3 text-slate-700">{humanize(o.source)}</td>
                    <td className="whitespace-nowrap px-4 py-3 text-slate-700">{formatNepalDateTime(o.createdAtUtc)}</td>
                    <td className="px-4 py-3">
                      {o.removedAtUtc ? (
                        <span className="text-slate-600">Removed {formatNepalDateTime(o.removedAtUtc)}</span>
                      ) : (
                        <span className="rounded-full bg-red-50 px-2 py-0.5 text-xs font-semibold text-red-700">Blocked</span>
                      )}
                    </td>
                    {canEdit && (
                      <td className="px-4 py-3 text-right">
                        {!o.removedAtUtc && (
                          <button type="button" className="text-sm font-medium text-brand-700 underline" onClick={() => setToRemove(o)}>
                            Remove
                          </button>
                        )}
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <Pagination page={result.data.page} totalPages={result.data.totalPages} totalCount={result.data.totalCount} hasNextPage={result.data.hasNextPage} noun="opt-out" onPage={setPage} />
          </>
        )}
      </div>

      <ConfirmDialog
        open={toAdd !== null}
        title="Stop messaging this number?"
        message={
          toAdd
            ? `No ${CHANNEL_LABELS[toAdd.channel]} messages will be sent to ${toAdd.phoneNumber} until the opt-out is removed.`
            : ''
        }
        confirmLabel="Add opt-out"
        onConfirm={async () => {
          if (!toAdd) return;
          await api.post<OptOutResponse>(notificationApi.optOuts, toAdd);
          setNotice(`${CHANNEL_LABELS[toAdd.channel]} messages to ${toAdd.phoneNumber} are now blocked.`);
          setPhone('');
          setChannel('');
          setPage(1);
          reload();
        }}
        onClose={() => setToAdd(null)}
      />
      <ConfirmDialog
        open={toRemove !== null}
        title="Allow messages to this number again?"
        message={
          toRemove
            ? `${CHANNEL_LABELS[toRemove.channel] ?? toRemove.channel} messages may be sent to ${toRemove.phoneMasked} again. Only remove an opt-out if the person asked to hear from us again.`
            : ''
        }
        confirmLabel="Remove opt-out"
        onConfirm={async () => {
          if (!toRemove) return;
          await api.delete(notificationApi.optOut(toRemove.id));
          setNotice(`The opt-out for ${toRemove.phoneMasked} was removed.`);
          reload();
        }}
        onClose={() => setToRemove(null)}
      />
    </div>
  );
}
