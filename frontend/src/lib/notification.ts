import type {
  CreateOptOutRequest,
  NotificationChannel,
  NotificationMessageQuery,
  NotificationMessageResponse,
  NotificationTemplateResponse,
  OptOutQuery,
  SimulatedStatus,
  UpdateNotificationPreferencesRequest,
} from '../types/api';
import { NOTIFICATION_AUDIENCES, NOTIFICATION_CHANNELS, NOTIFICATION_DELIVERY_STATUSES, NOTIFICATION_STATUSES } from '../types/api';
import { buildDeliveryQuery, csvLine } from './delivery';
import { NEPAL_TIME_ZONE } from './format';
import { notificationApi } from './notificationApi';

// ---------------------------------------------------------------------------------------------
// What state is a message in?
//
// Two fields are kept strictly apart. `status` is OUR processing state (queued, handed to the provider, given up, skipped).
// `deliveryStatus` is only what the PROVIDER told us afterwards (sent, delivered, read, failed). A message the provider merely
// accepted is Submitted with a delivery status of None, and must never be shown as delivered.

/** The places a message can be on its way: Queued -> Submitted -> Sent -> Delivered -> Read; or it ended as Failed, Skipped or Cancelled. */
export type MessageStep = 'Queued' | 'Submitted' | 'Sent' | 'Delivered' | 'Read' | 'Failed' | 'Skipped' | 'Cancelled';

export type StateTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger';

export interface MessageState {
  label: string;
  tone: StateTone;
  step: MessageStep;
}

/** The two fields that decide the state. Plain strings, so a value this app has never heard of cannot crash the screen. */
export interface MessageStateInput {
  status: string;
  deliveryStatus: string;
}

/** The steps of a message that went well, in order. */
export const PROGRESS_STEPS: readonly MessageStep[] = ['Queued', 'Submitted', 'Sent', 'Delivered', 'Read'];

/** The position of a step on the way to being read (0 to 4), or -1 for the steps where the message stopped (failed, skipped, cancelled). */
export function stepIndex(step: MessageStep): number {
  return PROGRESS_STEPS.indexOf(step);
}

/**
 * What to show for a message.
 *
 * - The provider's report wins whenever there is one (delivery status other than None): it is evidence of what really happened
 *   to the message, so it is shown even if our own record lags behind (a webhook can arrive before we have saved "submitted").
 * - With no report yet, our own status decides. Submitted is "accepted by the provider, waiting for the delivery report":
 *   never delivered.
 */
export function describeMessageState(message: MessageStateInput): MessageState {
  switch (message.deliveryStatus) {
    case 'Read':
      return { label: 'Read by the recipient', tone: 'success', step: 'Read' };
    case 'Delivered':
      return { label: 'Delivered to the phone', tone: 'success', step: 'Delivered' };
    case 'Sent':
      return { label: 'Sent by the provider – delivery not yet confirmed', tone: 'info', step: 'Sent' };
    case 'Failed':
      return { label: 'Delivery failed (reported by the provider)', tone: 'danger', step: 'Failed' };
    default:
      break; // None, or something unknown: our own status decides
  }

  switch (message.status) {
    case 'Pending':
      return { label: 'Queued – waiting to be sent', tone: 'neutral', step: 'Queued' };
    case 'Processing':
      return { label: 'Being sent now', tone: 'info', step: 'Queued' };
    case 'Submitted':
      return { label: 'Accepted by provider – waiting for delivery report', tone: 'info', step: 'Submitted' };
    case 'Failed':
      return { label: 'Failed – will retry', tone: 'warning', step: 'Queued' };
    case 'DeadLetter':
      return { label: 'Failed – gave up, no more retries', tone: 'danger', step: 'Failed' };
    case 'Skipped':
      return { label: 'Skipped – not sent', tone: 'neutral', step: 'Skipped' };
    case 'Cancelled':
      return { label: 'Cancelled – not sent', tone: 'neutral', step: 'Cancelled' };
    default:
      return { label: message.status || 'Unknown', tone: 'neutral', step: 'Queued' };
  }
}

export const STATE_TONE_CLASSES: Record<StateTone, string> = {
  neutral: 'bg-slate-100 text-slate-700',
  info: 'bg-blue-50 text-blue-700',
  success: 'bg-emerald-50 text-emerald-700',
  warning: 'bg-amber-50 text-amber-800',
  danger: 'bg-red-50 text-red-700',
};

/** A failed or given-up message may be tried again now (SuperAdmin). */
export const canRequeue = (message: Pick<NotificationMessageResponse, 'status'>): boolean =>
  message.status === 'Failed' || message.status === 'DeadLetter';

/** A queued or failed message may be stopped (SuperAdmin). */
export const canCancelMessage = (message: Pick<NotificationMessageResponse, 'status'>): boolean =>
  message.status === 'Pending' || message.status === 'Failed';

/** Failed, but the system will try again by itself. */
export const isRetrying = (message: Pick<NotificationMessageResponse, 'status'>): boolean => message.status === 'Failed';

export const attemptsLabel = (message: Pick<NotificationMessageResponse, 'attemptCount' | 'maxAttempts'>): string =>
  `${message.attemptCount}/${message.maxAttempts}`;

/** "NoConsent" or "no_consent" -> "No consent". A text that already has spaces is a sentence and is left alone. */
export function humanize(text: string): string {
  const trimmed = text.trim();
  if (!trimmed || /\s/.test(trimmed)) return trimmed;
  const spaced = trimmed
    .replace(/[-_]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/** The template key as words: "booking-created" -> "Booking created". */
export const templateKeyLabel = humanize;

/** The one line that explains a skipped or failed message, or null when there is nothing to explain. */
export function messageNote(message: NotificationMessageResponse): string | null {
  if (message.deliveryStatus === 'Failed') {
    return message.deliveryError?.trim() || 'The provider reported that it could not deliver this message.';
  }
  if (message.status === 'Skipped') {
    const reason = message.skipReason?.trim();
    return reason ? `Skipped: ${humanize(reason)}` : 'Skipped.';
  }
  if ((message.status === 'Failed' || message.status === 'DeadLetter') && message.lastError?.trim()) {
    return message.lastError.trim();
  }
  return null;
}

// ---------------------------------------------------------------------------------------------
// Labels and filter options

export const CHANNEL_LABELS: Record<NotificationChannel, string> = { Sms: 'SMS', WhatsApp: 'WhatsApp' };

export const channelLabel = (channel: string): string => (channel === 'Sms' ? 'SMS' : channel);

export const STATUS_LABELS: Record<string, string> = {
  Pending: 'Queued (pending)',
  Processing: 'Processing',
  Submitted: 'Submitted (accepted by provider)',
  Failed: 'Failed (will retry)',
  DeadLetter: 'Dead letter (gave up)',
  Skipped: 'Skipped',
  Cancelled: 'Cancelled',
};

export const DELIVERY_LABELS: Record<string, string> = {
  None: 'No report yet',
  Sent: 'Sent',
  Delivered: 'Delivered',
  Read: 'Read',
  Failed: 'Failed',
};

export const CHANNEL_OPTIONS = NOTIFICATION_CHANNELS.map((value) => ({ value, label: CHANNEL_LABELS[value] }));
export const STATUS_OPTIONS = NOTIFICATION_STATUSES.map((value) => ({ value, label: STATUS_LABELS[value] ?? value }));
export const DELIVERY_OPTIONS = NOTIFICATION_DELIVERY_STATUSES.map((value) => ({ value, label: DELIVERY_LABELS[value] ?? value }));
export const AUDIENCE_OPTIONS = NOTIFICATION_AUDIENCES.map((value) => ({ value, label: value }));

// ---------------------------------------------------------------------------------------------
// The message list: filters and query

export interface MessageFilters {
  channel: string;
  status: string;
  deliveryStatus: string;
  audience: string;
  templateKey: string;
  search: string;
  from: string;
  to: string;
}

export const MESSAGES_PAGE_SIZE = 20;

export const emptyMessageFilters = (): MessageFilters => ({
  channel: '',
  status: '',
  deliveryStatus: '',
  audience: '',
  templateKey: '',
  search: '',
  from: '',
  to: '',
});

const blankToUndefined = (value: string): string | undefined => value.trim() || undefined;

/** The filters as the server's query: blank filters are left out. */
export function messageQuery(filters: MessageFilters, page: number, pageSize = MESSAGES_PAGE_SIZE): NotificationMessageQuery {
  return {
    page,
    pageSize,
    channel: blankToUndefined(filters.channel),
    status: blankToUndefined(filters.status),
    deliveryStatus: blankToUndefined(filters.deliveryStatus),
    audience: blankToUndefined(filters.audience),
    templateKey: blankToUndefined(filters.templateKey),
    search: blankToUndefined(filters.search),
    from: blankToUndefined(filters.from),
    to: blankToUndefined(filters.to),
  };
}

export const messagesPath = (filters: MessageFilters, page: number, pageSize = MESSAGES_PAGE_SIZE): string =>
  `${notificationApi.messages}${buildDeliveryQuery(messageQuery(filters, page, pageSize))}`;

const isOneOf = (list: readonly string[], value: string | null): value is string => value !== null && list.includes(value);

/** Filters given in the address (for example from the dead-letter link). Values the server would not know are ignored. */
export function filtersFromSearch(params: URLSearchParams): MessageFilters {
  const filters = emptyMessageFilters();
  const channel = params.get('channel');
  const status = params.get('status');
  const deliveryStatus = params.get('deliveryStatus');
  const audience = params.get('audience');
  if (isOneOf(NOTIFICATION_CHANNELS, channel)) filters.channel = channel;
  if (isOneOf(NOTIFICATION_STATUSES, status)) filters.status = status;
  if (isOneOf(NOTIFICATION_DELIVERY_STATUSES, deliveryStatus)) filters.deliveryStatus = deliveryStatus;
  if (isOneOf(NOTIFICATION_AUDIENCES, audience)) filters.audience = audience;
  return filters;
}

/** The address of the Messages tab showing the messages that were given up on. */
export const DEAD_LETTER_LINK = '/admin/notifications?tab=messages&status=DeadLetter';

/** The date range must not run backwards. Returns the problem as a sentence, or null. */
export function validateDateRange(from: string, to: string): string | null {
  if (from && to && from > to) return 'The start date must not be after the end date.';
  return null;
}

// ---------------------------------------------------------------------------------------------
// The overview period (Nepal calendar days)

const nepalDateFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: NEPAL_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

/** Today's date in Nepal as yyyy-MM-dd. */
export const nepalToday = (now: Date = new Date()): string => nepalDateFormatter.format(now);

/** Moves a yyyy-MM-dd date by a number of days (negative = earlier). */
export function addDays(isoDate: string, days: number): string {
  const [year = 0, month = 1, day = 1] = isoDate.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day + days)).toISOString().slice(0, 10);
}

/** The last `days` Nepal days up to and including today. */
export function lastDaysRange(days: number, now: Date = new Date()): { from: string; to: string } {
  const to = nepalToday(now);
  return { from: addDays(to, -(Math.max(1, days) - 1)), to };
}

export const DEFAULT_PERIOD_DAYS = 7;

export const PERIOD_PRESETS = [
  { value: '7', label: 'Last 7 days', days: 7 },
  { value: '30', label: 'Last 30 days', days: 30 },
  { value: '90', label: 'Last 90 days', days: 90 },
] as const;

export const summaryPath = (from: string, to: string): string => `${notificationApi.summary}${buildDeliveryQuery({ from, to })}`;

// ---------------------------------------------------------------------------------------------
// Admin tabs

export const NOTIFICATION_TABS = ['overview', 'messages', 'templates', 'opt-outs', 'test-inbox'] as const;
export type NotificationTab = (typeof NOTIFICATION_TABS)[number];

export const TAB_LABELS: Record<NotificationTab, string> = {
  overview: 'Overview',
  messages: 'Messages',
  templates: 'Templates',
  'opt-outs': 'Opt-outs',
  'test-inbox': 'Test inbox',
};

/** The tabs this person may see. The test inbox is for super admins, and only while a mock provider is active. */
export function visibleTabs(options: { canUseMockInbox: boolean; mockInboxAvailable: boolean }): NotificationTab[] {
  return NOTIFICATION_TABS.filter((tab) => tab !== 'test-inbox' || (options.canUseMockInbox && options.mockInboxAvailable));
}

/** The tab named in the address, or the first visible one. */
export function parseTab(value: string | null, visible: readonly NotificationTab[]): NotificationTab {
  const found = visible.find((tab) => tab === value);
  return found ?? visible[0] ?? 'overview';
}

/**
 * The test inbox exists only while a mock provider is active: the server answers 404 otherwise. While the question is still
 * being answered, or when it failed in some other way that is not a 404, the tab stays hidden unless we know it is there.
 */
export function mockInboxAvailable(probe: { status: string; httpStatus?: number }): boolean {
  if (probe.status === 'loaded') return true;
  return probe.status === 'error' && probe.httpStatus !== undefined && probe.httpStatus !== 404;
}

// ---------------------------------------------------------------------------------------------
// Templates

export interface TemplateGroup {
  key: string;
  sms?: NotificationTemplateResponse;
  whatsApp?: NotificationTemplateResponse;
}

/** One row per template key with its SMS and WhatsApp versions side by side. Sorted by key. */
export function groupTemplates(templates: readonly NotificationTemplateResponse[]): TemplateGroup[] {
  const byKey = new Map<string, TemplateGroup>();
  for (const template of templates) {
    const group = byKey.get(template.key) ?? { key: template.key };
    if (template.channel === 'Sms') group.sms = template;
    else if (template.channel === 'WhatsApp') group.whatsApp = template;
    byKey.set(template.key, group);
  }
  return [...byKey.values()].sort((a, b) => a.key.localeCompare(b.key));
}

/**
 * The placeholders a template may use. The server lists them per template key; if there is no entry for the key (or the list is
 * keyed by channel instead) fall back to that, and then to every placeholder that is listed anywhere.
 */
export function placeholdersFor(allowed: Readonly<Record<string, readonly string[]>>, key: string, channel: string): string[] {
  const exact = allowed[key] ?? allowed[channel];
  if (exact) return [...exact];
  return [...new Set(Object.values(allowed).flat())];
}

const PLACEHOLDER_PATTERN = /\{([A-Za-z][A-Za-z0-9_]*)\}/g;

/** The placeholders used in a body, each once, in the order they first appear (the order WhatsApp numbers its fields). */
export function usedPlaceholders(body: string): string[] {
  const seen: string[] = [];
  for (const match of body.matchAll(PLACEHOLDER_PATTERN)) {
    const name = match[1];
    if (name && !seen.includes(name)) seen.push(name);
  }
  return seen;
}

export const placeholderToken = (name: string): string => `{${name}}`;

/** Puts text in place of the current selection and says where the cursor goes next (right after the inserted text). */
export function insertAtSelection(text: string, selectionStart: number, selectionEnd: number, insertion: string): { text: string; cursor: number } {
  const start = Math.max(0, Math.min(selectionStart, text.length));
  const end = Math.max(start, Math.min(selectionEnd, text.length));
  return { text: `${text.slice(0, start)}${insertion}${text.slice(end)}`, cursor: start + insertion.length };
}

// The GSM 03.38 default alphabet and its extension table (each extension character takes two places).
const GSM_BASIC = new Set(
  '@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !"#¤%&\'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà',
);
const GSM_EXTENDED = new Set('^{}\\[~]|€\f');

export interface SmsSegments {
  encoding: 'GSM' | 'Unicode';
  /** Places used: characters for GSM (extension characters count twice), UTF-16 units for Unicode. */
  units: number;
  segments: number;
  /** How many places fit in one SMS when this text is sent as one part. */
  singleLimit: number;
  /** How many places fit in each part when it needs more than one. */
  multiLimit: number;
}

/**
 * How many SMS parts a text needs. Plain text (the GSM alphabet) fits 160 places in one SMS, or 153 per part in a longer
 * message; any other character (Nepali, emoji, curly quotes) switches the whole message to Unicode: 70, or 67 per part.
 */
export function smsSegments(text: string): SmsSegments {
  let units = 0;
  let gsm = true;
  for (const char of text) {
    if (GSM_BASIC.has(char)) units += 1;
    else if (GSM_EXTENDED.has(char)) units += 2;
    else {
      gsm = false;
      break;
    }
  }
  const encoding = gsm ? 'GSM' : 'Unicode';
  if (!gsm) units = text.length;
  const singleLimit = gsm ? 160 : 70;
  const multiLimit = gsm ? 153 : 67;
  const segments = units === 0 ? 0 : units <= singleLimit ? 1 : Math.ceil(units / multiLimit);
  return { encoding, units, segments, singleLimit, multiLimit };
}

export function smsHint(text: string): string {
  const info = smsSegments(text);
  if (info.segments === 0) return 'Nothing to send yet.';
  const parts = info.segments === 1 ? '1 SMS' : `${info.segments} SMS parts`;
  const alphabet =
    info.encoding === 'GSM'
      ? 'plain characters (160 per SMS)'
      : 'it has characters outside the plain SMS alphabet, so it is sent as Unicode (70 per SMS)';
  return `${info.units} characters = ${parts}; ${alphabet}.`;
}

/** Saving is blocked while the template is empty or the server's check found problems, and while a fresh check is still running. */
export function canSaveTemplate(state: { body: string; checking: boolean; problems: readonly string[] }): boolean {
  return state.body.trim().length > 0 && !state.checking && state.problems.length === 0;
}

// ---------------------------------------------------------------------------------------------
// Opt-outs

export interface OptOutFilters {
  channel: string;
  activeOnly: boolean;
}

export const emptyOptOutFilters = (): OptOutFilters => ({ channel: '', activeOnly: true });

export function optOutQuery(filters: OptOutFilters, page: number, pageSize = 20): OptOutQuery {
  return { page, pageSize, channel: blankToUndefined(filters.channel), activeOnly: filters.activeOnly };
}

export const optOutsPath = (filters: OptOutFilters, page: number): string =>
  `${notificationApi.optOuts}${buildDeliveryQuery(optOutQuery(filters, page))}`;

/** Spaces and hyphens removed; a leading + kept. */
export const normalisePhone = (raw: string): string => raw.replace(/[\s-]/g, '');

/** A phone number to block: 7 to 15 digits, with an optional leading +. Returns the problem as a sentence, or null. */
export function validateOptOutPhone(raw: string): string | null {
  const phone = normalisePhone(raw);
  if (!phone) return 'Enter the phone number.';
  if (!/^\+?[0-9]{7,15}$/.test(phone)) return 'Enter a phone number with 7 to 15 digits, for example +9779812345678.';
  return null;
}

export function buildOptOutRequest(rawPhone: string, channel: string): CreateOptOutRequest | null {
  if (validateOptOutPhone(rawPhone) !== null) return null;
  if (!isOneOf(NOTIFICATION_CHANNELS, channel)) return null;
  return { phoneNumber: normalisePhone(rawPhone), channel: channel as NotificationChannel };
}

// ---------------------------------------------------------------------------------------------
// Consent

export interface ConsentChoices {
  sms: boolean;
  whatsApp: boolean;
}

/**
 * What to send after a booking: only the channels that were ticked. An unticked box means "no decision made here", so it
 * never switches off an earlier yes. Null when nothing was ticked (nothing to send).
 */
export function bookingConsentRequest(choices: ConsentChoices): UpdateNotificationPreferencesRequest | null {
  if (!choices.sms && !choices.whatsApp) return null;
  const request: UpdateNotificationPreferencesRequest = {};
  if (choices.sms) request.sms = true;
  if (choices.whatsApp) request.whatsApp = true;
  return request;
}

/**
 * The booking request with the ticked consent choices added. They travel with the booking, not after it: the server decides who is
 * messaged about the booking in the same step that creates it, so a choice saved afterwards would miss the first message. An
 * unticked box adds nothing, so it never switches off an earlier yes.
 */
export function withBookingConsent<T extends object>(
  request: T,
  choices: ConsentChoices,
): T & { notifyBySms?: true; notifyByWhatsApp?: true } {
  return {
    ...request,
    ...(choices.sms ? { notifyBySms: true as const } : {}),
    ...(choices.whatsApp ? { notifyByWhatsApp: true as const } : {}),
  };
}

/** How saving the consent choices went after a booking. Booking never waits for it and never fails because of it. */
export type ConsentOutcome = { kind: 'saved'; channels: string } | { kind: 'failed' };

/** The outcome handed from the booking page to the parcel page in the navigation state, or null when there is none. */
export function parseConsentOutcome(state: unknown): ConsentOutcome | null {
  if (typeof state !== 'object' || state === null) return null;
  const consent = (state as { consent?: unknown }).consent;
  if (typeof consent !== 'object' || consent === null) return null;
  const { kind, channels } = consent as { kind?: unknown; channels?: unknown };
  if (kind === 'failed') return { kind: 'failed' };
  if (kind === 'saved' && typeof channels === 'string' && channels) return { kind: 'saved', channels };
  return null;
}

/** "SMS", "WhatsApp" or "SMS and WhatsApp". */
export function chosenChannelsLabel(choices: ConsentChoices): string {
  if (choices.sms && choices.whatsApp) return 'SMS and WhatsApp';
  if (choices.sms) return 'SMS';
  if (choices.whatsApp) return 'WhatsApp';
  return 'none';
}

// ---------------------------------------------------------------------------------------------
// The mock provider's test inbox

export const SIMULATE_LABELS: Record<SimulatedStatus, string> = {
  Sent: 'Report sent',
  Delivered: 'Report delivered',
  Read: 'Report read',
  Failed: 'Report failed',
};

export const mockMessagesPath = (page: number, pageSize = 20): string =>
  `${notificationApi.mockMessages}${buildDeliveryQuery({ page, pageSize })}`;

// ---------------------------------------------------------------------------------------------
// Errors

/** A server error as a sentence: the server's own explanation where there is one. */
export function notificationErrorMessage(status: number | undefined, detail: string | undefined): string {
  if (detail && detail.trim()) return detail.trim();
  switch (status) {
    case 401:
      return 'Please sign in again.';
    case 403:
      return 'You are not allowed to do this.';
    case 404:
      return 'That could not be found. It may have been removed.';
    case 409:
      return 'Someone else changed this at the same time. Reload and try again.';
    default:
      return status === undefined ? 'Could not reach the server. Please try again shortly.' : 'Something went wrong. Please try again.';
  }
}

// ---------------------------------------------------------------------------------------------
// CSV export of the messages on the current page

export const MESSAGE_CSV_HEADER = [
  'Created (UTC)',
  'Channel',
  'Audience',
  'Template',
  'Tracking number',
  'Recipient',
  'Processing status',
  'Delivery status',
  'State',
  'Provider',
  'Provider message id',
  'Attempts',
  'Next attempt (UTC)',
  'Submitted (UTC)',
  'Sent (UTC)',
  'Delivered (UTC)',
  'Read (UTC)',
  'Failed (UTC)',
  'Note',
] as const;

export function messagesCsv(messages: readonly NotificationMessageResponse[]): string {
  const lines = [csvLine(MESSAGE_CSV_HEADER)];
  for (const m of messages) {
    lines.push(
      csvLine([
        m.createdAtUtc,
        channelLabel(m.channel),
        m.audience,
        m.templateKey,
        m.trackingNumber,
        m.recipientMasked,
        m.status,
        m.deliveryStatus,
        describeMessageState(m).label,
        m.provider,
        m.providerMessageId,
        attemptsLabel(m),
        isRetrying(m) ? m.nextAttemptAtUtc : null,
        m.submittedAtUtc,
        m.sentAtUtc,
        m.deliveredAtUtc,
        m.readAtUtc,
        m.failedAtUtc,
        messageNote(m),
      ]),
    );
  }
  return `${lines.join('\r\n')}\r\n`;
}

export const messagesFileName = (page: number): string => `notification-messages-page-${page}.csv`;
