import { describe, expect, it } from 'vitest';
import type { AgentCashPositionRow, PaymentReportResponse, PaymentStatusResponse, PaymentTransactionRow } from '../types/api';
import { ApiError } from './apiClient';
import {
  AUTO_CHECK_DELAY_MS,
  MAX_AUTO_CHECKS,
  agentCashCsv,
  amountsEqual,
  barPercent,
  buildCreateSettlementRequest,
  buildInitiateRequest,
  buildOfficeCashRequest,
  buildResolveRequest,
  buildVerifyRequest,
  canHandIn,
  canRecordCash,
  canResolveSettlement,
  canShowReceipt,
  canVerifySettlement,
  defaultReportFilters,
  emptySettlementFilters,
  emptyTransactionFilters,
  emptyVerifyForm,
  handInMessage,
  initialReturnState,
  isOnlineMethod,
  maxOf,
  nextReturnState,
  payableProvider,
  paymentErrorMessage,
  paymentStatusLabel,
  paymentStatusTone,
  providerLabel,
  reportCsv,
  reportFileName,
  reportQuery,
  resolveRedirect,
  settlementQuery,
  settlementStatusLabel,
  successShare,
  transactionQuery,
  transactionStatusLabel,
  transactionStatusTone,
  transactionsCsv,
  usesManualConfirmation,
  validateCashReceived,
  validateCount,
  validateReportRange,
  validateResolveRemarks,
  validateTransactionRange,
  validateVerifyForm,
  varianceText,
  varianceTone,
  type ReturnState,
} from './payment';
import { toPaymentError } from './usePaymentAction';

const ORIGIN = 'https://afno.example';

const answer = (patch: Partial<PaymentStatusResponse> = {}): PaymentStatusResponse => ({
  reference: 'AFPREF123',
  provider: 'Esewa',
  status: 'Pending',
  amount: 450,
  currency: 'NPR',
  trackingNumber: 'AFNABCDEFGHJKM',
  paymentStatus: 'Pending',
  ...patch,
});

const expected = { reference: 'AFPREF123', provider: 'Esewa' };

describe('labels and tones', () => {
  it('names the providers, and falls back to the raw value for an unknown one', () => {
    expect(providerLabel('Esewa')).toBe('eSewa');
    expect(providerLabel('Khalti')).toBe('Khalti');
    expect(providerLabel('Cash')).toBe('Cash at the office');
    expect(providerLabel('CashOnDelivery')).toBe('Cash on delivery');
    expect(providerLabel('Something')).toBe('Something');
  });

  it('only Succeeded is shown as paid, and only success is green', () => {
    expect(transactionStatusLabel('Succeeded')).toBe('Paid');
    expect(transactionStatusLabel('Pending')).toBe('Waiting');
    expect(transactionStatusLabel('Failed')).toBe('Failed');
    expect(transactionStatusTone('Succeeded')).toContain('emerald');
    expect(transactionStatusTone('Failed')).toContain('red');
    expect(transactionStatusTone('Pending')).toContain('amber');
    expect(transactionStatusTone('Whatever')).toContain('amber');
  });

  it('labels the payment and settlement statuses', () => {
    expect(paymentStatusLabel('Pending')).toBe('Not paid yet');
    expect(paymentStatusLabel('Paid')).toBe('Paid');
    expect(paymentStatusLabel('PartiallyRefunded')).toBe('Partly refunded');
    expect(paymentStatusTone('Paid')).toContain('emerald');
    expect(settlementStatusLabel('Submitted')).toBe('Waiting for count');
    expect(settlementStatusLabel('Disputed')).toBe('Count did not match');
  });
});

describe('what can be done with a payment', () => {
  it('only the method the parcel was booked with is offered, and only while unpaid', () => {
    expect(payableProvider({ status: 'Pending', method: 'Esewa' })).toBe('Esewa');
    expect(payableProvider({ status: 'Pending', method: 'Khalti' })).toBe('Khalti');
    expect(payableProvider({ status: 'Paid', method: 'Esewa' })).toBeNull();
    expect(payableProvider({ status: 'Failed', method: 'Khalti' })).toBeNull();
    expect(payableProvider({ status: 'Pending', method: 'Cash' })).toBeNull();
    expect(payableProvider({ status: 'Pending', method: 'CashOnDelivery' })).toBeNull();
  });

  it('recognises the online methods', () => {
    expect(isOnlineMethod('Esewa')).toBe(true);
    expect(isOnlineMethod('Khalti')).toBe(true);
    expect(isOnlineMethod('ConnectIps')).toBe(false);
    expect(isOnlineMethod('Cash')).toBe(false);
  });

  it('counter staff can record cash only for an unpaid cash payment of a live parcel', () => {
    const cash = { status: 'Pending', method: 'Cash' };
    expect(canRecordCash(cash, true, 'BookingCreated')).toBe(true);
    expect(canRecordCash(cash, false, 'BookingCreated')).toBe(false); // a customer cannot
    expect(canRecordCash(cash, true, 'Cancelled')).toBe(false);
    expect(canRecordCash({ status: 'Paid', method: 'Cash' }, true, 'BookingCreated')).toBe(false);
    expect(canRecordCash({ status: 'Pending', method: 'Esewa' }, true, 'BookingCreated')).toBe(false);
    expect(canRecordCash({ status: 'Pending', method: 'CashOnDelivery' }, true, 'BookingCreated')).toBe(false);
  });

  it('the plain "mark as paid" form is kept only for methods with no screen of their own', () => {
    expect(usesManualConfirmation('BankTransfer')).toBe(true);
    expect(usesManualConfirmation('ConnectIps')).toBe(true);
    expect(usesManualConfirmation('Cash')).toBe(false);
    expect(usesManualConfirmation('Esewa')).toBe(false);
    expect(usesManualConfirmation('Khalti')).toBe(false);
    expect(usesManualConfirmation('CashOnDelivery')).toBe(false);
  });

  it('a receipt exists only for a paid payment', () => {
    expect(canShowReceipt({ status: 'Paid' })).toBe(true);
    expect(canShowReceipt({ status: 'Pending' })).toBe(false);
    expect(canShowReceipt({ status: 'Failed' })).toBe(false);
  });

  it('builds the initiation request from the parcel and the method only (never an amount)', () => {
    expect(buildInitiateRequest('p1', 'Khalti')).toEqual({ parcelId: 'p1', provider: 'Khalti' });
  });
});

describe('resolveRedirect (where the browser may go after starting a payment)', () => {
  it('in test mode, our own simulated page is opened inside the app', () => {
    expect(resolveRedirect('https://afno.example/pay/test/esewa/AFREF1', true, ORIGIN)).toEqual({ kind: 'internal', path: '/pay/test/esewa/AFREF1' });
    expect(resolveRedirect('/pay/test/khalti/AFREF1', true, ORIGIN)).toEqual({ kind: 'internal', path: '/pay/test/khalti/AFREF1' });
  });

  it('in test mode the same simulated page on another host name of the site still works, by its path only', () => {
    expect(resolveRedirect('http://localhost:5173/pay/test/esewa/AFREF1', true, 'http://127.0.0.1:5173')).toEqual({
      kind: 'internal',
      path: '/pay/test/esewa/AFREF1',
    });
  });

  it('in test mode nothing else on another host is ever followed', () => {
    expect(resolveRedirect('https://evil.example/pay/test/esewa/AFREF1/../../steal', true, ORIGIN)).toBeNull();
    expect(resolveRedirect('https://evil.example/login', true, ORIGIN)).toBeNull();
    expect(resolveRedirect('//evil.example/login', true, ORIGIN)).toBeNull();
    expect(resolveRedirect('https://esewa.com.np/epay/main', true, ORIGIN)).toBeNull();
  });

  it('outside test mode an official provider page on another host is allowed, and a page on this site stays inside the app', () => {
    expect(resolveRedirect('https://pay.example.com/start?pidx=abc', false, ORIGIN)).toEqual({ kind: 'external', url: 'https://pay.example.com/start?pidx=abc' });
    expect(resolveRedirect('/parcels/1', false, ORIGIN)).toEqual({ kind: 'internal', path: '/parcels/1' });
  });

  it('refuses anything that is not plain http(s), and addresses with a user name or password', () => {
    for (const bad of ['javascript:alert(1)', 'data:text/html,<script>1</script>', 'file:///etc/passwd', 'vbscript:x', 'ftp://x.example/']) {
      expect(resolveRedirect(bad, false, ORIGIN)).toBeNull();
      expect(resolveRedirect(bad, true, ORIGIN)).toBeNull();
    }
    expect(resolveRedirect('https://user:pass@pay.example.com/', false, ORIGIN)).toBeNull();
    // The classic disguise: it reads like pay.example.com but the real host is evil.example. The user-name part is what gives it away.
    expect(resolveRedirect('https://pay.example.com@evil.example/', false, ORIGIN)).toBeNull();
  });

  it('refuses an address that cannot be read at all', () => {
    expect(resolveRedirect('', false, 'not an origin')).toBeNull();
    expect(resolveRedirect('http://[bad', false, ORIGIN)).toBeNull();
  });
});

describe('the return page state machine (only the server decides)', () => {
  const run = (events: Parameters<typeof nextReturnState>[1][], from: ReturnState = initialReturnState) => events.reduce(nextReturnState, from);

  it('starts by verifying', () => {
    expect(initialReturnState.phase).toBe('verifying');
    expect(initialReturnState.message).toMatch(/verifying/i);
  });

  it('paid only when the server says the transaction Succeeded', () => {
    const state = run([{ type: 'check' }, { type: 'answer', expected, response: answer({ status: 'Succeeded', paymentStatus: 'Paid', completedAtUtc: '2026-11-19T06:00:00Z' }) }]);

    expect(state.phase).toBe('paid');
    expect(state.autoRetry).toBe(false);
    expect(state.result?.amount).toBe(450);
  });

  it('failed when the server says Failed, with the server\'s reason', () => {
    const state = run([{ type: 'answer', expected, response: answer({ status: 'Failed', failureReason: 'Cancelled at the provider' }) }]);

    expect(state.phase).toBe('failed');
    expect(state.message).toBe('Cancelled at the provider');
  });

  it('a failure without a reason still gets a kind sentence', () => {
    const state = run([{ type: 'answer', expected, response: answer({ status: 'Failed', failureReason: null }) }]);

    expect(state.phase).toBe('failed');
    expect(state.message).toMatch(/not completed/i);
  });

  it('pending keeps asking by itself a few times, then waits for the person to press "Check again"', () => {
    let state = initialReturnState;
    for (let i = 1; i < MAX_AUTO_CHECKS; i++) {
      state = nextReturnState(state, { type: 'answer', expected, response: answer({ status: 'Pending' }) });
      expect(state.phase).toBe('pending');
      expect(state.autoRetry).toBe(true);
      expect(state.attempts).toBe(i);
    }

    state = nextReturnState(state, { type: 'answer', expected, response: answer({ status: 'Pending' }) });
    expect(state.phase).toBe('pending');
    expect(state.autoRetry).toBe(false);
    expect(state.attempts).toBe(MAX_AUTO_CHECKS);
  });

  it('a manual check starts again but keeps the count, and a later success is accepted', () => {
    let state = run([{ type: 'answer', expected, response: answer({ status: 'Pending' }) }]);
    state = nextReturnState(state, { type: 'check' });
    expect(state.phase).toBe('verifying');
    expect(state.attempts).toBe(1);
    state = nextReturnState(state, { type: 'answer', expected, response: answer({ status: 'Succeeded' }) });
    expect(state.phase).toBe('paid');
  });

  it('an unknown status is treated as still waiting, never as paid', () => {
    for (const status of ['Completed', 'Paid', 'success', 'SUCCEEDED', '', 'Refunded', 'Initiated']) {
      const state = run([{ type: 'answer', expected, response: answer({ status }) }]);
      expect(state.phase, status).toBe('pending');
    }
  });

  it('an answer about a different reference is refused, even if it says Succeeded', () => {
    const state = run([{ type: 'answer', expected, response: answer({ status: 'Succeeded', reference: 'SOMEONE-ELSES' }) }]);

    expect(state.phase).toBe('invalid');
    expect(state.result).toBeNull();
    expect(state.autoRetry).toBe(false);
  });

  it('an answer about a different provider is refused, and the provider is compared without regard to case', () => {
    const wrong = run([{ type: 'answer', expected, response: answer({ status: 'Succeeded', provider: 'Khalti' }) }]);
    const same = run([{ type: 'answer', expected, response: answer({ status: 'Succeeded', provider: 'ESEWA' }) }]);

    expect(wrong.phase).toBe('invalid');
    expect(same.phase).toBe('paid');
  });

  it('a network problem or a server error is retried a few times, then left to the person', () => {
    let state = nextReturnState(initialReturnState, { type: 'failure' });
    expect(state.phase).toBe('unreachable');
    expect(state.autoRetry).toBe(true);
    state = nextReturnState(state, { type: 'failure', httpStatus: 503 });
    expect(state.autoRetry).toBe(true);
    state = nextReturnState(state, { type: 'failure', httpStatus: 429 });
    state = nextReturnState(state, { type: 'failure', httpStatus: 500 });
    state = nextReturnState(state, { type: 'failure' });
    expect(state.attempts).toBe(5);
    expect(state.autoRetry).toBe(false);
    expect(state.message).toMatch(/not been lost/i);
  });

  it('an unknown payment (404) or a refused request is not retried', () => {
    const missing = nextReturnState(initialReturnState, { type: 'failure', httpStatus: 404 });
    const refused = nextReturnState(initialReturnState, { type: 'failure', httpStatus: 400 });

    expect(missing.phase).toBe('invalid');
    expect(missing.autoRetry).toBe(false);
    expect(missing.message).toMatch(/could not find/i);
    expect(refused.phase).toBe('invalid');
    expect(refused.autoRetry).toBe(false);
  });

  it('once paid or failed, nothing later can change the outcome', () => {
    const paid = run([{ type: 'answer', expected, response: answer({ status: 'Succeeded' }) }]);
    const failed = run([{ type: 'answer', expected, response: answer({ status: 'Failed' }) }]);

    expect(nextReturnState(paid, { type: 'failure', httpStatus: 500 })).toBe(paid);
    expect(nextReturnState(paid, { type: 'answer', expected, response: answer({ status: 'Failed' }) })).toBe(paid);
    expect(nextReturnState(paid, { type: 'check' })).toBe(paid);
    expect(nextReturnState(failed, { type: 'answer', expected, response: answer({ status: 'Succeeded' }) })).toBe(failed);
  });

  it('waits a couple of seconds between automatic checks', () => {
    expect(AUTO_CHECK_DELAY_MS).toBeGreaterThanOrEqual(1000);
    expect(AUTO_CHECK_DELAY_MS).toBeLessThanOrEqual(10_000);
  });
});

describe('cash received at the office', () => {
  it('accepts exactly the amount due', () => {
    const entry = validateCashReceived('450', 450);
    expect(entry.ok).toBe(true);
    expect(entry.amount).toBe(450);
    expect(validateCashReceived('450.00', 450).ok).toBe(true);
    expect(validateCashReceived(' 1234.5 ', 1234.5).ok).toBe(true);
  });

  it('refuses short and over payments, blanks and rubbish, with a reason', () => {
    expect(validateCashReceived('449.99', 450).ok).toBe(false);
    expect(validateCashReceived('450.01', 450).error).toMatch(/change/i);
    expect(validateCashReceived('', 450).error).toMatch(/enter/i);
    expect(validateCashReceived('abc', 450).ok).toBe(false);
    expect(validateCashReceived('-450', 450).ok).toBe(false);
  });

  it('compares amounts to the paisa, free of floating point error', () => {
    expect(amountsEqual(0.1 + 0.2, 0.3)).toBe(true);
    expect(amountsEqual(450, 450.004)).toBe(true);
    expect(amountsEqual(450, 450.01)).toBe(false);
  });

  it('builds the request, trimming the note and sending null for an empty one', () => {
    expect(buildOfficeCashRequest('p1', 450, '  paid by the sender ')).toEqual({ parcelId: 'p1', amountReceived: 450, note: 'paid by the sender' });
    expect(buildOfficeCashRequest('p1', 450, '   ')).toEqual({ parcelId: 'p1', amountReceived: 450, note: null });
  });
});

describe('filters and queries', () => {
  it('turns the transaction filters into a query, leaving blanks out and keeping dates only when valid', () => {
    expect(transactionQuery(emptyTransactionFilters(), 1)).toEqual({
      page: 1,
      pageSize: 20,
      provider: undefined,
      status: undefined,
      search: undefined,
      branchId: undefined,
      from: undefined,
      to: undefined,
    });
    expect(transactionQuery({ provider: 'Esewa', status: 'Failed', search: '  AFN12 ', branchId: 'b1', from: '2026-11-01', to: 'tomorrow' }, 3, 50)).toEqual({
      page: 3,
      pageSize: 50,
      provider: 'Esewa',
      status: 'Failed',
      search: 'AFN12',
      branchId: 'b1',
      from: '2026-11-01',
      to: undefined,
    });
  });

  it('turns the settlement filters into a query', () => {
    expect(settlementQuery({ ...emptySettlementFilters(), status: 'Disputed', from: '2026-11-01' }, 2)).toMatchObject({ page: 2, pageSize: 20, status: 'Disputed', from: '2026-11-01', branchId: undefined });
  });

  it('refuses a date range that runs backwards', () => {
    expect(validateTransactionRange('2026-11-10', '2026-11-01')).toMatch(/must not be after/i);
    expect(validateTransactionRange('2026-11-01', '2026-11-10')).toBeNull();
    expect(validateTransactionRange('', '2026-11-10')).toBeNull();
    expect(validateTransactionRange('', '')).toBeNull();
  });

  it('the report defaults to the last 30 Nepal days including today', () => {
    // 20:00 UTC on 18 Nov is already 19 Nov in Nepal (UTC+05:45).
    const filters = defaultReportFilters(new Date('2026-11-18T20:00:00Z'));
    expect(filters.to).toBe('2026-11-19');
    expect(filters.from).toBe('2026-10-21');
    expect(filters.branchId).toBe('');
    expect(reportQuery({ from: filters.from, to: filters.to, branchId: '' })).toEqual({ from: '2026-10-21', to: '2026-11-19', branchId: undefined });
    expect(validateReportRange('2026-11-19', '2026-11-01')).not.toBeNull();
    expect(validateReportRange(filters.from, filters.to)).toBeNull();
  });
});

describe('counting a settlement', () => {
  it('accepts a count that equals the total, and reports no variance', () => {
    const entry = validateCount('4500', 4500);
    expect(entry).toMatchObject({ ok: true, counted: 4500, matches: true, variance: 0 });
    expect(validateCount('4500.00', 4500).matches).toBe(true);
  });

  it('accepts a different count but reports it as not matching, with the variance', () => {
    expect(validateCount('4300', 4500)).toMatchObject({ ok: true, matches: false, variance: -200 });
    expect(validateCount('4550.5', 4500)).toMatchObject({ ok: true, matches: false, variance: 50.5 });
    expect(validateCount('0', 4500)).toMatchObject({ ok: true, matches: false, variance: -4500 });
  });

  it('refuses blanks, rubbish, negatives and more than two decimals', () => {
    expect(validateCount('', 4500).ok).toBe(false);
    expect(validateCount('  ', 4500).error).toMatch(/enter/i);
    expect(validateCount('forty', 4500).ok).toBe(false);
    expect(validateCount('-1', 4500).ok).toBe(false);
    expect(validateCount('4500.123', 4500).error).toMatch(/two decimal/i);
  });

  it('writes the variance in words', () => {
    expect(varianceText(0)).toBe('Matches');
    expect(varianceText(-200)).toBe('Short by Rs. 200.00');
    expect(varianceText(50.5)).toBe('Over by Rs. 50.50');
    expect(varianceText(null)).toBe('—');
    expect(varianceText(undefined)).toBe('—');
    expect(varianceTone(0)).toBe('text-slate-700');
    expect(varianceTone(-1)).toBe('text-red-700');
  });

  it('a matching count needs no remarks; a different count needs an explanation', () => {
    expect(validateVerifyForm({ counted: '4500', remarks: '' }, 4500)).toEqual({});
    expect(validateVerifyForm({ counted: '4300', remarks: '' }, 4500).remarks).toMatch(/explain/i);
    expect(validateVerifyForm({ counted: '4300', remarks: 'no' }, 4500).remarks).toBeDefined();
    expect(validateVerifyForm({ counted: '4300', remarks: 'Two notes were torn' }, 4500)).toEqual({});
    expect(validateVerifyForm({ counted: '', remarks: '' }, 4500).counted).toBeDefined();
    expect(validateVerifyForm({ counted: '4500', remarks: 'x'.repeat(501) }, 4500).remarks).toMatch(/at most/i);
    expect(emptyVerifyForm()).toEqual({ counted: '', remarks: '' });
  });

  it('builds the request with the number the accountant typed', () => {
    expect(buildVerifyRequest({ counted: ' 4300.50 ', remarks: ' short ' })).toEqual({ countedAmount: 4300.5, remarks: 'short' });
    expect(buildVerifyRequest({ counted: '4500', remarks: '' })).toEqual({ countedAmount: 4500, remarks: null });
  });

  it('a resolution needs a written explanation', () => {
    expect(validateResolveRemarks('')).not.toBeNull();
    expect(validateResolveRemarks('ok')).not.toBeNull();
    expect(validateResolveRemarks('Agent repaid the missing Rs. 200 in cash')).toBeNull();
    expect(validateResolveRemarks('x'.repeat(501))).toMatch(/at most/i);
    expect(buildResolveRequest('  settled  ')).toEqual({ remarks: 'settled' });
  });

  it('only a submitted hand-in can be counted and only a disputed one can be resolved', () => {
    expect(canVerifySettlement('Submitted')).toBe(true);
    expect(canVerifySettlement('Verified')).toBe(false);
    expect(canVerifySettlement('Disputed')).toBe(false);
    expect(canResolveSettlement('Disputed')).toBe(true);
    expect(canResolveSettlement('Submitted')).toBe(false);
    expect(canResolveSettlement('Verified')).toBe(false);
  });
});

describe('the agent handing in cash', () => {
  const position = (inHand: number): Pick<AgentCashPositionRow, 'inHand'> => ({ inHand });

  it('only when there is cash in hand', () => {
    expect(canHandIn(position(100))).toBe(true);
    expect(canHandIn(position(0.01))).toBe(true);
    expect(canHandIn(position(0))).toBe(false);
    expect(canHandIn(position(0.001))).toBe(false);
    expect(canHandIn(null)).toBe(false);
    expect(canHandIn(undefined)).toBe(false);
  });

  it('the confirmation names the amount and says it cannot be undone', () => {
    const text = handInMessage(1234.5);
    expect(text).toContain('Rs. 1,234.50');
    expect(text).toMatch(/cannot change/i);
    expect(buildCreateSettlementRequest('  ')).toEqual({ remarks: null });
    expect(buildCreateSettlementRequest(' left at the counter ')).toEqual({ remarks: 'left at the counter' });
  });
});

describe('report numbers', () => {
  it('sizes a bar against the largest value of its group', () => {
    expect(barPercent(50, 200)).toBe(25);
    expect(barPercent(200, 200)).toBe(100);
    expect(barPercent(1, 3)).toBe(33.3);
    expect(barPercent(0, 200)).toBe(0);
    expect(barPercent(10, 0)).toBe(0);
    expect(barPercent(-5, 100)).toBe(0);
    expect(barPercent(Number.NaN, 100)).toBe(0);
    expect(barPercent(500, 100)).toBe(100);
    expect(maxOf([3, 9, 4])).toBe(9);
    expect(maxOf([])).toBe(0);
  });

  it('works out the share of finished transactions that succeeded', () => {
    expect(successShare({ succeeded: 3, failed: 1 })).toBe(75);
    expect(successShare({ succeeded: 0, failed: 0 })).toBeNull();
    expect(successShare({ succeeded: 1, failed: 2 })).toBe(33.3);
  });
});

describe('CSV exports', () => {
  const report: PaymentReportResponse = {
    from: '2026-10-21',
    to: '2026-11-19',
    branchId: null,
    paidCount: 3,
    paidAmount: 1350,
    pendingCount: 1,
    pendingAmount: 450,
    failedAttempts: 2,
    byMethod: [{ method: 'Esewa', count: 2, amount: 900 }],
    byProvider: [{ provider: 'Esewa', succeeded: 2, failed: 2, pending: 0, amount: 900 }],
    daily: [{ date: '2026-11-18', count: 3, amount: 1350 }],
    cod: { collectionCount: 4, collected: 8000, inHand: 1000, inSettlement: 2000, disputed: 500, settled: 4500 },
    settlements: { submitted: 1, verified: 2, disputed: 1, totalVariance: -200 },
  };

  it('writes the whole report with CRLF line ends and the period in the first line', () => {
    const csv = reportCsv(report);
    const lines = csv.split('\r\n');
    expect(lines[0]).toBe('Payment report,2026-10-21 to 2026-11-19');
    expect(lines).toContain('Delivery charges paid,3,1350');
    expect(lines).toContain('Esewa,2,900');
    expect(lines).toContain('Esewa,2,2,0,900');
    expect(lines).toContain('2026-11-18,3,1350');
    expect(lines).toContain('Collected,4,8000');
    expect(lines).toContain('Total variance (NPR),-200');
    expect(csv.endsWith('\r\n')).toBe(true);
    expect(reportFileName(report)).toBe('payment-report-2026-10-21-to-2026-11-19.csv');
  });

  it('writes one line per transaction and defuses spreadsheet formulas in text', () => {
    const row: PaymentTransactionRow = {
      id: 't1',
      paymentId: 'p1',
      parcelId: 'c1',
      trackingNumber: 'AFNABCDEFGHJKM',
      reference: 'AFPREF1',
      type: 'Charge',
      status: 'Succeeded',
      provider: 'Esewa',
      providerReference: '=HYPERLINK("http://evil")',
      amount: 450,
      currency: 'NPR',
      failureReason: 'a, "quoted" reason',
      createdAtUtc: '2026-11-19T06:00:00Z',
      completedAtUtc: null,
    };
    const lines = transactionsCsv([row]).split('\r\n');

    expect(lines[0]).toContain('Provider reference');
    expect(lines[1]).toContain("'=HYPERLINK");
    expect(lines[1]).toContain('"a, ""quoted"" reason"');
    expect(lines[1]).toContain('eSewa');
    expect(lines[1]).toContain('Paid');
  });

  it('writes the agent cash table', () => {
    const csv = agentCashCsv([{ agentId: 'a', fullName: 'Ram, Rider', branchName: 'Kathmandu', collected: 10, inHand: 4, inSettlement: 3, disputed: 2, settled: 1, collectionCount: 5, lastCollectionAtUtc: null }]);
    expect(csv.split('\r\n')[1]).toBe('"Ram, Rider",Kathmandu,10,4,3,2,1,5,');
  });
});

describe('error wording', () => {
  it('prefers the server explanation for conflicts and refusals', () => {
    expect(paymentErrorMessage(422, 'Collect exactly Rs. 450.00')).toBe('Collect exactly Rs. 450.00');
    expect(paymentErrorMessage(409, 'Already paid')).toBe('Already paid');
    expect(paymentErrorMessage(404, undefined)).toMatch(/could not find that payment/i);
    expect(paymentErrorMessage(403, 'x')).toBe('You are not allowed to do this.');
    expect(paymentErrorMessage(401, undefined)).toMatch(/sign in/i);
    expect(paymentErrorMessage(429, undefined)).toMatch(/too many/i);
    expect(paymentErrorMessage(500, 'secret detail')).not.toContain('secret detail');
    expect(paymentErrorMessage(undefined, undefined)).toMatch(/could not reach/i);
  });

  it('turns an ApiError into that wording, and anything else into the offline message', () => {
    expect(toPaymentError(new ApiError(422, { status: 422, detail: 'Collect exactly Rs. 450.00' }))).toBe('Collect exactly Rs. 450.00');
    expect(toPaymentError(new ApiError(404, { status: 404 }))).toMatch(/could not find that payment/i);
    expect(toPaymentError(new Error('network'))).toMatch(/could not reach/i);
  });
});
