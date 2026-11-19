import { beforeEach, describe, expect, it } from 'vitest';
import type { TokenStorage } from './auth';
import {
  OTP_LENGTH,
  clearTrackingAccess,
  isValidOtp,
  normalizeOtp,
  readTrackingAccess,
  storeTrackingAccess,
} from './trackingAccess';

function fakeStorage(): TokenStorage & { data: Map<string, string> } {
  const data = new Map<string, string>();
  return {
    data,
    getItem: (key) => data.get(key) ?? null,
    setItem: (key, value) => void data.set(key, value),
    removeItem: (key) => void data.delete(key),
  };
}

const NOW = new Date('2026-11-18T10:00:00Z');
const LATER = '2026-11-18T10:15:00Z';
const EARLIER = '2026-11-18T09:59:00Z';

describe('tracking access tokens', () => {
  let storage: ReturnType<typeof fakeStorage>;
  beforeEach(() => {
    storage = fakeStorage();
    clearTrackingAccess('AFNK7M2Q9XT4B3', storage);
    clearTrackingAccess('AFNQ2W3E4R5T6Y', storage);
  });

  it('returns the token while it is still valid', () => {
    storeTrackingAccess('AFNK7M2Q9XT4B3', 'token-1', LATER, storage);

    expect(readTrackingAccess('AFNK7M2Q9XT4B3', NOW, storage)).toBe('token-1');
  });

  it('keeps tokens separate for each tracking number', () => {
    storeTrackingAccess('AFNK7M2Q9XT4B3', 'token-1', LATER, storage);

    expect(readTrackingAccess('AFNQ2W3E4R5T6Y', NOW, storage)).toBeNull();
  });

  it('drops an expired token and removes it from storage', () => {
    storeTrackingAccess('AFNK7M2Q9XT4B3', 'token-1', EARLIER, storage);

    expect(readTrackingAccess('AFNK7M2Q9XT4B3', NOW, storage)).toBeNull();
    expect(storage.data.size).toBe(0);
  });

  it('survives a page reload by reading it back from storage', () => {
    storage.setItem('afno.trackingAccess.AFNK7M2Q9XT4B3', JSON.stringify({ token: 'token-2', expiresAtUtc: LATER }));

    expect(readTrackingAccess('AFNK7M2Q9XT4B3', NOW, storage)).toBe('token-2');
  });

  it('ignores corrupt stored data', () => {
    storage.setItem('afno.trackingAccess.AFNK7M2Q9XT4B3', '{not json');

    expect(readTrackingAccess('AFNK7M2Q9XT4B3', NOW, storage)).toBeNull();
  });

  it('ignores a stored value with a bad expiry', () => {
    storage.setItem('afno.trackingAccess.AFNK7M2Q9XT4B3', JSON.stringify({ token: 'x', expiresAtUtc: 'soon' }));

    expect(readTrackingAccess('AFNK7M2Q9XT4B3', NOW, storage)).toBeNull();
  });

  it('can be cleared', () => {
    storeTrackingAccess('AFNK7M2Q9XT4B3', 'token-1', LATER, storage);

    clearTrackingAccess('AFNK7M2Q9XT4B3', storage);

    expect(readTrackingAccess('AFNK7M2Q9XT4B3', NOW, storage)).toBeNull();
  });

  it('works without any storage (blocked or unavailable)', () => {
    storeTrackingAccess('AFNK7M2Q9XT4B3', 'token-3', LATER, null);

    expect(readTrackingAccess('AFNK7M2Q9XT4B3', NOW, null)).toBe('token-3');
  });
});

describe('one-time codes', () => {
  it('has six digits', () => {
    expect(OTP_LENGTH).toBe(6);
  });

  it('keeps only digits and at most six of them', () => {
    expect(normalizeOtp('123 456')).toBe('123456');
    expect(normalizeOtp('12-34-56-78')).toBe('123456');
    expect(normalizeOtp('abc')).toBe('');
  });

  it('is valid only with exactly six digits', () => {
    expect(isValidOtp('123456')).toBe(true);
    expect(isValidOtp('123 456')).toBe(true);
    expect(isValidOtp('12345')).toBe(false);
    expect(isValidOtp('')).toBe(false);
  });
});
