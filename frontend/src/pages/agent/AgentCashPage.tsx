import { useState } from 'react';
import { InfoRow, BigButton, Panel } from '../../components/agent/AgentBits';
import ConfirmDialog from '../../components/ConfirmDialog';
import { Alert } from '../../components/form';
import Pagination from '../../components/Pagination';
import { SettlementStatusChip } from '../../components/SettlementDialogs';
import { api } from '../../lib/apiClient';
import { buildDeliveryQuery } from '../../lib/delivery';
import { formatNepalDateTime, formatNpr } from '../../lib/format';
import { buildCreateSettlementRequest, canHandIn, handInMessage, varianceText, varianceTone } from '../../lib/payment';
import { paymentApi } from '../../lib/paymentApi';
import { useStableApi } from '../../lib/useStableApi';
import type { AgentCashSummaryResponse, PagedResponse, SettlementResponse } from '../../types/api';

function Stat({ label, value, hint, tone = 'text-slate-900' }: { label: string; value: number; hint: string; tone?: string }) {
  return (
    <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
      <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</p>
      <p className={`mt-1 text-xl font-bold tabular-nums ${tone}`}>{formatNpr(value)}</p>
      <p className="mt-0.5 text-xs text-slate-500">{hint}</p>
    </div>
  );
}

/** The agent's cash: what is in their pocket, what is waiting to be counted, and a way to hand it in. */
export default function AgentCashPage() {
  const [reloadKey, setReloadKey] = useState(0);
  const [page, setPage] = useState(1);
  const [confirming, setConfirming] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const summary = useStableApi<AgentCashSummaryResponse>(paymentApi.myCash, reloadKey);
  const history = useStableApi<PagedResponse<SettlementResponse>>(`${paymentApi.mySettlements}${buildDeliveryQuery({ page, pageSize: 10 })}`, reloadKey);
  const position = summary.data?.position;

  async function handIn() {
    const created = await api.post<SettlementResponse>(paymentApi.mySettlements, buildCreateSettlementRequest(''));
    setNotice(`Handed in ${formatNpr(created.totalAmount)} (${created.referenceNumber}). The accountant will count it.`);
    setPage(1);
    setReloadKey((k) => k + 1);
  }

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold tracking-tight">Cash</h1>

      {summary.error && <Alert>{summary.error}</Alert>}
      {summary.loading && <p className="text-base text-slate-500">Loading…</p>}
      {notice && <Alert tone="success">{notice}</Alert>}

      {position && (
        <>
          <div className="grid grid-cols-2 gap-3">
            <Stat label="Cash in hand" value={position.inHand} hint="collected, not handed in" tone={position.inHand > 0 ? 'text-amber-800' : 'text-slate-900'} />
            <Stat label="Waiting for count" value={position.inSettlement} hint="handed in to the accountant" />
            <Stat label="Disputed" value={position.disputed} hint="the count did not match" tone={position.disputed > 0 ? 'text-red-700' : 'text-slate-900'} />
            <Stat label="Settled" value={position.settled} hint="counted and accepted" tone="text-emerald-700" />
          </div>

          <BigButton disabled={!canHandIn(position)} onClick={() => setConfirming(true)}>
            {canHandIn(position) ? `Hand in ${formatNpr(position.inHand)}` : 'No cash to hand in'}
          </BigButton>
          <p className="text-sm text-slate-500">Hand in the cash at your branch after you press the button. The accountant counts it and either accepts it or marks any difference.</p>

          <Panel title="Totals">
            <dl className="divide-y divide-slate-100">
              <InfoRow label="Collected in total">{formatNpr(position.collected)}</InfoRow>
              <InfoRow label="Collections">{position.collectionCount}</InfoRow>
              {position.lastCollectionAtUtc && <InfoRow label="Last collection">{formatNepalDateTime(position.lastCollectionAtUtc)}</InfoRow>}
            </dl>
          </Panel>
        </>
      )}

      <h2 className="pt-2 text-lg font-semibold">Your hand-ins</h2>
      <div aria-live="polite" className="space-y-3">
        {history.error && <Alert>{history.error}</Alert>}
        {history.data && history.data.items.length === 0 && (
          <Panel>
            <p className="text-center text-base text-slate-600">You have not handed in any cash yet.</p>
          </Panel>
        )}
        {history.data?.items.map((s) => (
          <Panel key={s.id}>
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <p className="font-mono text-sm font-semibold text-slate-900">{s.referenceNumber}</p>
                <p className="text-xs text-slate-500">
                  {s.submittedAtUtc ? formatNepalDateTime(s.submittedAtUtc) : formatNepalDateTime(s.createdAtUtc)} · {s.collectionCount} collection{s.collectionCount === 1 ? '' : 's'}
                </p>
              </div>
              <SettlementStatusChip status={s.status} />
            </div>
            <dl className="mt-2 divide-y divide-slate-100">
              <InfoRow label="Handed in">{formatNpr(s.totalAmount)}</InfoRow>
              {s.countedAmount !== null && s.countedAmount !== undefined && <InfoRow label="Counted">{formatNpr(s.countedAmount)}</InfoRow>}
              {s.variance !== null && s.variance !== undefined && s.variance !== 0 && (
                <InfoRow label="Difference">
                  <span className={varianceTone(s.variance)}>{varianceText(s.variance)}</span>
                </InfoRow>
              )}
            </dl>
            {s.remarks && <p className="mt-2 whitespace-pre-wrap text-sm text-slate-600">{s.remarks}</p>}
          </Panel>
        ))}
      </div>
      {history.data && history.data.totalCount > 0 && (
        <Pagination page={history.data.page} totalPages={history.data.totalPages} totalCount={history.data.totalCount} hasNextPage={history.data.hasNextPage} noun="hand-in" onPage={setPage} />
      )}

      <ConfirmDialog
        open={confirming}
        title="Hand in your cash?"
        message={handInMessage(position?.inHand ?? 0)}
        confirmLabel="Yes, hand it in"
        onConfirm={handIn}
        onClose={() => setConfirming(false)}
      />
    </div>
  );
}
