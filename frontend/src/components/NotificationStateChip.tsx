import {
  PROGRESS_STEPS,
  STATE_TONE_CLASSES,
  describeMessageState,
  messageNote,
  stepIndex,
  type MessageStateInput,
} from '../lib/notification';
import type { NotificationMessageResponse } from '../types/api';

/**
 * The state of one message as a chip. The wording comes from `describeMessageState`, which keeps our own processing status
 * apart from what the provider reported: a message the provider only accepted is never shown as delivered.
 */
export default function NotificationStateChip({ message }: { message: MessageStateInput }) {
  const state = describeMessageState(message);
  return (
    <span
      className={`inline-block rounded-full px-2.5 py-0.5 text-xs font-semibold ${STATE_TONE_CLASSES[state.tone]}`}
      title={`Processing status: ${message.status}. Provider report: ${message.deliveryStatus}.`}
    >
      {state.label}
    </span>
  );
}

/** The chip with the one line that explains a skipped or failed message underneath. */
export function NotificationStateCell({ message }: { message: NotificationMessageResponse }) {
  const note = messageNote(message);
  return (
    <div>
      <NotificationStateChip message={message} />
      {note && <p className="mt-1 max-w-64 text-xs text-slate-600">{note}</p>}
    </div>
  );
}

/** Queued → Submitted → Sent → Delivered → Read, with the steps reached so far filled in. A message that stopped shows no path. */
export function NotificationSteps({ message }: { message: MessageStateInput }) {
  const state = describeMessageState(message);
  const reached = stepIndex(state.step);
  if (reached < 0) return null;
  return (
    <ol aria-label="Progress" className="flex flex-wrap items-center gap-1 text-xs">
      {PROGRESS_STEPS.map((step, index) => {
        const done = index <= reached;
        return (
          <li key={step} className="flex items-center gap-1">
            <span
              className={`rounded-full px-2 py-0.5 font-medium ${
                done ? (index === reached ? 'bg-brand-600 text-white' : 'bg-brand-50 text-brand-700') : 'bg-slate-100 text-slate-400'
              }`}
              aria-current={index === reached ? 'step' : undefined}
            >
              {step}
            </span>
            {index < PROGRESS_STEPS.length - 1 && (
              <span aria-hidden className="text-slate-300">
                →
              </span>
            )}
          </li>
        );
      })}
    </ol>
  );
}
