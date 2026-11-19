import { describe, expect, it } from 'vitest';
import {
  MAX_BATCH_SIZE,
  addScans,
  buildBatchRequest,
  buildScanRequest,
  describeResult,
  describeScanError,
  extractTrackingNumber,
  outcomeOf,
  outcomeTone,
  parseScanInput,
  remainingAfterScan,
  summarizeResults,
  validateScanForm,
  validateScanSettings,
} from './scan';

// Valid-looking tracking numbers: AFN + 11 characters from ABCDEFGHJKMNPQRSTUVWXYZ23456789.
const A = 'AFNK7M2Q9XT4B3';
const B = 'AFNQ2W3E4R5T6Y';
const make = (i: number) => `AFN${String(i).padStart(11, '2')}`;

describe('extractTrackingNumber', () => {
  it('returns a plain tracking number, normalised', () => {
    expect(extractTrackingNumber(A)).toBe(A);
    expect(extractTrackingNumber(` ${A.toLowerCase()} `)).toBe(A);
    expect(extractTrackingNumber('afn-k7m2q9xt4b3')).toBe(A);
  });

  it('finds the tracking number inside a QR code link', () => {
    expect(extractTrackingNumber(`https://afnoparcel.example/track/${A}`)).toBe(A);
    expect(extractTrackingNumber(`http://localhost:5173/track/${A.toLowerCase()}?src=qr`)).toBe(A);
  });

  it('returns null when there is no tracking number', () => {
    expect(extractTrackingNumber('')).toBeNull();
    expect(extractTrackingNumber('   ')).toBeNull();
    expect(extractTrackingNumber('hello')).toBeNull();
    expect(extractTrackingNumber('https://example.com/track/ABC123')).toBeNull();
    expect(extractTrackingNumber('AFN0000000000X')).toBeNull();
  });
});

describe('parseScanInput with QR links', () => {
  it('accepts a scanned link as a tracking number and drops repeats', () => {
    expect(parseScanInput(`https://host/track/${A} ${A} ${B}`)).toEqual({ valid: [A, B], invalid: [] });
  });
});

describe('parseScanInput', () => {
  it('accepts one number, normalising case and dashes', () => {
    expect(parseScanInput('afn-k7m2q9xt4b3')).toEqual({ valid: [A], invalid: [] });
    expect(parseScanInput(`  ${A}  `)).toEqual({ valid: [A], invalid: [] });
  });

  it('treats whitespace as the end of a number, so a pasted list splits correctly', () => {
    expect(parseScanInput('afn-k7m2 q9xt4b3').valid).toEqual([]);
  });

  it('splits several numbers separated by newlines, commas, semicolons and spaces', () => {
    expect(parseScanInput(`${A}\n${B},${A.toLowerCase()};  ${B}`).valid).toEqual([A, B]);
  });

  it('collects what is not a tracking number', () => {
    expect(parseScanInput(`${A} hello AFN123 ${B}`)).toEqual({ valid: [A, B], invalid: ['hello', 'AFN123'] });
  });

  it('rejects look-alike characters and wrong prefixes', () => {
    expect(parseScanInput('AFN0000000000X').valid).toEqual([]);
    expect(parseScanInput('XYZK7M2Q9XT4B3').valid).toEqual([]);
  });

  it('ignores blank input', () => {
    expect(parseScanInput('  \n ,, ')).toEqual({ valid: [], invalid: [] });
  });
});

describe('addScans', () => {
  it('adds a scanned number to the list', () => {
    const result = addScans([], A);

    expect(result.list).toEqual([A]);
    expect(result.added).toEqual([A]);
  });

  it('does not add the same parcel twice', () => {
    const result = addScans([A], `${A.toLowerCase()} ${B}`);

    expect(result.list).toEqual([A, B]);
    expect(result.duplicates).toEqual([A]);
    expect(result.added).toEqual([B]);
  });

  it('reports invalid entries without adding them', () => {
    const result = addScans([A], 'nonsense');

    expect(result.list).toEqual([A]);
    expect(result.invalid).toEqual(['nonsense']);
  });

  it('stops at the batch limit of 100', () => {
    const full = Array.from({ length: MAX_BATCH_SIZE }, (_, i) => make(i + 1));

    const result = addScans(full, A);

    expect(result.list).toHaveLength(MAX_BATCH_SIZE);
    expect(result.rejectedFull).toEqual([A]);
    expect(result.added).toEqual([]);
  });

  it('fits as many as it can when pasting past the limit', () => {
    const almost = Array.from({ length: MAX_BATCH_SIZE - 1 }, (_, i) => make(i + 1));

    const result = addScans(almost, `${A} ${B}`);

    expect(result.added).toEqual([A]);
    expect(result.rejectedFull).toEqual([B]);
  });

  it('does not modify the list it was given', () => {
    const original = [A];
    addScans(original, B);

    expect(original).toEqual([A]);
  });
});

describe('validateScanForm', () => {
  const valid = { scanType: 'Arrival' as const, warehouseId: 'w1', remarks: '', trackingNumbers: [A] };

  it('accepts a complete form', () => {
    expect(validateScanForm(valid)).toEqual({});
  });

  it('needs a warehouse, a scan type and at least one parcel', () => {
    expect(Object.keys(validateScanForm({ scanType: '', warehouseId: '', remarks: '', trackingNumbers: [] })).sort()).toEqual([
      'scanType',
      'trackingNumbers',
      'warehouseId',
    ]);
  });

  it('allows at most 100 parcels and 500 characters of remarks', () => {
    expect(validateScanForm({ ...valid, trackingNumbers: Array.from({ length: 101 }, (_, i) => make(i + 1)) })).toHaveProperty('trackingNumbers');
    expect(validateScanForm({ ...valid, remarks: 'x'.repeat(501) })).toHaveProperty('remarks');
  });
});

describe('Dispatch destination', () => {
  const dispatch = { scanType: 'Dispatch' as const, warehouseId: 'w1', remarks: '', toWarehouseId: 'w2', trackingNumbers: [A] };

  it('accepts a different destination warehouse', () => {
    expect(validateScanForm(dispatch)).toEqual({});
  });

  it('refuses the same warehouse as the destination', () => {
    expect(validateScanForm({ ...dispatch, toWarehouseId: 'w1' })).toHaveProperty('toWarehouseId');
    expect(validateScanSettings({ ...dispatch, toWarehouseId: 'w1' })).toHaveProperty('toWarehouseId');
  });

  it('does not require a destination, and ignores it for other scan types', () => {
    expect(validateScanForm({ ...dispatch, toWarehouseId: '' })).toEqual({});
    expect(validateScanForm({ ...dispatch, scanType: 'Arrival', toWarehouseId: 'w1' })).toEqual({});
  });

  it('sends the destination only for a Dispatch scan', () => {
    expect(buildBatchRequest(dispatch)).toMatchObject({ toWarehouseId: 'w2' });
    expect('toWarehouseId' in buildBatchRequest({ ...dispatch, scanType: 'Arrival' })).toBe(false);
    expect('toWarehouseId' in buildBatchRequest({ ...dispatch, toWarehouseId: '' })).toBe(false);
  });
});

describe('buildScanRequest', () => {
  const base = { scanType: 'Arrival' as const, warehouseId: 'w1', remarks: ' ok ' };

  it('sends the scanned text as the scanner read it, trimmed', () => {
    expect(buildScanRequest(` https://host/track/${A}\n`, base)).toEqual({
      trackingNumber: `https://host/track/${A}`,
      scanType: 'Arrival',
      warehouseId: 'w1',
      remarks: 'ok',
    });
  });

  it('leaves out blank remarks and sends the destination only for Dispatch', () => {
    expect('remarks' in buildScanRequest(A, { ...base, remarks: ' ' })).toBe(false);
    expect(buildScanRequest(A, { ...base, scanType: 'Dispatch', toWarehouseId: 'w2' })).toMatchObject({ toWarehouseId: 'w2' });
    expect('toWarehouseId' in buildScanRequest(A, { ...base, toWarehouseId: 'w2' })).toBe(false);
  });
});

describe('outcomes', () => {
  it('uses the outcome the server sent', () => {
    expect(outcomeOf({ outcome: 'Duplicate', succeeded: true })).toBe('Duplicate');
    expect(outcomeOf({ outcome: 'Failed', succeeded: false })).toBe('Failed');
    expect(outcomeOf({ outcome: 'Applied', succeeded: true })).toBe('Applied');
  });

  it('falls back to succeeded when the outcome is missing or unknown', () => {
    expect(outcomeOf({ outcome: '', succeeded: true })).toBe('Applied');
    expect(outcomeOf({ outcome: 'Weird', succeeded: false })).toBe('Failed');
  });

  it('counts applied, duplicate and failed scans', () => {
    expect(
      summarizeResults([
        { outcome: 'Applied', succeeded: true },
        { outcome: 'Applied', succeeded: true },
        { outcome: 'Duplicate', succeeded: true },
        { outcome: 'Failed', succeeded: false },
      ]),
    ).toEqual({ total: 4, applied: 2, duplicates: 1, failed: 1 });
    expect(summarizeResults([])).toEqual({ total: 0, applied: 0, duplicates: 0, failed: 0 });
  });

  it('colours applied green, duplicate amber and failed red', () => {
    expect(outcomeTone('Applied')).toContain('emerald');
    expect(outcomeTone('Duplicate')).toContain('amber');
    expect(outcomeTone('Failed')).toContain('red');
  });

  it('describes each result in a line', () => {
    expect(describeResult({ trackingNumber: A, outcome: 'Applied', succeeded: true, statusName: 'Arrived at warehouse' })).toBe('Arrived at warehouse');
    expect(describeResult({ trackingNumber: A, outcome: 'Duplicate', succeeded: true })).toMatch(/already recorded/i);
    expect(describeResult({ trackingNumber: A, outcome: 'Failed', succeeded: false, error: 'Wrong branch' })).toBe('Wrong branch');
  });
});

describe('describeScanError', () => {
  it('tells an unassigned person to ask a manager when the server says 403', () => {
    expect(describeScanError(403, 'You are not assigned to this branch.')).toMatch(/not assigned to this branch.*ask a branch manager/i);
    expect(describeScanError(403, undefined)).toMatch(/ask a branch manager/i);
  });

  it('shows the server message for other errors, with a fallback', () => {
    expect(describeScanError(422, 'Parcel is cancelled.')).toBe('Parcel is cancelled.');
    expect(describeScanError(500, undefined)).toMatch(/try again/i);
  });
});

describe('buildBatchRequest', () => {
  it('builds the batch body and trims remarks', () => {
    expect(buildBatchRequest({ scanType: 'Dispatch', warehouseId: 'w1', remarks: ' truck 7 ', trackingNumbers: [A, B] })).toEqual({
      scanType: 'Dispatch',
      warehouseId: 'w1',
      trackingNumbers: [A, B],
      remarks: 'truck 7',
    });
  });

  it('leaves blank remarks out and copies the list', () => {
    const numbers = [A];
    const request = buildBatchRequest({ scanType: 'Arrival', warehouseId: 'w1', remarks: '  ', trackingNumbers: numbers });

    expect('remarks' in request).toBe(false);
    expect(request.trackingNumbers).not.toBe(numbers);
  });
});

describe('remainingAfterScan', () => {
  it('keeps only the parcels that failed so they can be retried', () => {
    const results = [
      { trackingNumber: A, outcome: 'Applied', succeeded: true },
      { trackingNumber: B, outcome: 'Failed', succeeded: false, error: 'Wrong warehouse' },
    ];

    expect(remainingAfterScan([A, B], results)).toEqual([B]);
  });

  it('treats a duplicate as done, since it was already recorded', () => {
    expect(remainingAfterScan([A, B], [{ trackingNumber: A, outcome: 'Duplicate', succeeded: true }])).toEqual([B]);
  });

  it('keeps everything when the server reports nothing succeeded', () => {
    expect(remainingAfterScan([A, B], [])).toEqual([A, B]);
  });
});
