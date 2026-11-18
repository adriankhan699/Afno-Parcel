// The final tracking-number scheme is defined with the Parcels module (Phase 2). Until then the UI accepts
// 8–20 alphanumeric characters, ignoring spaces and dashes that customers often type.
const TRACKING_PATTERN = /^[A-Z0-9]{8,20}$/;

export function normalizeTrackingNumber(input: string): string {
  return input.replace(/[\s-]/g, '').toUpperCase();
}

export function isValidTrackingNumber(input: string): boolean {
  return TRACKING_PATTERN.test(normalizeTrackingNumber(input));
}
