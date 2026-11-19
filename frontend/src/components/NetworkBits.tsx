import { barWidth, formatWaiting, utilisation, utilisationTone, waitLevel, waitTone } from '../lib/network';

export function ActiveBadge({ active }: { active: boolean }) {
  return (
    <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${active ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-200 text-slate-600'}`}>
      {active ? 'Active' : 'Inactive'}
    </span>
  );
}

/** How long a parcel has been waiting; a day or more is highlighted, two days or more is flagged late. */
export function WaitBadge({ hours }: { hours: number }) {
  const level = waitLevel(hours);
  return (
    <span
      className={`whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-semibold ${waitTone(level)}`}
      title={level === 'normal' ? undefined : level === 'late' ? 'Waiting two days or more' : 'Waiting a day or more'}
    >
      {formatWaiting(hours)}
    </span>
  );
}

/** "12 / 500" with a small bar showing how full the warehouse is. */
export function CapacityBar({ held, capacity }: { held: number; capacity?: number | null }) {
  const percent = utilisation(held, capacity);
  if (percent === null) {
    return <span className="text-slate-700">{held} held · no capacity set</span>;
  }
  return (
    <div className="min-w-32">
      <p className="text-slate-700">
        {held} / {capacity} <span className="text-xs text-slate-500">({percent}%)</span>
      </p>
      <div
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(barWidth(percent))}
        aria-label="Warehouse capacity in use"
        className="mt-1 h-1.5 w-full overflow-hidden rounded-full bg-slate-200"
      >
        <div className={`h-full ${utilisationTone(percent)}`} style={{ width: `${barWidth(percent)}%` }} />
      </div>
    </div>
  );
}

export function Th({ children, right = false }: { children?: React.ReactNode; right?: boolean }) {
  return (
    <th scope="col" className={`px-4 py-3 ${right ? 'text-right' : ''}`}>
      {children}
    </th>
  );
}

export function TableShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="overflow-x-auto rounded-xl border border-slate-200 bg-white shadow-sm">
      <table className="min-w-full text-left text-sm">{children}</table>
    </div>
  );
}

export const theadClass = 'border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wide text-slate-500';
