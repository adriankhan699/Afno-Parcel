// Tracking numbers look like AFNK7M2Q9XT4B3: the prefix "AFN", ten random characters and one check character, all
// from an alphabet without the look-alikes 0/O and 1/I/L. This is a format check only; the server verifies the
// check character and whether the parcel exists.
export const TRACKING_PREFIX = 'AFN';
const TRACKING_PATTERN = /^AFN[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{11}$/;

export function normalizeTrackingNumber(input: string): string {
  return input.replace(/[\s-]/g, '').toUpperCase();
}

export function isValidTrackingNumber(input: string): boolean {
  return TRACKING_PATTERN.test(normalizeTrackingNumber(input));
}
