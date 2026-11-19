import type {
  AgentAssignmentResponse,
  AgentPerformanceReport,
  AgentPerformanceRow,
  CollectCodRequest,
  CreateDeliveryAgentRequest,
  DeliveryAgentResponse,
  DeliveryProofResponse,
  MarkDeliveryFailedRequest,
  UpdateDeliveryAgentRequest,
  VerifyDeliveryCodeRequest,
} from '../types/api';
import { isNepalPhone, parseNumber, type FormErrors } from './booking';
import { NEPAL_TIME_ZONE, formatNpr } from './format';
import { barWidth, formatWaiting } from './network';

type QueryValue = string | number | boolean | undefined | null;

/**
 * "?page=1&isActive=false". Unlike the older filters, an explicit `false` is kept (it means "only inactive agents"); only
 * undefined, null and blank values are left out. Values are percent-encoded.
 */
export function buildDeliveryQuery(params: object): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params as Record<string, QueryValue>)) {
    if (value === undefined || value === null || value === '') continue;
    search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

const trimmed = (value: string): string | undefined => {
  const text = value.trim();
  return text ? text : undefined;
};

const orNull = (value: string): string | null => value.trim() || null;

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;
const asDate = (value: string): string | undefined => (DATE_PATTERN.test(value) ? value : undefined);

// ---------------------------------------------------------------------------------------------
// Errors from the agent actions

/**
 * A plain sentence for a failed action. The server's own explanation (a 409 or 422 problem detail) is the best message, so it
 * is shown when there is one.
 */
export function actionErrorMessage(status: number | undefined, detail: string | undefined): string {
  switch (status) {
    case 400:
      return detail ?? 'Please check what you entered and try again.';
    case 401:
      return 'Your session has ended. Please sign in again.';
    case 403:
      return 'You are not allowed to do this.';
    case 404:
      return 'This delivery is no longer available to you. Go back to your list and refresh it.';
    case 409:
      return detail ?? 'This was just changed by someone else. The screen has been refreshed; please check and try again.';
    case 422:
      return detail ?? 'That is not possible right now.';
    case 429:
      return 'Too many attempts. Please wait a minute and try again.';
    case undefined:
      return 'Could not reach the server. Check your connection and try again.';
    default:
      return status >= 500 ? 'The server had a problem. Please try again shortly.' : (detail ?? 'Something went wrong. Please try again.');
  }
}

// ---------------------------------------------------------------------------------------------
// Delivery failed

export const FAILURE_PRESETS = ['Receiver not available', 'Wrong address', 'Receiver refused', 'Phone not reachable', 'Other'] as const;
export type FailurePreset = (typeof FAILURE_PRESETS)[number];

export const REASON_MIN_LENGTH = 5;
export const REASON_MAX_LENGTH = 300;
export const FAILURE_REMARKS_MAX_LENGTH = 500;

export interface FailureForm {
  preset: FailurePreset | '';
  /** Required when the preset is "Other"; optional extra detail otherwise. */
  detail: string;
  remarks: string;
}

export const emptyFailureForm = (): FailureForm => ({ preset: '', detail: '', remarks: '' });

/** The one-line reason sent to the server: the preset, with the agent's own words added. "Other" is just their words. */
export function buildFailureReason(form: Pick<FailureForm, 'preset' | 'detail'>): string {
  const detail = form.detail.trim();
  if (form.preset === 'Other' || form.preset === '') return detail;
  return detail ? `${form.preset}: ${detail}` : form.preset;
}

export function validateFailureForm(form: FailureForm): FormErrors {
  const errors: FormErrors = {};
  if (!form.preset) {
    errors.preset = 'Choose why the delivery failed.';
    return errors;
  }
  const detail = form.detail.trim();
  if (form.preset === 'Other' && detail.length < REASON_MIN_LENGTH) {
    errors.detail = `Describe what happened (at least ${REASON_MIN_LENGTH} characters).`;
  }
  const reason = buildFailureReason(form);
  if (reason.length > REASON_MAX_LENGTH) {
    errors.detail = `The reason can be at most ${REASON_MAX_LENGTH} characters (now ${reason.length}).`;
  }
  if (form.remarks.trim().length > FAILURE_REMARKS_MAX_LENGTH) {
    errors.remarks = `Remarks can be at most ${FAILURE_REMARKS_MAX_LENGTH} characters.`;
  }
  return errors;
}

export function buildFailRequest(form: FailureForm): MarkDeliveryFailedRequest {
  const request: MarkDeliveryFailedRequest = { reason: buildFailureReason(form) };
  const remarks = form.remarks.trim();
  if (remarks) request.remarks = remarks;
  return request;
}

// ---------------------------------------------------------------------------------------------
// Cash on delivery

const toPaisa = (amount: number): number => Math.round(amount * 100);

export interface CodEntryResult {
  ok: boolean;
  amount: number | null;
  error?: string;
}

/** The agent types what they received. Only the exact amount due is accepted: no change kept, no short payment. */
export function validateCodEntry(text: string, due: number): CodEntryResult {
  if (!text.trim()) return { ok: false, amount: null, error: 'Enter the cash you received.' };
  const amount = parseNumber(text);
  if (amount === null || !Number.isFinite(amount) || amount < 0) return { ok: false, amount: null, error: 'Enter the amount as a number, for example 1500.' };
  const received = toPaisa(amount);
  const expected = toPaisa(due);
  if (received < expected) {
    return { ok: false, amount, error: `Ask for the full ${formatNpr(due)}. You entered ${formatNpr(amount)}.` };
  }
  if (received > expected) {
    return { ok: false, amount, error: `That is more than the ${formatNpr(due)} due. Give change back and enter the exact amount.` };
  }
  return { ok: true, amount: due };
}

export const buildCollectRequest = (amount: number): CollectCodRequest => ({ collectedAmount: amount });

// ---------------------------------------------------------------------------------------------
// Proof of delivery

export const DELIVERY_CODE_LENGTH = 6;
export const MAX_PROOF_BYTES = 6 * 1024 * 1024;
/** A phone camera photo can be large; it is shrunk before upload, so only absurd inputs are refused up front. */
export const MAX_PICKED_IMAGE_BYTES = 40 * 1024 * 1024;
export const MAX_IMAGE_EDGE = 1600;
export const JPEG_QUALITY = 0.8;
export const RECIPIENT_NAME_MAX_LENGTH = 150;

export function validateDeliveryCode(code: string): string | null {
  const text = code.trim();
  if (!text) return 'Enter the code the receiver read out.';
  if (!new RegExp(`^\\d{${DELIVERY_CODE_LENGTH}}$`).test(text)) return `The code has ${DELIVERY_CODE_LENGTH} digits.`;
  return null;
}

export function buildOtpProofRequest(code: string, recipientName: string): VerifyDeliveryCodeRequest {
  const request: VerifyDeliveryCodeRequest = { code: code.trim() };
  const name = recipientName.trim();
  if (name) request.recipientName = name;
  return request;
}

export function validateSignature(hasInk: boolean, signerName: string): FormErrors {
  const errors: FormErrors = {};
  if (!hasInk) errors.signature = 'Ask the receiver to sign in the box.';
  const name = signerName.trim();
  if (!name) errors.recipientName = 'Enter the name of the person who signed.';
  else if (name.length > RECIPIENT_NAME_MAX_LENGTH) errors.recipientName = `At most ${RECIPIENT_NAME_MAX_LENGTH} characters.`;
  return errors;
}

export function validateRecipientName(name: string): string | null {
  return name.trim().length > RECIPIENT_NAME_MAX_LENGTH ? `At most ${RECIPIENT_NAME_MAX_LENGTH} characters.` : null;
}

/** A photo the agent picked: it must be an image, and not absurdly large. */
export function validatePickedImage(file: { type: string; size: number }): string | null {
  if (!file.type.toLowerCase().startsWith('image/')) return 'Choose a photo (an image file).';
  if (file.size <= 0) return 'That file is empty.';
  if (file.size > MAX_PICKED_IMAGE_BYTES) return 'That photo is too large. Take a new one with the camera.';
  return null;
}

/** The size check after the photo has been shrunk, just before upload. */
export function validateProcessedSize(bytes: number): string | null {
  if (bytes <= 0) return 'The photo could not be prepared. Try again.';
  if (bytes > MAX_PROOF_BYTES) return `The photo is still larger than ${Math.round(MAX_PROOF_BYTES / (1024 * 1024))} MB after shrinking. Take it again from further away.`;
  return null;
}

/** The size to draw a picture at so neither edge is longer than `max`, never enlarging, keeping the proportions. */
export function fitWithin(width: number, height: number, max = MAX_IMAGE_EDGE): { width: number; height: number } {
  if (!(width > 0) || !(height > 0)) return { width: 0, height: 0 };
  const longest = Math.max(width, height);
  if (longest <= max) return { width: Math.round(width), height: Math.round(height) };
  const scale = max / longest;
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}

/** The multipart body of a signature or photo proof: type, optional recipient name and the file. */
export function buildFileProofForm(type: 'Signature' | 'Photo', file: Blob, fileName: string, recipientName: string): FormData {
  const form = new FormData();
  form.set('type', type);
  const name = recipientName.trim();
  if (name) form.set('recipientName', name);
  form.set('file', file, fileName);
  return form;
}

// ---------------------------------------------------------------------------------------------
// The agent's assignment screens

/** What a proof is called in a list: "Signature (Sita)". */
export function proofSummary(proof: Pick<DeliveryProofResponse, 'type' | 'recipientName'>): string {
  const who = proof.recipientName ? ` (${proof.recipientName})` : '';
  return proof.type === 'Otp' ? `Code checked${who}` : `${proof.type}${who}`;
}

export const isOpenStatus = (status: string): boolean => status === 'Assigned' || status === 'Accepted' || status === 'InProgress';

export type AgentStage = 'assigned' | 'deliver' | 'failed' | 'completed' | 'closed';

/** Which part of the delivery the agent is in, from the assignment's own status. */
export function agentStage(assignment: Pick<AgentAssignmentResponse, 'status'>): AgentStage {
  switch (assignment.status) {
    case 'Assigned':
    case 'Accepted':
      return 'assigned';
    case 'InProgress':
      return 'deliver';
    case 'Failed':
      return 'failed';
    case 'Completed':
      return 'completed';
    default:
      return 'closed';
  }
}

export type DeliveryStepKey = 'cash' | 'proof' | 'confirm';

export interface DeliveryStep {
  key: DeliveryStepKey;
  label: string;
  done: boolean;
}

/** The steps to finish a delivery: collect the cash (only for cash on delivery), record a proof, confirm. */
export function deliverySteps(assignment: Pick<AgentAssignmentResponse, 'status' | 'codAmount' | 'codCollected' | 'hasProof'>): DeliveryStep[] {
  const steps: DeliveryStep[] = [];
  if (assignment.codAmount > 0) {
    steps.push({ key: 'cash', label: `Collect ${formatNpr(assignment.codAmount)} in cash`, done: assignment.codCollected });
  }
  steps.push({ key: 'proof', label: 'Record proof of delivery', done: assignment.hasProof });
  steps.push({ key: 'confirm', label: 'Confirm delivery', done: assignment.status === 'Completed' });
  return steps;
}

/** The step the agent should do next, or null when there is nothing left (or the delivery has not started). */
export function nextDeliveryStep(assignment: Pick<AgentAssignmentResponse, 'status' | 'codAmount' | 'codCollected' | 'hasProof'>): DeliveryStepKey | null {
  if (assignment.status !== 'InProgress') return null;
  return deliverySteps(assignment).find((s) => !s.done)?.key ?? null;
}

export function assignmentTone(status: string): string {
  switch (status) {
    case 'Completed':
      return 'bg-emerald-50 text-emerald-700';
    case 'Failed':
    case 'Cancelled':
      return 'bg-red-50 text-red-700';
    case 'InProgress':
      return 'bg-amber-50 text-amber-800';
    default:
      return 'bg-blue-50 text-blue-700';
  }
}

const ASSIGNMENT_STATUS_LABELS: Record<string, string> = {
  Assigned: 'New',
  Accepted: 'Accepted',
  InProgress: 'Out for delivery',
  Completed: 'Delivered',
  Failed: 'Failed',
  Cancelled: 'Cancelled',
};

export const assignmentLabel = (status: string): string => ASSIGNMENT_STATUS_LABELS[status] ?? status;

/** A link that makes a phone start a call. Only digits and a leading + are kept, so nothing odd can be smuggled into the link. */
export function telHref(phone: string | null | undefined): string | null {
  if (!phone) return null;
  const cleaned = phone.trim().replace(/(?!^\+)[^\d]/g, '');
  return /\d/.test(cleaned) ? `tel:${cleaned}` : null;
}

/** A map search for the address text. It only opens a web page; the app never reads the device's location. */
export function mapsUrl(address: string | null | undefined, area?: string | null): string | null {
  const text = [address, area].filter((part): part is string => Boolean(part && part.trim())).join(', ');
  return text ? `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(text)}` : null;
}

// ---------------------------------------------------------------------------------------------
// Managing agents

export interface AgentFilters {
  search: string;
  branchId: string;
  status: '' | 'active' | 'inactive';
  availability: '' | 'available' | 'unavailable';
}

export const emptyAgentFilters = (): AgentFilters => ({ search: '', branchId: '', status: '', availability: '' });

export function agentQuery(filters: AgentFilters, page: number, pageSize = 20) {
  return {
    page,
    pageSize,
    search: trimmed(filters.search),
    branchId: filters.branchId || undefined,
    isActive: filters.status === '' ? undefined : filters.status === 'active',
    isAvailable: filters.availability === '' ? undefined : filters.availability === 'available',
  };
}

export interface AgentForm {
  fullName: string;
  email: string;
  phoneNumber: string;
  password: string;
  branchId: string;
  vehicleType: string;
  vehicleNumber: string;
  licenseNumber: string;
}

export const emptyAgentForm = (): AgentForm => ({
  fullName: '',
  email: '',
  phoneNumber: '',
  password: '',
  branchId: '',
  vehicleType: '',
  vehicleNumber: '',
  licenseNumber: '',
});

export const agentToForm = (agent: DeliveryAgentResponse): AgentForm => ({
  fullName: agent.fullName,
  email: agent.email ?? '',
  phoneNumber: agent.phoneNumber,
  password: '',
  branchId: agent.branchId,
  vehicleType: agent.vehicleType ?? '',
  vehicleNumber: agent.vehicleNumber ?? '',
  licenseNumber: agent.licenseNumber ?? '',
});

export const AGENT_LIMITS = { name: 150, vehicleType: 40, vehicleNumber: 30, licenseNumber: 40 } as const;

export function isStrongPassword(password: string): boolean {
  return password.length >= 10 && /[a-z]/.test(password) && /[A-Z]/.test(password) && /\d/.test(password);
}

/** A new agent needs the login details and a branch; an existing one can only change phone and vehicle details. */
export function validateAgentForm(form: AgentForm, isNew: boolean): FormErrors {
  const errors: FormErrors = {};
  if (isNew) {
    const name = form.fullName.trim();
    if (!name) errors.fullName = 'Enter the full name.';
    else if (name.length > AGENT_LIMITS.name) errors.fullName = `At most ${AGENT_LIMITS.name} characters.`;
    if (!EMAIL_PATTERN.test(form.email.trim())) errors.email = 'Enter a valid email address. The agent signs in with it.';
    if (!form.branchId) errors.branchId = 'Choose the branch this agent rides from.';
    if (!isStrongPassword(form.password)) {
      errors.password = 'At least 10 characters with an upper-case letter, a lower-case letter and a digit.';
    }
  }
  if (!isNepalPhone(form.phoneNumber)) errors.phoneNumber = 'Enter a valid Nepali mobile (98XXXXXXXX) or landline number.';
  if (form.vehicleType.trim().length > AGENT_LIMITS.vehicleType) errors.vehicleType = `At most ${AGENT_LIMITS.vehicleType} characters.`;
  if (form.vehicleNumber.trim().length > AGENT_LIMITS.vehicleNumber) errors.vehicleNumber = `At most ${AGENT_LIMITS.vehicleNumber} characters.`;
  if (form.licenseNumber.trim().length > AGENT_LIMITS.licenseNumber) errors.licenseNumber = `At most ${AGENT_LIMITS.licenseNumber} characters.`;
  return errors;
}

export function buildCreateAgentRequest(form: AgentForm): CreateDeliveryAgentRequest {
  return {
    fullName: form.fullName.trim(),
    email: form.email.trim(),
    phoneNumber: form.phoneNumber.trim(),
    password: form.password,
    branchId: form.branchId,
    vehicleType: orNull(form.vehicleType),
    vehicleNumber: orNull(form.vehicleNumber),
    licenseNumber: orNull(form.licenseNumber),
  };
}

export function buildUpdateAgentRequest(form: AgentForm): UpdateDeliveryAgentRequest {
  return {
    phoneNumber: form.phoneNumber.trim(),
    vehicleType: orNull(form.vehicleType),
    vehicleNumber: orNull(form.vehicleNumber),
    licenseNumber: orNull(form.licenseNumber),
  };
}

/** Agents who can be given a parcel now: active and available. */
export const assignableAgents = <T extends Pick<DeliveryAgentResponse, 'isActive' | 'isAvailable'>>(agents: readonly T[]): T[] =>
  agents.filter((a) => a.isActive && a.isAvailable);

export interface AssignmentFilters {
  status: string;
  deliveryAgentId: string;
  from: string;
  to: string;
}

export const emptyAssignmentFilters = (): AssignmentFilters => ({ status: '', deliveryAgentId: '', from: '', to: '' });

export function assignmentQuery(filters: AssignmentFilters, page: number, pageSize = 20) {
  return {
    page,
    pageSize,
    status: filters.status || undefined,
    deliveryAgentId: filters.deliveryAgentId || undefined,
    from: asDate(filters.from),
    to: asDate(filters.to),
  };
}

export const CANCEL_REASON_MIN_LENGTH = 5;
export const CANCEL_REASON_MAX_LENGTH = 300;

export function validateCancelReason(reason: string): string | null {
  const text = reason.trim();
  if (text.length < CANCEL_REASON_MIN_LENGTH) return `Say why the parcel is being taken back (at least ${CANCEL_REASON_MIN_LENGTH} characters).`;
  if (text.length > CANCEL_REASON_MAX_LENGTH) return `At most ${CANCEL_REASON_MAX_LENGTH} characters.`;
  return null;
}

export interface ReadyFilters {
  search: string;
  branchId: string;
}

export const emptyReadyFilters = (): ReadyFilters => ({ search: '', branchId: '' });

export function readyQuery(filters: ReadyFilters, page: number, pageSize = 20) {
  return { page, pageSize, search: trimmed(filters.search), branchId: filters.branchId || undefined };
}

// ---------------------------------------------------------------------------------------------
// Performance

/** Today's date in Nepal time as yyyy-MM-dd. */
export function nepalDate(now: Date): string {
  const parts = new Intl.DateTimeFormat('en-GB', { timeZone: NEPAL_TIME_ZONE, year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(now);
  const part = (type: string) => parts.find((p) => p.type === type)?.value ?? '';
  return `${part('year')}-${part('month')}-${part('day')}`;
}

/** yyyy-MM-dd moved by a number of days (calendar arithmetic, so no time zone or daylight saving can interfere). */
export function shiftDate(date: string, days: number): string {
  const [year = 1970, month = 1, day = 1] = date.split('-').map(Number);
  const moved = new Date(Date.UTC(year, month - 1, day + days));
  return moved.toISOString().slice(0, 10);
}

export const DEFAULT_PERFORMANCE_DAYS = 30;

/** The last 30 Nepal days including today. */
export function defaultPerformanceRange(now: Date, days = DEFAULT_PERFORMANCE_DAYS): { from: string; to: string } {
  const to = nepalDate(now);
  return { from: shiftDate(to, -(days - 1)), to };
}

export interface PerformanceFilters {
  from: string;
  to: string;
  branchId: string;
}

export function performanceQuery(filters: PerformanceFilters) {
  return { from: asDate(filters.from), to: asDate(filters.to), branchId: filters.branchId || undefined };
}

export function validatePerformanceRange(from: string, to: string): string | null {
  if (!DATE_PATTERN.test(from) || !DATE_PATTERN.test(to)) return 'Choose both dates.';
  if (from > to) return 'The end date cannot be before the start date.';
  return null;
}

/** Delivered divided by delivered plus failed, to one decimal place; null when nothing was attempted. */
export function computeSuccessRate(delivered: number, failed: number): number | null {
  const attempts = delivered + failed;
  return attempts === 0 ? null : Math.round((delivered / attempts) * 1000) / 10;
}

/** The server's rate, or one worked out from the counts when the server did not send it. */
export function rowSuccessRate(row: Pick<AgentPerformanceRow, 'delivered' | 'failed' | 'successRatePercent'>): number | null {
  return row.successRatePercent ?? computeSuccessRate(row.delivered, row.failed);
}

export const formatPercent = (value: number | null | undefined): string => (value === null || value === undefined ? '—' : `${value}%`);

export const formatAverageHours = (value: number | null | undefined): string => (value === null || value === undefined ? '—' : formatWaiting(value));

export function rateTone(percent: number | null): string {
  if (percent === null) return 'bg-slate-300';
  if (percent >= 90) return 'bg-emerald-500';
  if (percent >= 75) return 'bg-amber-500';
  return 'bg-red-500';
}

export const rateBarWidth = (percent: number | null): number => barWidth(percent);

// ---------------------------------------------------------------------------------------------
// CSV

/**
 * One CSV cell. Quotes are doubled and cells with commas, quotes or line breaks are wrapped in quotes. A text cell that starts
 * with = + - @ or a control character would be run as a formula by a spreadsheet, so it gets a leading apostrophe. Numbers are
 * written as they are.
 */
export function csvCell(value: string | number | boolean | null | undefined): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'number') return Number.isFinite(value) ? String(value) : '';
  let text = String(value);
  if (/^[=+\-@\t\r]/.test(text)) text = `'${text}`;
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

export const csvLine = (cells: readonly (string | number | boolean | null | undefined)[]): string => cells.map(csvCell).join(',');

export const PERFORMANCE_CSV_HEADER = [
  'Agent',
  'Branch',
  'Active',
  'Assigned',
  'Delivered',
  'Failed',
  'Cancelled',
  'In progress',
  'Success rate %',
  'First-attempt success %',
  'Average delivery hours',
  'COD collections',
  'COD collected (NPR)',
] as const;

function performanceCells(row: AgentPerformanceRow, label: string, branch: string, active: string) {
  return [
    label,
    branch,
    active,
    row.assigned,
    row.delivered,
    row.failed,
    row.cancelled,
    row.inProgress,
    rowSuccessRate(row),
    row.firstAttemptSuccessPercent,
    row.averageDeliveryHours,
    row.codCollections,
    row.codCollected,
  ];
}

/** The report as CSV text, one line per agent and a final totals line. Lines end with CRLF as spreadsheets expect. */
export function performanceCsv(report: AgentPerformanceReport): string {
  const lines = [
    csvLine(['Delivery agent performance', `${report.from} to ${report.to}`]),
    csvLine(PERFORMANCE_CSV_HEADER),
    ...report.agents.map((a) => csvLine(performanceCells(a, a.fullName, a.branchName, a.isActive ? 'Yes' : 'No'))),
    csvLine(performanceCells(report.totals, 'Total', '', '')),
  ];
  return `${lines.join('\r\n')}\r\n`;
}

export const performanceFileName = (report: Pick<AgentPerformanceReport, 'from' | 'to'>): string => `agent-performance-${report.from}-to-${report.to}.csv`;
