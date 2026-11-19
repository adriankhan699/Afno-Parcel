import { formatNepalDateTime } from '../lib/format';
import type { TimelineItem } from '../lib/status';

interface TimelineProps {
  items: readonly TimelineItem[];
  /** Which row is highlighted as "latest": the first (newest-first lists) or the last (oldest-first lists). */
  latest: 'first' | 'last';
  /** Accessible name for the list. */
  label: string;
}

/** The complete history of a parcel as a vertical timeline. History is append-only, so every step ever taken is here. */
export default function Timeline({ items, latest, label }: TimelineProps) {
  if (items.length === 0) {
    return <p className="text-sm text-slate-500">No history yet.</p>;
  }

  const latestIndex = latest === 'first' ? 0 : items.length - 1;

  return (
    <ol aria-label={label} className="relative ml-2 border-l border-slate-200">
      {items.map((item, index) => (
        <li key={item.key} className="mb-4 ml-4 last:mb-0">
          <span
            aria-hidden
            className={`absolute -left-1.5 mt-1.5 h-3 w-3 rounded-full ${index === latestIndex ? 'bg-brand-600' : 'bg-slate-300'}`}
          />
          <p className="font-medium text-slate-900">{item.title}</p>
          {item.transition && <p className="text-xs text-slate-500">{item.transition}</p>}
          <p className="text-xs text-slate-500">
            <time dateTime={item.occurredAtUtc}>{formatNepalDateTime(item.occurredAtUtc)}</time>
            {item.location && <span> · {item.location}</span>}
            {item.changedBy && <span> · by {item.changedBy}</span>}
          </p>
          {item.message && <p className="mt-0.5 text-sm text-slate-600">{item.message}</p>}
          {item.remarks && <p className="mt-0.5 text-sm text-slate-600">Note: {item.remarks}</p>}
        </li>
      ))}
    </ol>
  );
}
