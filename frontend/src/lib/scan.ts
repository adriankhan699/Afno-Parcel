import type { BatchScanRequest, ScanRequest, ScanResultDto, ScanType } from '../types/api';
import { isValidTrackingNumber, normalizeTrackingNumber } from './tracking';

export const MAX_BATCH_SIZE = 100;
export const REMARKS_MAX = 500;

export const SCAN_TYPE_OPTIONS: ReadonlyArray<{ value: ScanType; label: string; hint: string }> = [
  { value: 'Arrival', label: 'Arrival', hint: 'Parcels arriving at this warehouse → Arrived at warehouse' },
  { value: 'Dispatch', label: 'Dispatch', hint: 'Parcels leaving for the next warehouse → In transit' },
  { value: 'OutForDelivery', label: 'Out for delivery', hint: 'Parcels handed to a rider → Out for delivery' },
];

const TRACKING_IN_TEXT = /AFN[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{11}/i;

/**
 * Pulls the tracking number out of whatever a scanner read. A barcode gives the number itself (maybe lower case or with
 * dashes); a QR code gives the tracking link, e.g. https://host/track/AFNK7M2Q9XT4B3. Returns null when there is none.
 */
export function extractTrackingNumber(raw: string): string | null {
  const text = raw.trim();
  if (!text) return null;
  if (isValidTrackingNumber(text)) return normalizeTrackingNumber(text);
  const found = TRACKING_IN_TEXT.exec(text);
  return found ? found[0].toUpperCase() : null;
}

export interface ParsedScanInput {
  /** Normalised, valid, in the order given, without repeats. */
  valid: string[];
  /** The pieces that did not contain a tracking number (as typed). */
  invalid: string[];
}

/**
 * A barcode scanner types a tracking number and presses Enter; a person may paste many at once. Split on anything that
 * is not part of a code, extract each tracking number (from a barcode or a QR link), and drop repeats.
 */
export function parseScanInput(raw: string): ParsedScanInput {
  const pieces = raw
    .split(/[\s,;]+/)
    .map((p) => p.trim())
    .filter(Boolean);

  const valid: string[] = [];
  const invalid: string[] = [];
  for (const piece of pieces) {
    const trackingNumber = extractTrackingNumber(piece);
    if (trackingNumber) {
      if (!valid.includes(trackingNumber)) valid.push(trackingNumber);
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
  /** Only used for Dispatch: the warehouse the parcels are travelling to. */
  toWarehouseId?: string;
  remarks: string;
  trackingNumbers: readonly string[];
}

/** Field errors for the part of the form shared by single and batch scans (everything but the codes). */
export function validateScanSettings(form: Pick<ScanForm, 'scanType' | 'warehouseId' | 'toWarehouseId' | 'remarks'>): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!form.warehouseId) errors.warehouseId = 'Choose the warehouse you are scanning at.';
  if (!form.scanType) errors.scanType = 'Choose what kind of scan this is.';
  if (form.scanType === 'Dispatch' && form.toWarehouseId && form.toWarehouseId === form.warehouseId) {
    errors.toWarehouseId = 'The destination must be a different warehouse.';
  }
  if (form.remarks.trim().length > REMARKS_MAX) errors.remarks = `Remarks can be at most ${REMARKS_MAX} characters.`;
  return errors;
}

export function validateScanForm(form: ScanForm): Record<string, string> {
  const errors = validateScanSettings(form);
  if (form.trackingNumbers.length === 0) errors.trackingNumbers = 'Scan or type at least one tracking number.';
  if (form.trackingNumbers.length > MAX_BATCH_SIZE) errors.trackingNumbers = `At most ${MAX_BATCH_SIZE} parcels per submit.`;
  return errors;
}

/** The destination warehouse is only ever sent for a Dispatch scan. */
function destination(form: { scanType: ScanType; toWarehouseId?: string }): { toWarehouseId?: string } {
  return form.scanType === 'Dispatch' && form.toWarehouseId ? { toWarehouseId: form.toWarehouseId } : {};
}

export function buildBatchRequest(form: ScanForm & { scanType: ScanType }): BatchScanRequest {
  const request: BatchScanRequest = {
    scanType: form.scanType,
    warehouseId: form.warehouseId,
    trackingNumbers: [...form.trackingNumbers],
    ...destination(form),
  };
  const remarks = form.remarks.trim();
  if (remarks) request.remarks = remarks;
  return request;
}

/** One scan of one code, exactly as the scanner read it (the server extracts the tracking number). */
export function buildScanRequest(code: string, form: Omit<ScanForm, 'trackingNumbers' | 'scanType'> & { scanType: ScanType }): ScanRequest {
  const request: ScanRequest = {
    trackingNumber: code.trim(),
    scanType: form.scanType,
    warehouseId: form.warehouseId,
    ...destination(form),
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

export type OutcomeKind = 'Applied' | 'Duplicate' | 'Failed';

/** The outcome of a result, falling back to `succeeded` when the server did not send one. */
export function outcomeOf(result: Pick<ScanResultDto, 'outcome' | 'succeeded'>): OutcomeKind {
  if (result.outcome === 'Applied' || result.outcome === 'Duplicate' || result.outcome === 'Failed') return result.outcome;
  return result.succeeded ? 'Applied' : 'Failed';
}

export interface ScanSummary {
  total: number;
  applied: number;
  duplicates: number;
  failed: number;
}

export function summarizeResults(results: readonly Pick<ScanResultDto, 'outcome' | 'succeeded'>[]): ScanSummary {
  const summary: ScanSummary = { total: results.length, applied: 0, duplicates: 0, failed: 0 };
  for (const result of results) {
    const outcome = outcomeOf(result);
    if (outcome === 'Applied') summary.applied += 1;
    else if (outcome === 'Duplicate') summary.duplicates += 1;
    else summary.failed += 1;
  }
  return summary;
}

export const OUTCOME_LABELS: Record<OutcomeKind, string> = {
  Applied: 'Recorded',
  Duplicate: 'Already recorded',
  Failed: 'Failed',
};

/** Tailwind classes for an outcome pill: green, amber, red. */
export function outcomeTone(outcome: string): string {
  switch (outcome) {
    case 'Applied':
      return 'bg-emerald-50 text-emerald-700';
    case 'Duplicate':
      return 'bg-amber-50 text-amber-800';
    default:
      return 'bg-red-50 text-red-700';
  }
}

/** One line for the live results list. */
export function describeResult(result: ScanResultDto): string {
  switch (outcomeOf(result)) {
    case 'Applied':
      return result.message ?? result.statusName ?? 'Scan recorded.';
    case 'Duplicate':
      return result.message ?? 'This scan was already recorded. Nothing was changed.';
    default:
      return result.error ?? 'Could not be scanned.';
  }
}

/** What to tell the person when the server refuses a whole scan request. */
export function describeScanError(httpStatus: number | undefined, detail: string | undefined): string {
  if (httpStatus === 403) {
    return `${detail ?? 'You are not allowed to scan here.'} Ask a branch manager to assign you to this warehouse's branch.`;
  }
  return detail ?? 'The scan could not be recorded. Please try again.';
}

/** Camera scanning: the formats worth asking the browser for (the tracking number barcode and the QR code). */
export const CAMERA_FORMATS = ['qr_code', 'code_128'] as const;
