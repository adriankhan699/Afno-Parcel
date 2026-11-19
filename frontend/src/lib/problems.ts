import type { ProblemDetails } from '../types/api';

/** "Receiver.Address.District" → "receiver.address.district" (the form's field keys follow the API's property paths). */
export function toFieldKey(serverKey: string): string {
  return serverKey
    .split('.')
    .map((segment) => (segment ? segment.charAt(0).toLowerCase() + segment.slice(1) : segment))
    .join('.');
}

/** Maps a validation problem onto form fields. Only the first message per field is kept. */
export function problemToFieldErrors(problem: ProblemDetails | undefined): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(problem?.errors ?? {})) {
    const first = messages[0];
    if (first && key) {
      result[toFieldKey(key)] = first;
    }
  }
  return result;
}

/** A one-line summary shown above the form, whatever the kind of error. */
export function problemSummary(problem: ProblemDetails | undefined, fallback = 'Something went wrong. Please try again.'): string {
  if (!problem) return fallback;
  const fieldCount = Object.keys(problem.errors ?? {}).length;
  if (fieldCount > 0) {
    return fieldCount === 1
      ? 'Please fix the highlighted field and try again.'
      : `Please fix the ${fieldCount} highlighted fields and try again.`;
  }
  return problem.detail ?? problem.title ?? fallback;
}
