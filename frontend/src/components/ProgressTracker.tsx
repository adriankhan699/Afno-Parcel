import { statusLabel } from '../lib/labels';
import { computeProgress, type StepState } from '../lib/status';

const circle: Record<StepState, string> = {
  done: 'bg-emerald-600 text-white',
  current: 'bg-brand-600 text-white ring-4 ring-brand-100',
  upcoming: 'bg-slate-200 text-slate-500',
};

const specialText: Record<string, string> = {
  DeliveryFailed: 'A delivery attempt was unsuccessful. The parcel is not lost: it will be tried again or returned.',
  Returned: 'This parcel was returned to the sender.',
  Cancelled: 'This booking was cancelled.',
};

interface ProgressTrackerProps {
  status: string;
  /** Every status the parcel has had, so a failed or cancelled parcel still shows how far it got. */
  historyStatuses: readonly string[];
}

/** The normal journey as a row of steps, plus a notice when the parcel left it (failed, returned, cancelled). */
export default function ProgressTracker({ status, historyStatuses }: ProgressTrackerProps) {
  const progress = computeProgress(status, historyStatuses);

  return (
    <div>
      <ol
        aria-label={`Progress: ${progress.percent}% of the way`}
        className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-7"
      >
        {progress.steps.map((step, index) => (
          <li key={step.status} className="flex items-center gap-2 text-sm" aria-current={step.state === 'current' ? 'step' : undefined}>
            <span
              aria-hidden
              className={`flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-xs font-bold ${circle[step.state]}`}
            >
              {step.state === 'done' ? '✓' : index + 1}
            </span>
            <span className={step.state === 'upcoming' ? 'text-slate-400' : 'font-medium text-slate-800'}>
              {step.label}
              <span className="sr-only">
                {step.state === 'done' ? ' (done)' : step.state === 'current' ? ' (current)' : ' (not yet)'}
              </span>
            </span>
          </li>
        ))}
      </ol>

      {progress.special && (
        <div
          role="status"
          className={`mt-4 rounded-lg p-3 text-sm ${progress.special === 'DeliveryFailed' ? 'bg-amber-50 text-amber-800' : 'bg-red-50 text-red-800'}`}
        >
          <strong>{statusLabel(progress.special)}.</strong> {specialText[progress.special]}
        </div>
      )}
    </div>
  );
}
