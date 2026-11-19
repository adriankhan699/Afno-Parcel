import { Link } from 'react-router';
import { formatNepalDateTime, formatNpr } from '../../lib/format';
import type { AgentAssignmentResponse } from '../../types/api';
import { CodBadge, StatusChip } from './AgentBits';

/** One assignment in a list: tap anywhere on the card to open it. */
export function AgentAssignmentCard({ assignment: a, showFinished = false }: { assignment: AgentAssignmentResponse; showFinished?: boolean }) {
  return (
    <Link
      to={`/agent/assignments/${a.id}`}
      className="block rounded-2xl border border-slate-200 bg-white p-4 shadow-sm active:bg-slate-50"
    >
      <div className="flex items-start justify-between gap-2">
        <p className="font-mono text-base font-bold text-slate-900">{a.trackingNumber}</p>
        <StatusChip status={a.status} />
      </div>
      <p className="mt-2 text-lg font-semibold text-slate-900">{a.receiverName}</p>
      <p className="text-base text-slate-600">{a.receiverArea}</p>
      <div className="mt-3 flex flex-wrap items-center gap-2">
        <CodBadge amount={a.codAmount} />
        {a.attemptNumber > 1 && <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-700">Attempt {a.attemptNumber}</span>}
        <span className="text-xs text-slate-500">{a.quantity === 1 ? '1 piece' : `${a.quantity} pieces`}</span>
      </div>
      {showFinished && (
        <div className="mt-2 text-sm text-slate-600">
          {a.completedAtUtc && <p>{formatNepalDateTime(a.completedAtUtc)}</p>}
          {a.failureReason && <p className="text-red-700">{a.failureReason}</p>}
          {a.codCollectedAmount ? <p>Cash collected {formatNpr(a.codCollectedAmount)}</p> : null}
        </div>
      )}
    </Link>
  );
}
