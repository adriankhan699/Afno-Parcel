import { describe, expect, it } from 'vitest';
import {
  AUDIENCE_OPTIONS,
  CHANNEL_OPTIONS,
  DEAD_LETTER_LINK,
  DELIVERY_OPTIONS,
  MESSAGE_CSV_HEADER,
  PROGRESS_STEPS,
  STATUS_OPTIONS,
  addDays,
  attemptsLabel,
  bookingConsentRequest,
  withBookingConsent,
  buildOptOutRequest,
  canCancelMessage,
  canRequeue,
  canSaveTemplate,
  chosenChannelsLabel,
  describeMessageState,
  emptyMessageFilters,
  emptyOptOutFilters,
  filtersFromSearch,
  groupTemplates,
  humanize,
  insertAtSelection,
  isRetrying,
  lastDaysRange,
  messageNote,
  messageQuery,
  messagesCsv,
  messagesFileName,
  messagesPath,
  mockInboxAvailable,
  mockMessagesPath,
  nepalToday,
  normalisePhone,
  parseConsentOutcome,
  notificationErrorMessage,
  optOutsPath,
  parseTab,
  placeholdersFor,
  placeholderToken,
  smsHint,
  smsSegments,
  stepIndex,
  summaryPath,
  usedPlaceholders,
  validateDateRange,
  validateOptOutPhone,
  visibleTabs,
  type MessageStep,
} from './notification';
import {
  NOTIFICATION_DELIVERY_STATUSES,
  NOTIFICATION_STATUSES,
  type NotificationMessageResponse,
  type NotificationTemplateResponse,
} from '../types/api';

function message(overrides: Partial<NotificationMessageResponse> = {}): NotificationMessageResponse {
  return {
    id: 'm1',
    channel: 'Sms',
    audience: 'Sender',
    templateKey: 'booking-created',
    recipientMasked: '+97798*****78',
    status: 'Pending',
    deliveryStatus: 'None',
    provider: 'mock-sms',
    providerMessageId: null,
    parcelId: 'p1',
    trackingNumber: 'NP-1',
    attemptCount: 0,
    maxAttempts: 5,
    createdAtUtc: '2026-11-19T04:00:00Z',
    nextAttemptAtUtc: '2026-11-19T04:00:00Z',
    submittedAtUtc: null,
    sentAtUtc: null,
    deliveredAtUtc: null,
    readAtUtc: null,
    failedAtUtc: null,
    skipReason: null,
    lastError: null,
    deliveryError: null,
    ...overrides,
  };
}

describe('describeMessageState: our status and the provider report are kept apart', () => {
  it('a submitted message with no report is "accepted by provider", never delivered', () => {
    const state = describeMessageState({ status: 'Submitted', deliveryStatus: 'None' });
    expect(state.label).toBe('Accepted by provider – waiting for delivery report');
    expect(state.step).toBe('Submitted');
    expect(state.tone).toBe('info');
    expect(state.label.toLowerCase()).not.toContain('delivered');
  });

  // Every status with no provider report yet: the step and tone come from our own status only.
  it.each<[string, MessageStep, string]>([
    ['Pending', 'Queued', 'neutral'],
    ['Processing', 'Queued', 'info'],
    ['Submitted', 'Submitted', 'info'],
    ['Failed', 'Queued', 'warning'],
    ['DeadLetter', 'Failed', 'danger'],
    ['Skipped', 'Skipped', 'neutral'],
    ['Cancelled', 'Cancelled', 'neutral'],
  ])('status %s with no report -> step %s (%s)', (status, step, tone) => {
    const state = describeMessageState({ status, deliveryStatus: 'None' });
    expect(state.step).toBe(step);
    expect(state.tone).toBe(tone);
  });

  it('a failed attempt that will be retried is a warning, a message given up on is an error', () => {
    expect(describeMessageState({ status: 'Failed', deliveryStatus: 'None' }).label).toBe('Failed – will retry');
    expect(describeMessageState({ status: 'DeadLetter', deliveryStatus: 'None' }).label).toMatch(/gave up/);
  });

  // Every combination of the two fields: a provider report always decides the step, whatever our status says.
  const reported: Record<string, MessageStep> = { Sent: 'Sent', Delivered: 'Delivered', Read: 'Read', Failed: 'Failed' };
  const combos = NOTIFICATION_STATUSES.flatMap((status) => NOTIFICATION_DELIVERY_STATUSES.map((deliveryStatus) => [status, deliveryStatus] as const));

  it.each(combos)('status %s + delivery %s gives a sensible state', (status, deliveryStatus) => {
    const state = describeMessageState({ status, deliveryStatus });
    expect(state.label.length).toBeGreaterThan(0);
    if (deliveryStatus === 'None') {
      expect(['Queued', 'Submitted', 'Failed', 'Skipped', 'Cancelled']).toContain(state.step);
      // Without a provider report nothing may look delivered or read.
      expect(state.step).not.toBe('Delivered');
      expect(state.step).not.toBe('Read');
      expect(state.step).not.toBe('Sent');
    } else {
      expect(state.step).toBe(reported[deliveryStatus]);
    }
  });

  it('shows delivery states, with the right tones', () => {
    expect(describeMessageState({ status: 'Submitted', deliveryStatus: 'Sent' })).toEqual({
      label: 'Sent by the provider – delivery not yet confirmed',
      tone: 'info',
      step: 'Sent',
    });
    expect(describeMessageState({ status: 'Submitted', deliveryStatus: 'Delivered' })).toMatchObject({ step: 'Delivered', tone: 'success' });
    expect(describeMessageState({ status: 'Submitted', deliveryStatus: 'Read' })).toMatchObject({ step: 'Read', tone: 'success' });
    expect(describeMessageState({ status: 'Submitted', deliveryStatus: 'Failed' })).toMatchObject({ step: 'Failed', tone: 'danger' });
  });

  describe('out-of-order reports', () => {
    it('a delivery report that arrived before we recorded "submitted" still shows the report', () => {
      expect(describeMessageState({ status: 'Processing', deliveryStatus: 'Delivered' }).step).toBe('Delivered');
      expect(describeMessageState({ status: 'Pending', deliveryStatus: 'Sent' }).step).toBe('Sent');
      expect(describeMessageState({ status: 'Failed', deliveryStatus: 'Read' }).step).toBe('Read');
    });

    it('a read report shows as read even though we never saw "sent" or "delivered" (the earlier steps are implied)', () => {
      const state = describeMessageState({ status: 'Submitted', deliveryStatus: 'Read' });
      expect(state.step).toBe('Read');
      expect(stepIndex(state.step)).toBe(PROGRESS_STEPS.length - 1);
    });

    it('a message cancelled or skipped by us but reported delivered by the provider shows the delivery (the provider knows best)', () => {
      expect(describeMessageState({ status: 'Cancelled', deliveryStatus: 'Delivered' }).step).toBe('Delivered');
      expect(describeMessageState({ status: 'Skipped', deliveryStatus: 'Sent' }).step).toBe('Sent');
    });

    it('a failure report on a message we gave up on is a failure', () => {
      expect(describeMessageState({ status: 'DeadLetter', deliveryStatus: 'Failed' }).step).toBe('Failed');
    });
  });

  it('does not crash on values it has never seen', () => {
    expect(describeMessageState({ status: 'Brand-new', deliveryStatus: 'None' })).toEqual({ label: 'Brand-new', tone: 'neutral', step: 'Queued' });
    expect(describeMessageState({ status: 'Submitted', deliveryStatus: 'Mystery' }).step).toBe('Submitted');
    expect(describeMessageState({ status: '', deliveryStatus: '' }).label).toBe('Unknown');
  });
});

describe('steps', () => {
  it('runs Queued, Submitted, Sent, Delivered, Read; the stopped states are off the path', () => {
    expect(PROGRESS_STEPS).toEqual(['Queued', 'Submitted', 'Sent', 'Delivered', 'Read']);
    expect(['Queued', 'Submitted', 'Sent', 'Delivered', 'Read'].map((s) => stepIndex(s as MessageStep))).toEqual([0, 1, 2, 3, 4]);
    for (const stopped of ['Failed', 'Skipped', 'Cancelled'] as const) expect(stepIndex(stopped)).toBe(-1);
  });
});

describe('what an administrator may do with a message', () => {
  it.each([
    ['Pending', false, true],
    ['Processing', false, false],
    ['Submitted', false, false],
    ['Failed', true, true],
    ['DeadLetter', true, false],
    ['Skipped', false, false],
    ['Cancelled', false, false],
  ])('%s: retry now = %s, cancel = %s', (status, retry, cancel) => {
    expect(canRequeue({ status } as NotificationMessageResponse)).toBe(retry);
    expect(canCancelMessage({ status } as NotificationMessageResponse)).toBe(cancel);
  });

  it('only a failed message is "retrying"', () => {
    expect(isRetrying(message({ status: 'Failed' }))).toBe(true);
    expect(isRetrying(message({ status: 'DeadLetter' }))).toBe(false);
    expect(isRetrying(message({ status: 'Pending' }))).toBe(false);
  });

  it('shows attempts as n/max', () => {
    expect(attemptsLabel({ attemptCount: 2, maxAttempts: 5 })).toBe('2/5');
  });
});

describe('messageNote', () => {
  it('explains a skipped message with its reason in words', () => {
    expect(messageNote(message({ status: 'Skipped', skipReason: 'NoConsent' }))).toBe('Skipped: No consent');
    expect(messageNote(message({ status: 'Skipped', skipReason: 'no_template' }))).toBe('Skipped: No template');
    expect(messageNote(message({ status: 'Skipped', skipReason: 'Number has opted out.' }))).toBe('Skipped: Number has opted out.');
    expect(messageNote(message({ status: 'Skipped' }))).toBe('Skipped.');
  });

  it('shows the provider failure for a failed delivery, and our own error for a failed or given-up send', () => {
    expect(messageNote(message({ status: 'Submitted', deliveryStatus: 'Failed', deliveryError: 'Handset off' }))).toBe('Handset off');
    expect(messageNote(message({ status: 'Submitted', deliveryStatus: 'Failed' }))).toMatch(/could not deliver/);
    expect(messageNote(message({ status: 'Failed', lastError: ' Timeout ' }))).toBe('Timeout');
    expect(messageNote(message({ status: 'DeadLetter', lastError: 'Rejected' }))).toBe('Rejected');
  });

  it('has nothing to say for a healthy message', () => {
    expect(messageNote(message({ status: 'Submitted' }))).toBeNull();
    expect(messageNote(message({ status: 'Pending' }))).toBeNull();
    expect(messageNote(message({ status: 'Failed' }))).toBeNull();
  });
});

describe('humanize', () => {
  it('turns keys into words and leaves sentences alone', () => {
    expect(humanize('booking-created')).toBe('Booking created');
    expect(humanize('OutForDelivery')).toBe('Out for delivery');
    expect(humanize('delivery_failed')).toBe('Delivery failed');
    expect(humanize('Already a sentence.')).toBe('Already a sentence.');
    expect(humanize('  ')).toBe('');
  });
});

describe('filter options', () => {
  it('offer every status, delivery status, channel and audience', () => {
    expect(STATUS_OPTIONS.map((o) => o.value)).toEqual([...NOTIFICATION_STATUSES]);
    expect(DELIVERY_OPTIONS.map((o) => o.value)).toEqual([...NOTIFICATION_DELIVERY_STATUSES]);
    expect(CHANNEL_OPTIONS).toEqual([
      { value: 'Sms', label: 'SMS' },
      { value: 'WhatsApp', label: 'WhatsApp' },
    ]);
    expect(AUDIENCE_OPTIONS.map((o) => o.value)).toEqual(['Sender', 'Receiver']);
  });

  it('say in the labels that submitted only means accepted', () => {
    expect(STATUS_OPTIONS.find((o) => o.value === 'Submitted')?.label).toMatch(/accepted by provider/);
    expect(DELIVERY_OPTIONS.find((o) => o.value === 'None')?.label).toBe('No report yet');
  });
});

describe('message query', () => {
  it('leaves blank filters out and trims the rest', () => {
    const filters = { ...emptyMessageFilters(), channel: 'Sms', search: '  NP-12 ', from: '2026-11-01' };
    expect(messageQuery(filters, 3)).toEqual({
      page: 3,
      pageSize: 20,
      channel: 'Sms',
      status: undefined,
      deliveryStatus: undefined,
      audience: undefined,
      templateKey: undefined,
      search: 'NP-12',
      from: '2026-11-01',
      to: undefined,
    });
  });

  it('builds the address with only the filters that are set', () => {
    expect(messagesPath(emptyMessageFilters(), 1)).toBe('/api/v1/notifications/messages?page=1&pageSize=20');
    expect(messagesPath({ ...emptyMessageFilters(), status: 'DeadLetter', deliveryStatus: 'None', search: 'NP 1&2' }, 2)).toBe(
      '/api/v1/notifications/messages?page=2&pageSize=20&status=DeadLetter&deliveryStatus=None&search=NP+1%262',
    );
  });

  it('reads filters from the address and ignores values the server would not know', () => {
    expect(filtersFromSearch(new URLSearchParams('tab=messages&status=DeadLetter'))).toMatchObject({ status: 'DeadLetter', channel: '' });
    expect(filtersFromSearch(new URLSearchParams('status=Nonsense&channel=Fax&audience=Receiver&deliveryStatus=Read'))).toEqual({
      ...emptyMessageFilters(),
      audience: 'Receiver',
      deliveryStatus: 'Read',
    });
  });

  it('has a dead-letter link that opens the Messages tab filtered to the messages given up on', () => {
    const [path, query] = DEAD_LETTER_LINK.split('?');
    expect(path).toBe('/admin/notifications');
    const params = new URLSearchParams(query);
    expect(params.get('tab')).toBe('messages');
    expect(filtersFromSearch(params).status).toBe('DeadLetter');
  });

  it('refuses a date range that runs backwards', () => {
    expect(validateDateRange('2026-11-10', '2026-11-01')).toMatch(/start date/);
    expect(validateDateRange('2026-11-01', '2026-11-01')).toBeNull();
    expect(validateDateRange('', '2026-11-01')).toBeNull();
    expect(validateDateRange('', '')).toBeNull();
  });
});

describe('the overview period', () => {
  it('uses the Nepal calendar day, not the UTC one', () => {
    // 20:00 UTC on 18 Nov is 01:45 on 19 Nov in Nepal (UTC+05:45).
    expect(nepalToday(new Date('2026-11-18T20:00:00Z'))).toBe('2026-11-19');
    expect(nepalToday(new Date('2026-11-18T10:00:00Z'))).toBe('2026-11-18');
  });

  it('adds days across months and years', () => {
    expect(addDays('2026-11-19', -6)).toBe('2026-11-13');
    expect(addDays('2026-01-02', -3)).toBe('2025-12-30');
    expect(addDays('2026-02-27', 3)).toBe('2026-03-02');
  });

  it('the last 7 days include today and the 6 days before it', () => {
    expect(lastDaysRange(7, new Date('2026-11-19T06:00:00Z'))).toEqual({ from: '2026-11-13', to: '2026-11-19' });
    expect(lastDaysRange(1, new Date('2026-11-19T06:00:00Z'))).toEqual({ from: '2026-11-19', to: '2026-11-19' });
    expect(lastDaysRange(0, new Date('2026-11-19T06:00:00Z'))).toEqual({ from: '2026-11-19', to: '2026-11-19' });
  });

  it('builds the summary address', () => {
    expect(summaryPath('2026-11-13', '2026-11-19')).toBe('/api/v1/notifications/summary?from=2026-11-13&to=2026-11-19');
  });
});

describe('tabs', () => {
  it('shows the test inbox only to a super admin while a mock provider is active', () => {
    expect(visibleTabs({ canUseMockInbox: true, mockInboxAvailable: true })).toEqual(['overview', 'messages', 'templates', 'opt-outs', 'test-inbox']);
    expect(visibleTabs({ canUseMockInbox: true, mockInboxAvailable: false })).not.toContain('test-inbox');
    expect(visibleTabs({ canUseMockInbox: false, mockInboxAvailable: true })).not.toContain('test-inbox');
  });

  it('falls back to the first visible tab for an unknown or hidden tab', () => {
    const visible = visibleTabs({ canUseMockInbox: false, mockInboxAvailable: false });
    expect(parseTab('templates', visible)).toBe('templates');
    expect(parseTab('test-inbox', visible)).toBe('overview');
    expect(parseTab('nonsense', visible)).toBe('overview');
    expect(parseTab(null, visible)).toBe('overview');
  });

  it('knows the test inbox exists unless the server answered 404', () => {
    expect(mockInboxAvailable({ status: 'loaded' })).toBe(true);
    expect(mockInboxAvailable({ status: 'error', httpStatus: 404 })).toBe(false);
    expect(mockInboxAvailable({ status: 'error', httpStatus: 500 })).toBe(true);
    expect(mockInboxAvailable({ status: 'error' })).toBe(false);
    expect(mockInboxAvailable({ status: 'loading' })).toBe(false);
    expect(mockInboxAvailable({ status: 'idle' })).toBe(false);
  });

  it('builds the test inbox address', () => {
    expect(mockMessagesPath(2, 10)).toBe('/api/v1/notifications/mock/messages?page=2&pageSize=10');
  });
});

describe('templates', () => {
  const template = (key: string, channel: 'Sms' | 'WhatsApp'): NotificationTemplateResponse => ({
    id: `${key}-${channel}`,
    key,
    channel,
    language: 'en',
    body: 'Hi',
    providerTemplateName: null,
    parameters: [],
    isActive: true,
    length: 2,
    updatedAtUtc: '2026-11-19T04:00:00Z',
  });

  it('groups SMS and WhatsApp versions under one key, sorted by key', () => {
    const groups = groupTemplates([template('delivered', 'WhatsApp'), template('booking-created', 'Sms'), template('delivered', 'Sms'), template('booking-created', 'WhatsApp')]);
    expect(groups.map((g) => g.key)).toEqual(['booking-created', 'delivered']);
    expect(groups[0]?.sms?.id).toBe('booking-created-Sms');
    expect(groups[0]?.whatsApp?.id).toBe('booking-created-WhatsApp');
    expect(groups[1]?.sms?.id).toBe('delivered-Sms');
  });

  it('leaves a missing channel empty', () => {
    const [group] = groupTemplates([template('returned', 'Sms')]);
    expect(group?.whatsApp).toBeUndefined();
    expect(groupTemplates([])).toEqual([]);
  });

  it('lists placeholders once each, in the order they first appear', () => {
    expect(usedPlaceholders('Parcel {tracking} for {receiverName}. Track {tracking} at {link}')).toEqual(['tracking', 'receiverName', 'link']);
    expect(usedPlaceholders('No fields here { not one } {1bad}')).toEqual([]);
    expect(usedPlaceholders('')).toEqual([]);
  });

  it('finds the placeholders a template may use, falling back when the key is not listed', () => {
    const allowed = { 'booking-created': ['tracking', 'link'], delivered: ['tracking', 'receiverName'] };
    expect(placeholdersFor(allowed, 'delivered', 'Sms')).toEqual(['tracking', 'receiverName']);
    expect(placeholdersFor(allowed, 'returned', 'Sms')).toEqual(['tracking', 'link', 'receiverName']);
    expect(placeholdersFor({ Sms: ['a'], WhatsApp: ['a', 'b'] }, 'x', 'WhatsApp')).toEqual(['a', 'b']);
    expect(placeholdersFor({}, 'x', 'Sms')).toEqual([]);
  });

  it('makes a placeholder token', () => {
    expect(placeholderToken('tracking')).toBe('{tracking}');
  });

  it('inserts at the cursor and replaces a selection', () => {
    expect(insertAtSelection('Hello  there', 6, 6, '{name}')).toEqual({ text: 'Hello {name} there', cursor: 12 });
    expect(insertAtSelection('Hello world', 6, 11, '{name}')).toEqual({ text: 'Hello {name}', cursor: 12 });
    expect(insertAtSelection('', 0, 0, '{a}')).toEqual({ text: '{a}', cursor: 3 });
  });

  it('keeps an out-of-range or backwards selection inside the text', () => {
    expect(insertAtSelection('abc', 99, 120, '!')).toEqual({ text: 'abc!', cursor: 4 });
    expect(insertAtSelection('abc', -5, -1, '!')).toEqual({ text: '!abc', cursor: 1 });
    expect(insertAtSelection('abcdef', 4, 2, '!')).toEqual({ text: 'abcd!ef', cursor: 5 });
  });

  it('blocks saving an empty template, a template with problems, and one that is still being checked', () => {
    expect(canSaveTemplate({ body: 'Hi {tracking}', checking: false, problems: [] })).toBe(true);
    expect(canSaveTemplate({ body: '   ', checking: false, problems: [] })).toBe(false);
    expect(canSaveTemplate({ body: 'Hi {oops}', checking: false, problems: ['Unknown placeholder {oops}'] })).toBe(false);
    expect(canSaveTemplate({ body: 'Hi', checking: true, problems: [] })).toBe(false);
  });
});

describe('SMS length', () => {
  it('counts plain text as GSM: 160 in one SMS', () => {
    expect(smsSegments('')).toMatchObject({ segments: 0, units: 0 });
    expect(smsSegments('a'.repeat(160))).toMatchObject({ encoding: 'GSM', units: 160, segments: 1 });
    expect(smsSegments('a'.repeat(161))).toMatchObject({ encoding: 'GSM', units: 161, segments: 2 });
    expect(smsSegments('a'.repeat(306))).toMatchObject({ segments: 2 });
    expect(smsSegments('a'.repeat(307))).toMatchObject({ segments: 3 });
  });

  it('counts the extension characters twice', () => {
    expect(smsSegments('{}[]~^|\\€')).toMatchObject({ encoding: 'GSM', units: 18 });
    expect(smsSegments(`${'a'.repeat(159)}€`)).toMatchObject({ units: 161, segments: 2 });
  });

  it('switches the whole message to Unicode for Nepali text or emoji: 70 in one SMS', () => {
    expect(smsSegments('नमस्ते')).toMatchObject({ encoding: 'Unicode', segments: 1 });
    expect(smsSegments('a'.repeat(69) + 'न')).toMatchObject({ encoding: 'Unicode', units: 70, segments: 1 });
    expect(smsSegments('a'.repeat(70) + 'न')).toMatchObject({ encoding: 'Unicode', units: 71, segments: 2 });
    expect(smsSegments('a'.repeat(134) + 'न')).toMatchObject({ units: 135, segments: 3 });
    expect(smsSegments('Parcel on its way 🚚')).toMatchObject({ encoding: 'Unicode' });
  });

  it('describes the length in words', () => {
    expect(smsHint('')).toBe('Nothing to send yet.');
    expect(smsHint('Hello')).toBe('5 characters = 1 SMS; plain characters (160 per SMS).');
    expect(smsHint('a'.repeat(200))).toMatch(/200 characters = 2 SMS parts/);
    expect(smsHint('नमस्ते')).toMatch(/Unicode \(70 per SMS\)/);
  });
});

describe('opt-outs', () => {
  it('builds the list address, keeping an explicit "include removed"', () => {
    expect(optOutsPath(emptyOptOutFilters(), 1)).toBe('/api/v1/notifications/opt-outs?page=1&pageSize=20&activeOnly=true');
    expect(optOutsPath({ channel: 'WhatsApp', activeOnly: false }, 2)).toBe('/api/v1/notifications/opt-outs?page=2&pageSize=20&channel=WhatsApp&activeOnly=false');
  });

  it('cleans a phone number and checks it', () => {
    expect(normalisePhone(' +977 981-234 5678 ')).toBe('+9779812345678');
    expect(validateOptOutPhone('+977 981-234-5678')).toBeNull();
    expect(validateOptOutPhone('9812345678')).toBeNull();
    expect(validateOptOutPhone('')).toMatch(/Enter the phone/);
    expect(validateOptOutPhone('98123')).toMatch(/7 to 15 digits/);
    expect(validateOptOutPhone('98123abc456')).toMatch(/7 to 15 digits/);
    expect(validateOptOutPhone('+1234567890123456')).toMatch(/7 to 15 digits/);
  });

  it('builds the request only from a valid phone and a known channel', () => {
    expect(buildOptOutRequest(' 981 234 5678', 'Sms')).toEqual({ phoneNumber: '9812345678', channel: 'Sms' });
    expect(buildOptOutRequest('abc', 'Sms')).toBeNull();
    expect(buildOptOutRequest('9812345678', 'Fax')).toBeNull();
    expect(buildOptOutRequest('9812345678', '')).toBeNull();
  });
});

describe('consent choices made while booking', () => {
  it('sends only the ticked channels, so an unticked box never switches off an earlier yes', () => {
    expect(bookingConsentRequest({ sms: true, whatsApp: false })).toEqual({ sms: true });
    expect(bookingConsentRequest({ sms: false, whatsApp: true })).toEqual({ whatsApp: true });
    expect(bookingConsentRequest({ sms: true, whatsApp: true })).toEqual({ sms: true, whatsApp: true });
  });

  it('sends nothing when nothing is ticked', () => {
    expect(bookingConsentRequest({ sms: false, whatsApp: false })).toBeNull();
  });

  it('travels with the booking itself, so the first message follows the choice', () => {
    const booking = { description: 'Books', quantity: 2 };

    expect(withBookingConsent(booking, { sms: true, whatsApp: false })).toEqual({ ...booking, notifyBySms: true });
    expect(withBookingConsent(booking, { sms: false, whatsApp: true })).toEqual({ ...booking, notifyByWhatsApp: true });
    expect(withBookingConsent(booking, { sms: true, whatsApp: true })).toEqual({ ...booking, notifyBySms: true, notifyByWhatsApp: true });
  });

  it('adds nothing for an unticked box, so it can never switch off an earlier yes', () => {
    const booking = { description: 'Books' };

    const sent = withBookingConsent(booking, { sms: false, whatsApp: false });

    expect(sent).toEqual(booking);
    expect('notifyBySms' in sent).toBe(false);
    expect('notifyByWhatsApp' in sent).toBe(false);
    expect(booking).toEqual({ description: 'Books' });
  });

  it('reads the outcome handed to the parcel page, and nothing else', () => {
    expect(parseConsentOutcome({ justBooked: true, consent: { kind: 'saved', channels: 'SMS' } })).toEqual({ kind: 'saved', channels: 'SMS' });
    expect(parseConsentOutcome({ consent: { kind: 'failed' } })).toEqual({ kind: 'failed' });
    expect(parseConsentOutcome({ justBooked: true })).toBeNull();
    expect(parseConsentOutcome({ consent: { kind: 'saved' } })).toBeNull();
    expect(parseConsentOutcome({ consent: { kind: 'saved', channels: '' } })).toBeNull();
    expect(parseConsentOutcome({ consent: 'failed' })).toBeNull();
    expect(parseConsentOutcome(null)).toBeNull();
    expect(parseConsentOutcome(undefined)).toBeNull();
    expect(parseConsentOutcome('x')).toBeNull();
  });

  it('names the channels', () => {
    expect(chosenChannelsLabel({ sms: true, whatsApp: true })).toBe('SMS and WhatsApp');
    expect(chosenChannelsLabel({ sms: true, whatsApp: false })).toBe('SMS');
    expect(chosenChannelsLabel({ sms: false, whatsApp: true })).toBe('WhatsApp');
    expect(chosenChannelsLabel({ sms: false, whatsApp: false })).toBe('none');
  });
});

describe('error messages', () => {
  it('prefers the server explanation', () => {
    expect(notificationErrorMessage(422, ' Template has unknown placeholders. ')).toBe('Template has unknown placeholders.');
  });

  it('falls back by status', () => {
    expect(notificationErrorMessage(403, undefined)).toBe('You are not allowed to do this.');
    expect(notificationErrorMessage(404, '')).toMatch(/could not be found/);
    expect(notificationErrorMessage(409, undefined)).toMatch(/at the same time/);
    expect(notificationErrorMessage(401, undefined)).toMatch(/sign in/i);
    expect(notificationErrorMessage(500, undefined)).toMatch(/went wrong/);
    expect(notificationErrorMessage(undefined, undefined)).toMatch(/reach the server/);
  });
});

describe('CSV export', () => {
  it('writes one header line and one line per message, with the state in words', () => {
    const csv = messagesCsv([
      message({ status: 'Submitted', providerMessageId: 'wamid.1', attemptCount: 1 }),
      message({ id: 'm2', status: 'Failed', attemptCount: 2, lastError: 'Timeout, again', nextAttemptAtUtc: '2026-11-19T05:00:00Z' }),
    ]);
    const lines = csv.trimEnd().split('\r\n');
    expect(lines).toHaveLength(3);
    expect(lines[0]).toBe(MESSAGE_CSV_HEADER.join(','));
    expect(lines[1]).toContain('Accepted by provider – waiting for delivery report');
    expect(lines[1]).toContain('wamid.1');
    // Only a retrying message has a next attempt; the error with a comma is quoted.
    expect(lines[1]).not.toContain('2026-11-19T05:00:00Z');
    expect(lines[2]).toContain('2026-11-19T05:00:00Z');
    expect(lines[2]).toContain('"Timeout, again"');
    expect(csv.endsWith('\r\n')).toBe(true);
  });

  it('protects against spreadsheet formulas', () => {
    const csv = messagesCsv([message({ providerMessageId: '=HYPERLINK("x")' })]);
    expect(csv).toContain(`"'=HYPERLINK(""x"")"`);
  });

  it('gives an empty export just the header', () => {
    expect(messagesCsv([])).toBe(`${MESSAGE_CSV_HEADER.join(',')}\r\n`);
  });

  it('names the file after the page', () => {
    expect(messagesFileName(3)).toBe('notification-messages-page-3.csv');
  });
});
