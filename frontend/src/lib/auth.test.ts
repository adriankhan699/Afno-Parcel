import { describe, expect, it } from 'vitest';
import {
  canBook,
  clearRefreshToken,
  isCounterStaff,
  isCustomer,
  isStaff,
  readRefreshToken,
  safeReturnPath,
  writeRefreshToken,
  type TokenStorage,
} from './auth';

describe('role helpers', () => {
  it('customer', () => {
    expect(isCustomer(['Customer'])).toBe(true);
    expect(isCounterStaff(['Customer'])).toBe(false);
    expect(isStaff(['Customer'])).toBe(false);
    expect(canBook(['Customer'])).toBe(true);
  });

  it.each(['SuperAdmin', 'BranchManager', 'BookingStaff'])('%s is counter staff who can book and see all parcels', (role) => {
    expect(isCounterStaff([role])).toBe(true);
    expect(isStaff([role])).toBe(true);
    expect(canBook([role])).toBe(true);
  });

  it.each(['WarehouseStaff', 'Accountant'])('%s can see all parcels but not book', (role) => {
    expect(isStaff([role])).toBe(true);
    expect(isCounterStaff([role])).toBe(false);
    expect(canBook([role])).toBe(false);
  });

  it('delivery agents and anonymous users have no parcel access', () => {
    expect(isStaff(['DeliveryAgent'])).toBe(false);
    expect(canBook(['DeliveryAgent'])).toBe(false);
    expect(isStaff(undefined)).toBe(false);
    expect(canBook(null)).toBe(false);
    expect(isCustomer([])).toBe(false);
  });
});

describe('safeReturnPath', () => {
  it('allows in-app paths', () => {
    expect(safeReturnPath('/parcels/123?page=2')).toBe('/parcels/123?page=2');
  });

  it.each(['https://evil.example', '//evil.example', '/\\evil.example', 'parcels', '', undefined, 42])(
    'rejects %s',
    (value) => {
      expect(safeReturnPath(value)).toBe('/');
    },
  );

  it('uses the given fallback', () => {
    expect(safeReturnPath(undefined, '/parcels')).toBe('/parcels');
  });
});

describe('refresh token storage', () => {
  const memory = (): TokenStorage & { data: Map<string, string> } => {
    const data = new Map<string, string>();
    return {
      data,
      getItem: (k) => data.get(k) ?? null,
      setItem: (k, v) => void data.set(k, v),
      removeItem: (k) => void data.delete(k),
    };
  };

  it('round-trips and clears the token', () => {
    const storage = memory();
    expect(readRefreshToken(storage)).toBeNull();
    writeRefreshToken('abc', storage);
    expect(readRefreshToken(storage)).toBe('abc');
    clearRefreshToken(storage);
    expect(readRefreshToken(storage)).toBeNull();
  });

  it('survives unavailable or throwing storage', () => {
    const broken: TokenStorage = {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
      removeItem: () => {
        throw new Error('blocked');
      },
    };
    expect(readRefreshToken(broken)).toBeNull();
    expect(() => writeRefreshToken('x', broken)).not.toThrow();
    expect(() => clearRefreshToken(broken)).not.toThrow();
    expect(readRefreshToken(null)).toBeNull();
  });
});
