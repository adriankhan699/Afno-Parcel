import type { BatchScanRequest, ScanResultDto, ScanType } from '../types/api';
import { isValidTrackingNumber, normalizeTrackingNumber } from './tracking';

export const MAX_BATCH_SIZE = 100;

export const SCAN_TYPE_OPTIONS: ReadonlyArray<{ value: ScanType; label: string; hint: string }> = [
  { value: 'Arrival', label: 'Arrival', hint: 'Parcels arriving at this warehouse → Arrived at warehouse' },
  { value: 'Dispatch', label: 'Dispatch', hint: 'Parcels leaving for the next hub → In transit' },
  { value: 'OutForDelivery', label: 'Out for delivery', hint: 'Parcels handed to a rider → Out for delivery' },
];

export interface ParsedScanInput {
  /** Normalised, valid, in the order given, without repeats. */
  valid: string[];
  /** The pieces that are not tracking numbers (as typed). */
  invalid: string[];
}

/**
 * A barcode scanner types a tracking number and presses Enter; a person may paste many at once. Split on anything that
 * is not part of a tracking number, normalise, and drop repeats.
 */
export function parseScanInput(raw: string): ParsedScanInput {
  const pieces = raw
    .split(/[\s,;]+/)
    .map((p) => p.trim())
    .filter(Boolean);

  const valid: string[] = [];
  const invalid: string[] = [];
  for (const piece of pieces) {
    if (isValidTrackingNumber(piece)) {
      const normalized = normalizeTrackingNumber(piece);
      if (!valid.includes(normalized)) valid.push(normalized);
    } else {
      invalid.push(piece);
    }
  }
  return { valid, invalid };
}

export interface AddScansResult {
  list: string[];
  added: string[];
  /** Valid numbers that were already in the list. */
  duplicates: string[];
  invalid: string[];
  /** Valid numbers that did not fit because the list is full. */
  rejectedFull: string[];
}

/** Adds scanned numbers to the pending list, never exceeding the batch limit. */
export function addScans(list: readonly string[], raw: string, limit = MAX_BATCH_SIZE): AddScansResult {
  const { valid, invalid } = parseScanInput(raw);
  const next = [...list];
  const added: string[] = [];
  const duplicates: string[] = [];
  const rejectedFull: string[] = [];

  for (const trackingNumber of valid) {
    if (next.includes(trackingNumber)) {
      duplicates.push(trackingNumber);
    } else if (next.length >= limit) {
      rejectedFull.push(trackingNumber);
    } else {
      next.push(trackingNumber);
      added.push(trackingNumber);
    }
  }
  return { list: next, added, duplicates, invalid, rejectedFull };
}

export interface ScanForm {
  scanType: ScanType | '';
  warehouseId: string;
  remarks: string;
  trackingNumbers: readonly string[];
}

export function validateScanForm(form: ScanForm): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!form.warehouseId) errors.warehouseId = 'Choose the warehouse you are scanning at.';
  if (!form.scanType) errors.scanType = 'Choose what kind of scan this is.';
  if (form.trackingNumbers.length === 0) errors.trackingNumbers = 'Scan or type at least one tracking number.';
  if (form.trackingNumbers.length > MAX_BATCH_SIZE) errors.trackingNumbers = `At most ${MAX_BATCH_SIZE} parcels per submit.`;
  if (form.remarks.trim().length > 500) errors.remarks = 'Remarks can be at most 500 characters.';
  return errors;
}

export function buildBatchRequest(form: ScanForm & { scanType: ScanType }): BatchScanRequest {
  const request: BatchScanRequest = {
    scanType: form.scanType,
    warehouseId: form.warehouseId,
    trackingNumbers: [...form.trackingNumbers],
  };
  const remarks = form.remarks.trim();
  if (remarks) request.remarks = remarks;
  return request;
}

/** After a submit, keep only the numbers that failed so they can be fixed and sent again. */
export function remainingAfterScan(list: readonly string[], results: readonly ScanResultDto[]): string[] {
  const done = new Set(results.filter((r) => r.succeeded).map((r) => normalizeTrackingNumber(r.trackingNumber)));
  return list.filter((trackingNumber) => !done.has(trackingNumber));
}
