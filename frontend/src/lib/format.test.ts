import { describe, expect, it } from 'vitest';
import { formatAddress, formatNepalDateTime, formatNpr } from './format';
import { isValidTrackingNumber, normalizeTrackingNumber } from './tracking';

describe('formatNpr', () => {
  it('uses lakh grouping and two decimals', () => {
    expect(formatNpr(123456.5)).toBe('Rs. 1,23,456.50');
  });

  it('rejects non-finite values', () => {
    expect(() => formatNpr(Number.NaN)).toThrow(RangeError);
  });
});

describe('formatNepalDateTime', () => {
  it('converts UTC to Nepal time (UTC+05:45)', () => {
    expect(formatNepalDateTime('2026-11-17T00:00:00Z')).toBe('17 Nov 2026, 05:45 NPT');
  });

  it('rolls over to the next day when needed', () => {
    expect(formatNepalDateTime('2026-11-17T20:30:00Z')).toBe('18 Nov 2026, 02:15 NPT');
  });

  it('rejects invalid input', () => {
    expect(() => formatNepalDateTime('not a date')).toThrow(RangeError);
  });
});

describe('formatAddress', () => {
  it('orders parts like a Nepali address and skips empty ones', () => {
    expect(
      formatAddress({
        province: 'Bagmati',
        district: 'Kathmandu',
        municipality: 'Kathmandu Metropolitan City',
        wardNumber: 10,
        streetOrTole: 'New Baneshwor',
        landmark: null,
        postalCode: '',
      }),
    ).toBe('New Baneshwor, Kathmandu Metropolitan City-10, Kathmandu, Bagmati Province');
  });
});

describe('tracking numbers', () => {
  it('normalizes spaces, dashes and case', () => {
    expect(normalizeTrackingNumber(' np-1234 5678 ')).toBe('NP12345678');
  });

  it.each([
    ['NP12345678', true],
    ['np-1234-5678', true],
    ['SHORT', false],
    ['NP1234567$', false],
  ])('validates %s → %s', (input, expected) => {
    expect(isValidTrackingNumber(input)).toBe(expected);
  });
});
