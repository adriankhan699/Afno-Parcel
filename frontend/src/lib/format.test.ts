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
    expect(normalizeTrackingNumber(' afn-k7m2 q9xt4b3 ')).toBe('AFNK7M2Q9XT4B3');
  });

  it.each([
    ['AFNK7M2Q9XT4B3', true],
    ['afn-k7m2-q9xt4b3', true],
    ['AFN K7M2Q9XT4B3', true],
    ['SHORT', false],
    ['NP12345678', false], // old scheme / wrong prefix
    ['AFNK7M2Q9XT4B', false], // 13 characters
    ['AFNK7M2Q9XT4B33', false], // 15 characters
    ['AFNK7M2Q9XT4B$', false], // symbol
    ['AFN0O1ILQ9XT4B', false], // look-alike characters are not in the alphabet
  ])('validates %s → %s', (input, expected) => {
    expect(isValidTrackingNumber(input)).toBe(expected);
  });
});
