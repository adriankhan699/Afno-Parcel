import { useState } from 'react';
import { Link } from 'react-router';
import { Alert, PrimaryButton, SelectField, TextField } from '../../../components/form';
import { TableShell, Th, theadClass } from '../../../components/NetworkBits';
import {
  CHANNEL_LABELS,
  DEAD_LETTER_LINK,
  DEFAULT_PERIOD_DAYS,
  PERIOD_PRESETS,
  lastDaysRange,
  summaryPath,
  validateDateRange,
} from '../../../lib/notification';
import { notificationApi } from '../../../lib/notificationApi';
import { useApi } from '../../../lib/useApi';
import type { NotificationProviderInfo, NotificationSummaryResponse } from '../../../types/api';

const CUSTOM = 'custom';

function ProviderCard({ provider }: { provider: NotificationProviderInfo }) {
  return (
    <li className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-sm font-semibold text-slate-900">{CHANNEL_LABELS[provider.channel] ?? provider.channel}</h3>
        <div className="flex flex-wrap gap-1">
          {provider.isMock && <span className="rounded-full bg-amber-100 px-2 py-0.5 text-xs font-semibold text-amber-800">MOCK</span>}
          <span
            className={`rounded-full px-2 py-0.5 text-xs font-semibold ${provider.isConfigured ? 'bg-emerald-50 text-emerald-700' : 'bg-red-50 text-red-700'}`}
          >
            {provider.isConfigured ? 'Configured' : 'Not configured'}
          </span>
        </div>
      </div>
      <p className="mt-2 text-sm text-slate-700">
        Provider: <span className="font-medium">{provider.provider}</span>
      </p>
      <p className="mt-1 text-xs text-slate-500">{provider.detail}</p>
      {provider.isMock && <p className="mt-2 text-xs text-amber-800">Messages through a mock provider are not really sent.</p>}
    </li>
  );
}

/** Which providers are in use, and how the messages of a period turned out, per channel. */
export default function OverviewTab() {
  const [now] = useState(() => new Date());
  const [preset, setPreset] = useState(String(DEFAULT_PERIOD_DAYS));
  const [draft, setDraft] = useState({ from: '', to: '' });
  const [applied, setApplied] = useState<{ from: string; to: string } | null>(null);
  const [rangeError, setRangeError] = useState<string | null>(null);

  const days = PERIOD_PRESETS.find((p) => p.value === preset)?.days;
  const range = preset === CUSTOM ? applied : lastDaysRange(days ?? DEFAULT_PERIOD_DAYS, now);

  const providers = useApi<NotificationProviderInfo[]>(notificationApi.providers);
  const summary = useApi<NotificationSummaryResponse>(range ? summaryPath(range.from, range.to) : null);

  function applyCustom() {
    if (!draft.from || !draft.to) {
      setRangeError('Choose both dates.');
      return;
    }
    const problem = validateDateRange(draft.from, draft.to);
    setRangeError(problem);
    if (!problem) setApplied({ from: draft.from, to: draft.to });
  }

  return (
    <div className="space-y-8">
      <section aria-labelledby="providers-heading" className="space-y-3">
        <h2 id="providers-heading" className="text-lg font-semibold">
          Providers
        </h2>
        {(providers.status === 'loading' || providers.status === 'idle') && <p className="text-sm text-slate-500">Loading providers…</p>}
        {providers.status === 'error' && <Alert>{providers.message}</Alert>}
        {providers.status === 'loaded' && (
          <ul className="grid gap-4 sm:grid-cols-2">
            {providers.data.map((p) => (
              <ProviderCard key={p.channel} provider={p} />
            ))}
          </ul>
        )}
      </section>

      <section aria-labelledby="summary-heading" className="space-y-4">
        <div className="flex flex-wrap items-end justify-between gap-4">
          <div>
            <h2 id="summary-heading" className="text-lg font-semibold">
              Messages in the period
            </h2>
            <p className="mt-1 text-sm text-slate-600">Messages created in the period, counted by what has happened to them so far.</p>
          </div>
          <SelectField
            id="period"
            label="Period"
            className="w-48"
            value={preset}
            onChange={setPreset}
            options={[...PERIOD_PRESETS.map((p) => ({ value: p.value, label: p.label })), { value: CUSTOM, label: 'Choose dates…' }]}
          />
        </div>

        {preset === CUSTOM && (
          <div className="flex flex-wrap items-end gap-3 rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <TextField id="period-from" label="From" type="date" value={draft.from} onChange={(from) => setDraft((d) => ({ ...d, from }))} />
            <TextField id="period-to" label="To" type="date" value={draft.to} onChange={(to) => setDraft((d) => ({ ...d, to }))} error={rangeError ?? undefined} />
            <PrimaryButton type="button" onClick={applyCustom}>
              Show
            </PrimaryButton>
          </div>
        )}

        <div aria-live="polite">
          {range === null && <p className="text-sm text-slate-500">Choose the dates to see the numbers.</p>}
          {range !== null && (summary.status === 'loading' || summary.status === 'idle') && <p className="text-sm text-slate-500">Loading numbers…</p>}
          {summary.status === 'error' && <Alert>{summary.message}</Alert>}
          {summary.status === 'loaded' && (
            <div className="space-y-4">
              <p className="text-sm text-slate-600">
                {summary.data.from} to {summary.data.to} (Nepal days)
              </p>
              <TableShell>
                <caption className="sr-only">Messages per channel from {summary.data.from} to {summary.data.to}</caption>
                <thead className={theadClass}>
                  <tr>
                    <Th>Channel</Th>
                    <Th right>Total</Th>
                    <Th right>Queued</Th>
                    <Th right>Retrying</Th>
                    <Th right>Submitted</Th>
                    <Th right>Sent</Th>
                    <Th right>Delivered</Th>
                    <Th right>Read</Th>
                    <Th right>Failed</Th>
                    <Th right>Skipped</Th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 tabular-nums">
                  {summary.data.channels.length === 0 && (
                    <tr>
                      <td colSpan={10} className="px-4 py-6 text-center text-slate-600">
                        No messages were created in this period.
                      </td>
                    </tr>
                  )}
                  {summary.data.channels.map((c) => (
                    <tr key={c.channel}>
                      <th scope="row" className="px-4 py-3 font-medium text-slate-900">
                        {CHANNEL_LABELS[c.channel] ?? c.channel}
                      </th>
                      <td className="px-4 py-3 text-right">{c.total}</td>
                      <td className="px-4 py-3 text-right">{c.queued}</td>
                      <td className="px-4 py-3 text-right">{c.retrying}</td>
                      <td className="px-4 py-3 text-right">{c.submitted}</td>
                      <td className="px-4 py-3 text-right">{c.sent}</td>
                      <td className="px-4 py-3 text-right">{c.delivered}</td>
                      <td className="px-4 py-3 text-right">{c.read}</td>
                      <td className="px-4 py-3 text-right">{c.failed}</td>
                      <td className="px-4 py-3 text-right">{c.skipped}</td>
                    </tr>
                  ))}
                </tbody>
              </TableShell>
              <p className="rounded-lg bg-blue-50 p-3 text-sm text-blue-800">
                <strong>Submitted</strong> only means the provider accepted the message and has not reported on it yet. It is not delivered until the provider says so
                (<strong>Delivered</strong> includes the messages that were also read). <strong>Retrying</strong> messages failed and will be tried again by themselves.
              </p>

              <div
                className={`flex flex-wrap items-center justify-between gap-3 rounded-xl border p-4 shadow-sm ${
                  summary.data.deadLetters > 0 ? 'border-red-200 bg-red-50' : 'border-slate-200 bg-white'
                }`}
              >
                <div>
                  <p className="text-sm font-semibold text-slate-900">Given up on (dead letters)</p>
                  <p className="mt-1 text-sm text-slate-600">Messages that failed on every attempt. Nobody will be told about them unless a super admin retries them.</p>
                </div>
                <div className="flex items-center gap-4">
                  <p className="text-3xl font-bold tabular-nums text-slate-900">{summary.data.deadLetters}</p>
                  {summary.data.deadLetters > 0 && (
                    <Link to={DEAD_LETTER_LINK} className="text-sm font-medium text-brand-700 underline">
                      Show them
                    </Link>
                  )}
                </div>
              </div>
            </div>
          )}
        </div>
      </section>
    </div>
  );
}
