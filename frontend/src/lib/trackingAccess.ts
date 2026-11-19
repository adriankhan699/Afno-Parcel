import type { TokenStorage } from './auth';

// A receiver who proves they own a parcel with a one-time code gets a short-lived access token for that one tracking
// number. It is kept in memory and in sessionStorage (gone when the tab closes), keyed by tracking number, and
// dropped as soon as it expires. It is never shown on screen.
const KEY_PREFIX = 'afno.trackingAccess.';

export const OTP_LENGTH = 6;

interface StoredAccess {
  token: string;
  expiresAtUtc: string;
}

function browserStorage(): TokenStorage | null {
  try {
    return typeof sessionStorage === 'undefined' ? null : sessionStorage;
  } catch {
    return null;
  }
}

const memory = new Map<string, StoredAccess>();

const keyFor = (trackingNumber: string) => `${KEY_PREFIX}${trackingNumber}`;

function isFresh(access: StoredAccess, now: Date): boolean {
  const expires = Date.parse(access.expiresAtUtc);
  return Number.isFinite(expires) && expires > now.getTime();
}

export function storeTrackingAccess(
  trackingNumber: string,
  token: string,
  expiresAtUtc: string,
  storage: TokenStorage | null = browserStorage(),
): void {
  const access: StoredAccess = { token, expiresAtUtc };
  memory.set(trackingNumber, access);
  try {
    storage?.setItem(keyFor(trackingNumber), JSON.stringify(access));
  } catch {
    // storage unavailable: the in-memory copy still works until the page is reloaded
  }
}

/** The stored token for this number, or null when there is none or it has expired (expired ones are removed). */
export function readTrackingAccess(
  trackingNumber: string,
  now: Date = new Date(),
  storage: TokenStorage | null = browserStorage(),
): string | null {
  let access = memory.get(trackingNumber) ?? null;
  if (!access) {
    try {
      const raw = storage?.getItem(keyFor(trackingNumber));
      if (raw) access = JSON.parse(raw) as StoredAccess;
    } catch {
      access = null;
    }
  }

  if (!access || typeof access.token !== 'string' || !isFresh(access, now)) {
    clearTrackingAccess(trackingNumber, storage);
    return null;
  }
  return access.token;
}

export function clearTrackingAccess(trackingNumber: string, storage: TokenStorage | null = browserStorage()): void {
  memory.delete(trackingNumber);
  try {
    storage?.removeItem(keyFor(trackingNumber));
  } catch {
    // ignore
  }
}

/** Keeps only digits, so "123 456" typed or pasted works. */
export const normalizeOtp = (input: string): string => input.replace(/\D/g, '').slice(0, OTP_LENGTH);

export const isValidOtp = (input: string): boolean => normalizeOtp(input).length === OTP_LENGTH;
