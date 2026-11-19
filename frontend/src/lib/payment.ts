import type {
  AgentCashPositionRow,
  CreateSettlementRequest,
  InitiatePaymentRequest,
  OnlineProvider,
  PaymentReportQuery,
  PaymentReportResponse,
  PaymentResponse,
  PaymentStatusResponse,
  PaymentTransactionQuery,
  PaymentTransactionRow,
  RecordOfficeCashRequest,
  ResolveSettlementRequest,
  SettlementQuery,
  VerifySettlementRequest,
} from '../types/api';
import { parseNumber, type FormErrors } from './booking';
import { csvLine, defaultPerformanceRange, validateCodEntry, validatePerformanceRange } from './delivery';
import { formatNpr } from './format';

// ---------------------------------------------------------------------------------------------
// Labels and colours

export const PROVIDER_LABELS: Record<string, string> = {
  Esewa: 'eSewa',
  Khalti: 'Khalti',
  Cash: 'Cash at the office',
  CashOnDelivery: 'Cash on delivery',
  BankTransfer: 'Bank transfer',
  ConnectIps: 'ConnectIPS',
};

export const providerLabel = (provider: string): string => PROVIDER_LABELS[provider] ?? provider;

/** The online providers that exist in this website (both simulated in test mode). */
export const isOnlineMethod = (method: string): method is OnlineProvider => method === 'Esewa' || method === 'Khalti';

const TRANSACTION_STATUS_LABELS: Record<string, string> = { Pending: 'Waiting', Succeeded: 'Paid', Failed: 'Failed' };

export const transactionStatusLabel = (status: string): string => TRANSACTION_STATUS_LABELS[status] ?? status;

export function transactionStatusTone(status: string): string {
  switch (status) {
    case 'Succeeded':
      return 'bg-emerald-50 text-emerald-700';
    case 'Failed':
      return 'bg-red-50 text-red-700';
    default:
      return 'bg-amber-50 text-amber-800';
  }
}

const PAYMENT_STATUS_LABELS: Record<string, string> = {
  Pending: 'Not paid yet',
  Paid: 'Paid',
  Failed: 'Failed',
  Refunded: 'Refunded',
  PartiallyRefunded: 'Partly refunded',
};

export const paymentStatusLabel = (status: string): string => PAYMENT_STATUS_LABELS[status] ?? status;

export function paymentStatusTone(status: string): string {
  switch (status) {
    case 'Paid':
      return 'bg-emerald-50 text-emerald-700';
    case 'Failed':
      return 'bg-red-50 text-red-700';
    case 'Refunded':
    case 'PartiallyRefunded':
      return 'bg-slate-200 text-slate-700';
    default:
      return 'bg-amber-50 text-amber-800';
  }
}

const SETTLEMENT_STATUS_LABELS: Record<string, string> = {
  Submitted: 'Waiting for count',
  Verified: 'Counted and accepted',
  Disputed: 'Count did not match',
};

export const settlementStatusLabel = (status: string): string => SETTLEMENT_STATUS_LABELS[status] ?? status;

export function settlementStatusTone(status: string): string {
  switch (status) {
    case 'Verified':
      return 'bg-emerald-50 text-emerald-700';
    case 'Disputed':
      return 'bg-red-50 text-red-700';
    default:
      return 'bg-amber-50 text-amber-800';
  }
}

export const SETTLEMENT_STATUS_OPTIONS = [
  { value: 'Submitted', label: SETTLEMENT_STATUS_LABELS.Submitted ?? 'Submitted' },
  { value: 'Verified', label: SETTLEMENT_STATUS_LABELS.Verified ?? 'Verified' },
  { value: 'Disputed', label: SETTLEMENT_STATUS_LABELS.Disputed ?? 'Disputed' },
];

export const TRANSACTION_STATUS_OPTIONS = [
  { value: 'Pending', label: 'Waiting' },
  { value: 'Succeeded', label: 'Paid' },
  { value: 'Failed', label: 'Failed' },
];

export const PROVIDER_OPTIONS = Object.entries(PROVIDER_LABELS).map(([value, label]) => ({ value, label }));

// ---------------------------------------------------------------------------------------------
// Errors

/** A plain sentence for a failed payment action. The server's own explanation is the best message, so it is used when present. */
export function paymentErrorMessage(status: number | undefined, detail: string | undefined): string {
  switch (status) {
    case 400:
      return detail ?? 'Please check what you entered and try again.';
    case 401:
      return 'Your session has ended. Please sign in again.';
    case 403:
      return 'You are not allowed to do this.';
    case 404:
      return detail ?? 'We could not find that payment.';
    case 409:
      return detail ?? 'This was just changed by someone else. Please reload and try again.';
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
// What can be done with a payment

/** The provider the customer can pay with now: only a payment that is still unpaid, and only the method the parcel was booked with. */
export function payableProvider(payment: Pick<PaymentResponse, 'status' | 'method'>): OnlineProvider | null {
  return payment.status === 'Pending' && isOnlineMethod(payment.method) ? payment.method : null;
}

/** Counter staff can record cash for an unpaid payment of a parcel booked to be paid in cash, unless the booking was cancelled. */
export function canRecordCash(payment: Pick<PaymentResponse, 'status' | 'method'>, isCounterStaff: boolean, parcelStatus: string): boolean {
  return isCounterStaff && payment.status === 'Pending' && payment.method === 'Cash' && parcelStatus !== 'Cancelled';
}

/**
 * The old "Mark as paid" form is only for the methods that have no screen of their own: cash has the cash dialog and the online
 * wallets are paid (and verified by the server) through the payment flow.
 */
export function usesManualConfirmation(method: string): boolean {
  return method !== 'Cash' && method !== 'Esewa' && method !== 'Khalti' && method !== 'CashOnDelivery';
}

export const canShowReceipt = (payment: Pick<PaymentResponse, 'status'>): boolean => payment.status === 'Paid';

export const buildInitiateRequest = (parcelId: string, provider: OnlineProvider): InitiatePaymentRequest => ({ parcelId, provider });

// ---------------------------------------------------------------------------------------------
// Sending the customer to the provider

export type RedirectTarget = { kind: 'internal'; path: string } | { kind: 'external'; url: string };

const TEST_PAGE_PATH = /^\/pay\/test\/(esewa|khalti)\/[A-Za-z0-9_-]+$/i;

/**
 * Decides where the browser may go after a payment was started, or null when the address cannot be trusted. Only http(s)
 * addresses without a user name or password qualify. An address on this website is opened inside the app. In test mode the
 * only other address accepted is the path of our own simulated provider page (so a different host name for the same site still
 * works, and nothing outside the site can ever be reached). Outside test mode an official provider page on another host is
 * allowed. The address never comes from the browser's own URL: only from the server's answer to the initiation.
 */
export function resolveRedirect(redirectUrl: string, testMode: boolean, origin: string): RedirectTarget | null {
  let url: URL;
  let home: URL;
  try {
    home = new URL(origin);
    url = new URL(redirectUrl, home);
  } catch {
    return null;
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
  if (url.username !== '' || url.password !== '') return null;

  if (url.origin === home.origin) return { kind: 'internal', path: `${url.pathname}${url.search}${url.hash}` };
  if (testMode) return TEST_PAGE_PATH.test(url.pathname) ? { kind: 'internal', path: `${url.pathname}${url.search}` } : null;
  return { kind: 'external', url: url.href };
}

// ---------------------------------------------------------------------------------------------
// The return page: only the server decides whether the payment happened

export type ReturnPhase = 'verifying' | 'paid' | 'failed' | 'pending' | 'unreachable' | 'invalid';

export interface ReturnState {
  phase: ReturnPhase;
  /** How many checks have come back without a decision. */
  attempts: number;
  /** True while the page should ask the server again by itself. */
  autoRetry: boolean;
  message: string;
  result: PaymentStatusResponse | null;
}

export type ReturnEvent =
  | { type: 'check' }
  | { type: 'answer'; expected: { reference: string; provider: string }; response: PaymentStatusResponse }
  | { type: 'failure'; httpStatus?: number };

export const MAX_AUTO_CHECKS = 5;
export const AUTO_CHECK_DELAY_MS = 2000;

export const initialReturnState: ReturnState = { phase: 'verifying', attempts: 0, autoRetry: false, message: 'Verifying your payment…', result: null };

/**
 * What the return page shows, as a pure function of what the server said. Success is only ever the server's `Succeeded`:
 * nothing in the address bar (a "status=success" in the URL, say) can produce it. An answer about a different reference or
 * provider is refused, and an unknown status counts as "still waiting", never as paid.
 */
export function nextReturnState(state: ReturnState, event: ReturnEvent): ReturnState {
  if (state.phase === 'paid' || state.phase === 'failed') return state;

  switch (event.type) {
    case 'check':
      return { ...state, phase: 'verifying', autoRetry: false, message: initialReturnState.message };

    case 'failure': {
      const status = event.httpStatus;
      const retryable = status === undefined || status === 429 || status >= 500;
      if (!retryable) {
        return {
          ...state,
          phase: 'invalid',
          autoRetry: false,
          message: status === 404 ? 'We could not find this payment. Check the link, or start the payment again from your parcel.' : 'We could not check this payment.',
        };
      }
      const attempts = state.attempts + 1;
      return {
        ...state,
        phase: 'unreachable',
        attempts,
        autoRetry: attempts < MAX_AUTO_CHECKS,
        message: 'We could not reach the server to check your payment. Your money has not been lost; check again in a moment.',
      };
    }

    case 'answer': {
      const { response, expected } = event;
      const sameReference = response.reference === expected.reference;
      const sameProvider = response.provider.toLowerCase() === expected.provider.toLowerCase();
      if (!sameReference || !sameProvider) {
        return {
          ...state,
          phase: 'invalid',
          autoRetry: false,
          result: null,
          message: 'The answer we received does not belong to this payment, so it was ignored. Please start the payment again from your parcel.',
        };
      }
      if (response.status === 'Succeeded') {
        return { ...state, phase: 'paid', autoRetry: false, result: response, message: 'Payment received. Thank you!' };
      }
      if (response.status === 'Failed') {
        return {
          ...state,
          phase: 'failed',
          autoRetry: false,
          result: response,
          message: response.failureReason ?? 'The payment was not completed. You have not been charged for this attempt.',
        };
      }
      const attempts = state.attempts + 1;
      return {
        ...state,
        phase: 'pending',
        attempts,
        autoRetry: attempts < MAX_AUTO_CHECKS,
        result: response,
        message: 'The provider has not confirmed this payment yet. This can take a minute.',
      };
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Cash at the office

/** The exact amount, in rupees and paisa: no change kept, nothing short. */
export const validateCashReceived = validateCodEntry;

const toPaisa = (amount: number): number => Math.round(amount * 100);

export const amountsEqual = (a: number, b: number): boolean => toPaisa(a) === toPaisa(b);

export const NOTE_MAX_LENGTH = 200;

export function buildOfficeCashRequest(parcelId: string, amountReceived: number, note: string): RecordOfficeCashRequest {
  const text = note.trim();
  return { parcelId, amountReceived, note: text ? text : null };
}

// ---------------------------------------------------------------------------------------------
// Filters as typed in the forms (all strings, so inputs stay controlled) → API queries

export const FINANCE_PAGE_SIZE = 20;

const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;
const asDate = (value: string): string | undefined => (DATE_PATTERN.test(value) ? value : undefined);
const asText = (value: string): string | undefined => value.trim() || undefined;

export interface TransactionFilters {
  provider: string;
  status: string;
  search: string;
  branchId: string;
  from: string;
  to: string;
}

export const emptyTransactionFilters = (): TransactionFilters => ({ provider: '', status: '', search: '', branchId: '', from: '', to: '' });

export function transactionQuery(filters: TransactionFilters, page: number, pageSize = FINANCE_PAGE_SIZE): PaymentTransactionQuery {
  return {
    page,
    pageSize,
    provider: filters.provider || undefined,
    status: filters.status || undefined,
    search: asText(filters.search),
    branchId: filters.branchId || undefined,
    from: asDate(filters.from),
    to: asDate(filters.to),
  };
}

export interface SettlementFilters {
  status: string;
  branchId: string;
  from: string;
  to: string;
}

export const emptySettlementFilters = (): SettlementFilters => ({ status: '', branchId: '', from: '', to: '' });

export function settlementQuery(filters: SettlementFilters, page: number, pageSize = FINANCE_PAGE_SIZE): SettlementQuery {
  return {
    page,
    pageSize,
    status: filters.status || undefined,
    branchId: filters.branchId || undefined,
    from: asDate(filters.from),
    to: asDate(filters.to),
  };
}

export const agentCashQuery = (branchId: string): { branchId?: string } => ({ branchId: branchId || undefined });

export interface ReportFilters {
  from: string;
  to: string;
  branchId: string;
}

/** The last 30 Nepal days including today, like the agent performance report. */
export function defaultReportFilters(now: Date): ReportFilters {
  return { ...defaultPerformanceRange(now), branchId: '' };
}

export const validateReportRange = validatePerformanceRange;

export function reportQuery(filters: ReportFilters): PaymentReportQuery {
  return { from: asDate(filters.from), to: asDate(filters.to), branchId: filters.branchId || undefined };
}

/** Both dates given and in the right order, or an explanation. */
export function validateTransactionRange(from: string, to: string): string | null {
  if (from && to && from > to) return 'The "from" date must not be after the "to" date.';
  return null;
}

// ---------------------------------------------------------------------------------------------
// Settlements: counting the cash

export const REMARKS_MIN_LENGTH = 5;
export const REMARKS_MAX_LENGTH = 500;

export interface CountEntry {
  ok: boolean;
  counted: number | null;
  /** True when the count equals the total exactly, so the settlement will be accepted. */
  matches: boolean;
  /** Counted minus expected, in rupees, when the count is valid. */
  variance: number | null;
  error?: string;
}

const hasMoreThanTwoDecimals = (text: string): boolean => /\.\d{3,}/.test(text.trim());

/** The accountant types the cash they counted. Any amount is allowed, but only the exact total is accepted without a dispute. */
export function validateCount(text: string, expectedTotal: number): CountEntry {
  const fail = (error: string): CountEntry => ({ ok: false, counted: null, matches: false, variance: null, error });
  if (!text.trim()) return fail('Enter the cash you counted.');
  const amount = parseNumber(text);
  if (amount === null || !Number.isFinite(amount) || amount < 0) return fail('Enter the amount as a number, for example 4500 or 4500.50.');
  if (hasMoreThanTwoDecimals(text)) return fail('Use at most two decimal places.');
  const matches = amountsEqual(amount, expectedTotal);
  return { ok: true, counted: amount, matches, variance: (toPaisa(amount) - toPaisa(expectedTotal)) / 100, error: undefined };
}

/** "Short by Rs. 200.00", "Over by Rs. 50.00" or "Matches". */
export function varianceText(variance: number | null | undefined): string {
  if (variance === null || variance === undefined) return '—';
  if (toPaisa(variance) === 0) return 'Matches';
  return variance < 0 ? `Short by ${formatNpr(Math.abs(variance))}` : `Over by ${formatNpr(variance)}`;
}

export function varianceTone(variance: number | null | undefined): string {
  if (variance === null || variance === undefined || toPaisa(variance) === 0) return 'text-slate-700';
  return 'text-red-700';
}

export interface VerifyForm {
  counted: string;
  remarks: string;
}

export const emptyVerifyForm = (): VerifyForm => ({ counted: '', remarks: '' });

/** A count that differs from the total makes the settlement disputed, and a written explanation is then required. */
export function validateVerifyForm(form: VerifyForm, expectedTotal: number): FormErrors {
  const errors: FormErrors = {};
  const count = validateCount(form.counted, expectedTotal);
  if (!count.ok) {
    errors.counted = count.error ?? 'Enter the cash you counted.';
    return errors;
  }
  const remarks = form.remarks.trim();
  if (remarks.length > REMARKS_MAX_LENGTH) errors.remarks = `Use at most ${REMARKS_MAX_LENGTH} characters.`;
  if (!count.matches && remarks.length < REMARKS_MIN_LENGTH) {
    errors.remarks = `The count does not match the total. Explain the difference (at least ${REMARKS_MIN_LENGTH} characters).`;
  }
  return errors;
}

export function buildVerifyRequest(form: VerifyForm): VerifySettlementRequest {
  const remarks = form.remarks.trim();
  return { countedAmount: parseNumber(form.counted) ?? 0, remarks: remarks ? remarks : null };
}

export function validateResolveRemarks(text: string): string | null {
  const remarks = text.trim();
  if (remarks.length < REMARKS_MIN_LENGTH) return `Explain how the difference was settled (at least ${REMARKS_MIN_LENGTH} characters).`;
  if (remarks.length > REMARKS_MAX_LENGTH) return `Use at most ${REMARKS_MAX_LENGTH} characters.`;
  return null;
}

export const buildResolveRequest = (remarks: string): ResolveSettlementRequest => ({ remarks: remarks.trim() });

/** Only a disputed settlement can be resolved; only one waiting for the count can be verified. */
export const canVerifySettlement = (status: string): boolean => status === 'Submitted';
export const canResolveSettlement = (status: string): boolean => status === 'Disputed';

// ---------------------------------------------------------------------------------------------
// The agent handing in cash

const CASH_EPSILON = 0.005;

/** The agent can hand in only when there is cash in hand that has not been handed in. */
export const canHandIn = (row: Pick<AgentCashPositionRow, 'inHand'> | null | undefined): boolean => !!row && row.inHand > CASH_EPSILON;

export const buildCreateSettlementRequest = (remarks: string): CreateSettlementRequest => ({ remarks: remarks.trim() ? remarks.trim() : null });

export function handInMessage(amount: number): string {
  return `You are handing in all the cash you have collected so far: ${formatNpr(amount)}. The accountant will count it and either accept it or mark any difference. You cannot change this after you confirm.`;
}

// ---------------------------------------------------------------------------------------------
// Reports: bars and CSV

/** The width of a bar (0 to 100) for a value against the largest value in its group. */
export function barPercent(value: number, max: number): number {
  if (!Number.isFinite(value) || !Number.isFinite(max) || max <= 0 || value <= 0) return 0;
  return Math.min(100, Math.round((value / max) * 1000) / 10);
}

export const maxOf = (values: readonly number[]): number => values.reduce((a, b) => (b > a ? b : a), 0);

export function successShare(row: { succeeded: number; failed: number }): number | null {
  const finished = row.succeeded + row.failed;
  return finished === 0 ? null : Math.round((row.succeeded / finished) * 1000) / 10;
}

export function reportCsv(report: PaymentReportResponse): string {
  const lines: string[] = [
    csvLine(['Payment report', `${report.from} to ${report.to}`]),
    csvLine(['Delivery charges paid', report.paidCount, report.paidAmount]),
    csvLine(['Waiting to be paid', report.pendingCount, report.pendingAmount]),
    csvLine(['Failed online attempts', report.failedAttempts]),
    '',
    csvLine(['By method', 'Payments', 'Amount (NPR)']),
    ...report.byMethod.map((m) => csvLine([m.method, m.count, m.amount])),
    '',
    csvLine(['By provider', 'Succeeded', 'Failed', 'Waiting', 'Amount (NPR)']),
    ...report.byProvider.map((p) => csvLine([p.provider, p.succeeded, p.failed, p.pending, p.amount])),
    '',
    csvLine(['Day', 'Payments', 'Amount (NPR)']),
    ...report.daily.map((d) => csvLine([d.date, d.count, d.amount])),
    '',
    csvLine(['Cash on delivery', 'Collections', 'Amount (NPR)']),
    csvLine(['Collected', report.cod.collectionCount, report.cod.collected]),
    csvLine(['With agents (not handed in)', '', report.cod.inHand]),
    csvLine(['Handed in, waiting for count', '', report.cod.inSettlement]),
    csvLine(['Count did not match', '', report.cod.disputed]),
    csvLine(['Counted and accepted', '', report.cod.settled]),
    '',
    csvLine(['Settlements', 'Count']),
    csvLine(['Waiting for count', report.settlements.submitted]),
    csvLine(['Counted and accepted', report.settlements.verified]),
    csvLine(['Count did not match', report.settlements.disputed]),
    csvLine(['Total variance (NPR)', report.settlements.totalVariance]),
  ];
  return `${lines.join('\r\n')}\r\n`;
}

export const reportFileName = (report: Pick<PaymentReportResponse, 'from' | 'to'>): string => `payment-report-${report.from}-to-${report.to}.csv`;

export const TRANSACTION_CSV_HEADER = [
  'Created (UTC)',
  'Completed (UTC)',
  'Tracking number',
  'Reference',
  'Provider',
  'Provider reference',
  'Type',
  'Status',
  'Amount',
  'Currency',
  'Failure reason',
] as const;

export function transactionsCsv(rows: readonly PaymentTransactionRow[]): string {
  const lines = [
    csvLine(TRANSACTION_CSV_HEADER),
    ...rows.map((r) =>
      csvLine([
        r.createdAtUtc,
        r.completedAtUtc ?? '',
        r.trackingNumber,
        r.reference,
        providerLabel(r.provider),
        r.providerReference ?? '',
        r.type,
        transactionStatusLabel(r.status),
        r.amount,
        r.currency,
        r.failureReason ?? '',
      ]),
    ),
  ];
  return `${lines.join('\r\n')}\r\n`;
}

export const transactionsFileName = (page: number): string => `payment-transactions-page-${page}.csv`;

export function agentCashCsv(rows: readonly AgentCashPositionRow[]): string {
  const lines = [
    csvLine(['Agent', 'Branch', 'Collected (NPR)', 'In hand (NPR)', 'Waiting for count (NPR)', 'Disputed (NPR)', 'Settled (NPR)', 'Collections', 'Last collection (UTC)']),
    ...rows.map((r) => csvLine([r.fullName, r.branchName, r.collected, r.inHand, r.inSettlement, r.disputed, r.settled, r.collectionCount, r.lastCollectionAtUtc ?? ''])),
  ];
  return `${lines.join('\r\n')}\r\n`;
}

export const agentCashFileName = 'agent-cash.csv';
