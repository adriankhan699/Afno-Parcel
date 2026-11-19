import { describe, expect, it } from 'vitest';
import {
  MAX_BATCH_SIZE,
  addScans,
  buildBatchRequest,
  parseScanInput,
  remainingAfterScan,
  validateScanForm,
} from './scan';

// Valid-looking tracking numbers: AFN + 11 characters from ABCDEFGHJKMNPQRSTUVWXYZ23456789.
const A = 'AFNK7M2Q9XT4B3';
const B = 'AFNQ2W3E4R5T6Y';
const make = (i: number) => `AFN${String(i).padStart(11, '2')}`;

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
      { trackingNumber: A, succeeded: true },
      { trackingNumber: B, succeeded: false, error: 'Already scanned' },
    ];

    expect(remainingAfterScan([A, B], results)).toEqual([B]);
  });

  it('keeps everything when the server reports nothing succeeded', () => {
    expect(remainingAfterScan([A, B], [])).toEqual([A, B]);
  });
});
