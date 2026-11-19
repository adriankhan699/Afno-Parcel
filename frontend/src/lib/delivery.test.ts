import { describe, expect, it } from 'vitest';
import type { AgentAssignmentResponse, AgentPerformanceReport, AgentPerformanceRow, DeliveryAgentResponse } from '../types/api';
import {
  CANCEL_REASON_MAX_LENGTH,
  FAILURE_PRESETS,
  JPEG_QUALITY,
  MAX_IMAGE_EDGE,
  MAX_PICKED_IMAGE_BYTES,
  MAX_PROOF_BYTES,
  PERFORMANCE_CSV_HEADER,
  REASON_MAX_LENGTH,
  actionErrorMessage,
  agentQuery,
  agentStage,
  agentToForm,
  assignableAgents,
  assignmentQuery,
  buildCollectRequest,
  buildCreateAgentRequest,
  buildDeliveryQuery,
  buildFailRequest,
  buildFailureReason,
  buildFileProofForm,
  buildOtpProofRequest,
  buildUpdateAgentRequest,
  computeSuccessRate,
  csvCell,
  csvLine,
  defaultPerformanceRange,
  deliverySteps,
  emptyAgentFilters,
  emptyAgentForm,
  emptyAssignmentFilters,
  emptyFailureForm,
  fitWithin,
  formatAverageHours,
  formatPercent,
  isOpenStatus,
  isStrongPassword,
  mapsUrl,
  nepalDate,
  nextDeliveryStep,
  performanceCsv,
  performanceFileName,
  performanceQuery,
  rateBarWidth,
  rateTone,
  readyQuery,
  rowSuccessRate,
  shiftDate,
  telHref,
  validateAgentForm,
  validateCancelReason,
  validateCodEntry,
  validateDeliveryCode,
  validateFailureForm,
  validatePerformanceRange,
  validatePickedImage,
  validateProcessedSize,
  validateSignature,
  type AgentForm,
} from './delivery';

describe('buildDeliveryQuery', () => {
  it('leaves out blanks but keeps an explicit false', () => {
    expect(buildDeliveryQuery({ page: 1, search: undefined, branchId: '', isActive: false, isAvailable: true, x: null })).toBe('?page=1&isActive=false&isAvailable=true');
  });

  it('percent-encodes values and returns nothing when empty', () => {
    expect(buildDeliveryQuery({ search: 'a b&c' })).toBe('?search=a+b%26c');
    expect(buildDeliveryQuery({})).toBe('');
  });
});

describe('actionErrorMessage', () => {
  it('shows the server explanation for conflicts and refused rules', () => {
    expect(actionErrorMessage(422, 'Collect the full amount.')).toBe('Collect the full amount.');
    expect(actionErrorMessage(409, 'Already collected.')).toBe('Already collected.');
  });

  it('has plain fallbacks', () => {
    expect(actionErrorMessage(409, undefined)).toMatch(/changed by someone else/);
    expect(actionErrorMessage(422, undefined)).toMatch(/not possible/);
    expect(actionErrorMessage(403, 'ignored')).toBe('You are not allowed to do this.');
    expect(actionErrorMessage(404, undefined)).toMatch(/no longer available/);
    expect(actionErrorMessage(429, undefined)).toMatch(/wait a minute/);
    expect(actionErrorMessage(500, 'stack trace')).not.toContain('stack trace');
    expect(actionErrorMessage(undefined, undefined)).toMatch(/Could not reach the server/);
    expect(actionErrorMessage(401, undefined)).toMatch(/sign in again/);
  });
});

describe('failed delivery form', () => {
  it('offers the five reasons from the brief', () => {
    expect(FAILURE_PRESETS).toEqual(['Receiver not available', 'Wrong address', 'Receiver refused', 'Phone not reachable', 'Other']);
  });

  it('needs a reason chosen', () => {
    expect(validateFailureForm(emptyFailureForm())).toHaveProperty('preset');
  });

  it('accepts a preset on its own', () => {
    const form = { ...emptyFailureForm(), preset: 'Wrong address' as const };
    expect(validateFailureForm(form)).toEqual({});
    expect(buildFailureReason(form)).toBe('Wrong address');
    expect(buildFailRequest(form)).toEqual({ reason: 'Wrong address' });
  });

  it('adds the agent\'s own words to a preset', () => {
    const form = { preset: 'Receiver not available' as const, detail: ' shop closed ', remarks: '  called twice ' };
    expect(buildFailureReason(form)).toBe('Receiver not available: shop closed');
    expect(buildFailRequest(form)).toEqual({ reason: 'Receiver not available: shop closed', remarks: 'called twice' });
  });

  it('makes "Other" need a real description', () => {
    expect(validateFailureForm({ preset: 'Other', detail: '  ', remarks: '' })).toHaveProperty('detail');
    expect(validateFailureForm({ preset: 'Other', detail: 'no', remarks: '' })).toHaveProperty('detail');
    expect(validateFailureForm({ preset: 'Other', detail: 'gate locked', remarks: '' })).toEqual({});
    expect(buildFailureReason({ preset: 'Other', detail: ' gate locked ' })).toBe('gate locked');
  });

  it('limits the reason and the remarks', () => {
    expect(validateFailureForm({ preset: 'Other', detail: 'x'.repeat(REASON_MAX_LENGTH + 1), remarks: '' })).toHaveProperty('detail');
    expect(validateFailureForm({ preset: 'Receiver refused', detail: '', remarks: 'x'.repeat(501) })).toHaveProperty('remarks');
    expect(validateFailureForm({ preset: 'Other', detail: 'x'.repeat(REASON_MAX_LENGTH), remarks: 'x'.repeat(500) })).toEqual({});
  });
});

describe('validateCodEntry (the exact amount only)', () => {
  it('accepts exactly the amount due', () => {
    expect(validateCodEntry('1500', 1500)).toEqual({ ok: true, amount: 1500 });
    expect(validateCodEntry(' 1500.00 ', 1500)).toMatchObject({ ok: true });
    expect(validateCodEntry('1695.5', 1695.5)).toMatchObject({ ok: true, amount: 1695.5 });
  });

  it('refuses a short payment, and says how much is due', () => {
    const result = validateCodEntry('1000', 1500);
    expect(result.ok).toBe(false);
    expect(result.error).toContain('Rs. 1,500.00');
  });

  it('refuses too much money, so change is given back first', () => {
    const result = validateCodEntry('2000', 1500);
    expect(result.ok).toBe(false);
    expect(result.error).toMatch(/more than/);
  });

  it('refuses empty, negative and non-numeric entries', () => {
    expect(validateCodEntry('', 100).ok).toBe(false);
    expect(validateCodEntry('abc', 100).ok).toBe(false);
    expect(validateCodEntry('-100', 100).ok).toBe(false);
    expect(validateCodEntry('1e3', 1000).ok).toBe(false);
  });

  it('does not trip over floating-point sums', () => {
    expect(validateCodEntry('0.3', 0.1 + 0.2).ok).toBe(true);
    expect(validateCodEntry('1695.1', 1695.1).ok).toBe(true);
  });

  it('builds the request from the amount due', () => {
    expect(buildCollectRequest(1500)).toEqual({ collectedAmount: 1500 });
  });
});

describe('delivery code and proofs', () => {
  it('needs exactly six digits', () => {
    expect(validateDeliveryCode('123456')).toBeNull();
    expect(validateDeliveryCode(' 123456 ')).toBeNull();
    expect(validateDeliveryCode('')).not.toBeNull();
    expect(validateDeliveryCode('12345')).not.toBeNull();
    expect(validateDeliveryCode('1234567')).not.toBeNull();
    expect(validateDeliveryCode('12a456')).not.toBeNull();
  });

  it('builds the OTP request, naming the receiver only when given', () => {
    expect(buildOtpProofRequest(' 123456 ', '')).toEqual({ code: '123456' });
    expect(buildOtpProofRequest('123456', ' Sita ')).toEqual({ code: '123456', recipientName: 'Sita' });
  });

  it('needs ink and the signer\'s name for a signature', () => {
    expect(validateSignature(false, '')).toEqual({ signature: expect.any(String), recipientName: expect.any(String) });
    expect(validateSignature(true, 'Sita')).toEqual({});
    expect(validateSignature(true, 'x'.repeat(151))).toHaveProperty('recipientName');
    expect(validateSignature(false, 'Sita')).toHaveProperty('signature');
  });

  it('refuses things that are not photos, and absurdly large ones', () => {
    expect(validatePickedImage({ type: 'image/jpeg', size: 4_000_000 })).toBeNull();
    expect(validatePickedImage({ type: 'IMAGE/PNG', size: 100 })).toBeNull();
    expect(validatePickedImage({ type: 'application/pdf', size: 100 })).not.toBeNull();
    expect(validatePickedImage({ type: '', size: 100 })).not.toBeNull();
    expect(validatePickedImage({ type: 'image/jpeg', size: 0 })).not.toBeNull();
    expect(validatePickedImage({ type: 'image/jpeg', size: MAX_PICKED_IMAGE_BYTES + 1 })).not.toBeNull();
  });

  it('refuses a processed photo over 6 MB', () => {
    expect(validateProcessedSize(MAX_PROOF_BYTES)).toBeNull();
    expect(validateProcessedSize(MAX_PROOF_BYTES + 1)).toMatch(/6 MB/);
    expect(validateProcessedSize(0)).not.toBeNull();
  });

  it('shrinks to at most 1600 pixels keeping the proportions, and never enlarges', () => {
    expect(MAX_IMAGE_EDGE).toBe(1600);
    expect(JPEG_QUALITY).toBe(0.8);
    expect(fitWithin(4000, 3000)).toEqual({ width: 1600, height: 1200 });
    expect(fitWithin(3000, 4000)).toEqual({ width: 1200, height: 1600 });
    expect(fitWithin(800, 600)).toEqual({ width: 800, height: 600 });
    expect(fitWithin(1600, 1600)).toEqual({ width: 1600, height: 1600 });
    expect(fitWithin(10000, 1)).toEqual({ width: 1600, height: 1 });
    expect(fitWithin(0, 100)).toEqual({ width: 0, height: 0 });
  });

  it('builds the multipart body with the type, signer and file', () => {
    const form = buildFileProofForm('Signature', new Blob(['png'], { type: 'image/png' }), 'signature.png', ' Sita ');
    expect(form.get('type')).toBe('Signature');
    expect(form.get('recipientName')).toBe('Sita');
    const file = form.get('file') as File;
    expect(file.name).toBe('signature.png');
    expect(file.type).toBe('image/png');
    expect(buildFileProofForm('Photo', new Blob(['x']), 'photo.jpg', '').has('recipientName')).toBe(false);
  });
});

const assignment = (patch: Partial<AgentAssignmentResponse> = {}): AgentAssignmentResponse => ({
  id: 'a1',
  parcelId: 'p1',
  trackingNumber: 'AFNK7M2Q9XT4B3',
  status: 'InProgress',
  attemptNumber: 1,
  assignedAtUtc: '2026-11-19T04:00:00Z',
  parcelStatus: 'OutForDelivery',
  receiverName: 'Sita Gurung',
  receiverArea: 'Pokhara-8, Kaski',
  serviceType: 'Standard',
  description: 'Books',
  quantity: 2,
  weightKg: 2.5,
  codAmount: 0,
  codCollected: false,
  hasProof: false,
  proofs: [],
  canStart: false,
  canFail: true,
  canComplete: false,
  canReturnToWarehouse: false,
  ...patch,
});

describe('the agent\'s delivery wizard', () => {
  it.each([
    ['Assigned', 'assigned'],
    ['Accepted', 'assigned'],
    ['InProgress', 'deliver'],
    ['Failed', 'failed'],
    ['Completed', 'completed'],
    ['Cancelled', 'closed'],
  ])('%s is the %s stage', (status, stage) => {
    expect(agentStage({ status })).toBe(stage);
  });

  it('knows which assignments are open', () => {
    expect(['Assigned', 'Accepted', 'InProgress'].every(isOpenStatus)).toBe(true);
    expect(['Completed', 'Failed', 'Cancelled'].some(isOpenStatus)).toBe(false);
  });

  it('asks for cash only on cash-on-delivery parcels', () => {
    expect(deliverySteps(assignment()).map((s) => s.key)).toEqual(['proof', 'confirm']);
    expect(deliverySteps(assignment({ codAmount: 1500 })).map((s) => s.key)).toEqual(['cash', 'proof', 'confirm']);
    expect(deliverySteps(assignment({ codAmount: 1500 }))[0]?.label).toContain('Rs. 1,500.00');
  });

  it('walks cash, then proof, then confirmation', () => {
    const cod = { codAmount: 1500 };
    expect(nextDeliveryStep(assignment(cod))).toBe('cash');
    expect(nextDeliveryStep(assignment({ ...cod, codCollected: true }))).toBe('proof');
    expect(nextDeliveryStep(assignment({ ...cod, codCollected: true, hasProof: true }))).toBe('confirm');
    expect(nextDeliveryStep(assignment())).toBe('proof');
  });

  it('has no next step before the delivery starts or after it ends', () => {
    expect(nextDeliveryStep(assignment({ status: 'Assigned' }))).toBeNull();
    expect(nextDeliveryStep(assignment({ status: 'Completed', hasProof: true }))).toBeNull();
    expect(nextDeliveryStep(assignment({ status: 'Failed' }))).toBeNull();
  });

  it('marks the confirm step done only once completed', () => {
    const done = deliverySteps(assignment({ status: 'Completed', hasProof: true, codAmount: 100, codCollected: true }));
    expect(done.every((s) => s.done)).toBe(true);
  });
});

describe('call and map links', () => {
  it('keeps only digits and a leading plus in the phone link', () => {
    expect(telHref('9851234567')).toBe('tel:9851234567');
    expect(telHref('+977 985-123 4567')).toBe('tel:+9779851234567');
    expect(telHref('98512x34567;rm')).toBe('tel:9851234567');
    expect(telHref('')).toBeNull();
    expect(telHref(null)).toBeNull();
    expect(telHref('abc')).toBeNull();
  });

  it('searches maps with the address text, encoded', () => {
    const url = mapsUrl('Lakeside, Pokhara-8 & more', 'Kaski');
    expect(url).toBe('https://www.google.com/maps/search/?api=1&query=Lakeside%2C%20Pokhara-8%20%26%20more%2C%20Kaski');
    expect(mapsUrl(null, 'Kaski')).toContain('query=Kaski');
    expect(mapsUrl('', '')).toBeNull();
    expect(mapsUrl(undefined)).toBeNull();
  });
});

const agent = (patch: Partial<DeliveryAgentResponse> = {}): DeliveryAgentResponse => ({
  id: 'ag1',
  userId: 'u1',
  fullName: 'Ram Rider',
  email: 'ram@example.test',
  phoneNumber: '9841000001',
  branchId: 'b1',
  branchName: 'Kathmandu Main',
  vehicleType: 'Motorbike',
  vehicleNumber: 'BA 1 PA 1234',
  licenseNumber: 'L-1',
  isActive: true,
  isAvailable: true,
  openAssignments: 0,
  createdAtUtc: '2026-11-01T00:00:00Z',
  ...patch,
});

describe('agent list filters', () => {
  it('sends nothing when no filter is set', () => {
    expect(buildDeliveryQuery(agentQuery(emptyAgentFilters(), 1))).toBe('?page=1&pageSize=20');
  });

  it('turns the status and availability selects into true and false', () => {
    const q = agentQuery({ search: ' ram ', branchId: 'b1', status: 'inactive', availability: 'available' }, 2);
    expect(q).toMatchObject({ page: 2, search: 'ram', branchId: 'b1', isActive: false, isAvailable: true });
    expect(buildDeliveryQuery(q)).toContain('isActive=false');
    expect(agentQuery({ ...emptyAgentFilters(), status: 'active', availability: 'unavailable' }, 1)).toMatchObject({ isActive: true, isAvailable: false });
  });

  it('lists only active, available agents as assignable', () => {
    const list = [agent({ id: '1' }), agent({ id: '2', isActive: false }), agent({ id: '3', isAvailable: false })];
    expect(assignableAgents(list).map((a) => a.id)).toEqual(['1']);
  });
});

describe('agent form', () => {
  const valid: AgentForm = {
    fullName: ' Ram Rider ',
    email: ' ram@example.test ',
    phoneNumber: '9841000001',
    password: 'Rider-pass-1',
    branchId: 'b1',
    vehicleType: ' Motorbike ',
    vehicleNumber: '',
    licenseNumber: '',
  };

  it('accepts a complete new agent', () => {
    expect(validateAgentForm(valid, true)).toEqual({});
  });

  it('asks for every login detail when creating', () => {
    const errors = validateAgentForm(emptyAgentForm(), true);
    expect(Object.keys(errors).sort()).toEqual(['branchId', 'email', 'fullName', 'password', 'phoneNumber']);
  });

  it('checks the password policy', () => {
    expect(isStrongPassword('Rider-pass-1')).toBe(true);
    expect(isStrongPassword('short1A')).toBe(false);
    expect(isStrongPassword('alllowercase123')).toBe(false);
    expect(isStrongPassword('ALLUPPERCASE123')).toBe(false);
    expect(isStrongPassword('NoDigitsHereAtAll')).toBe(false);
    expect(validateAgentForm({ ...valid, password: 'weak' }, true)).toHaveProperty('password');
  });

  it('only checks the phone and vehicle fields when editing', () => {
    const edit = { ...emptyAgentForm(), phoneNumber: '9841000002' };
    expect(validateAgentForm(edit, false)).toEqual({});
    expect(validateAgentForm({ ...edit, phoneNumber: '123' }, false)).toHaveProperty('phoneNumber');
    expect(validateAgentForm({ ...edit, vehicleNumber: 'x'.repeat(31) }, false)).toHaveProperty('vehicleNumber');
    expect(validateAgentForm({ ...edit, licenseNumber: 'x'.repeat(41) }, false)).toHaveProperty('licenseNumber');
    expect(validateAgentForm({ ...edit, vehicleType: 'x'.repeat(41) }, false)).toHaveProperty('vehicleType');
  });

  it('trims the create request and turns blanks into null', () => {
    expect(buildCreateAgentRequest(valid)).toEqual({
      fullName: 'Ram Rider',
      email: 'ram@example.test',
      phoneNumber: '9841000001',
      password: 'Rider-pass-1',
      branchId: 'b1',
      vehicleType: 'Motorbike',
      vehicleNumber: null,
      licenseNumber: null,
    });
  });

  it('sends only the editable fields on update, never a password or branch', () => {
    const request = buildUpdateAgentRequest(agentToForm(agent()));
    expect(request).toEqual({ phoneNumber: '9841000001', vehicleType: 'Motorbike', vehicleNumber: 'BA 1 PA 1234', licenseNumber: 'L-1' });
    expect(request).not.toHaveProperty('password');
    expect(request).not.toHaveProperty('branchId');
  });

  it('fills the edit form from the agent, with no password', () => {
    expect(agentToForm(agent({ email: null, vehicleType: null }))).toMatchObject({ email: '', vehicleType: '', password: '' });
  });
});

describe('assignment and ready filters', () => {
  it('sends only the filters that are set, and only well-formed dates', () => {
    expect(buildDeliveryQuery(assignmentQuery(emptyAssignmentFilters(), 1))).toBe('?page=1&pageSize=20');
    const q = assignmentQuery({ status: 'Open', deliveryAgentId: 'ag1', from: '2026-11-01', to: 'junk' }, 3);
    expect(q).toMatchObject({ page: 3, status: 'Open', deliveryAgentId: 'ag1', from: '2026-11-01', to: undefined });
  });

  it('builds the ready-parcel query', () => {
    expect(readyQuery({ search: ' AFN ', branchId: '' }, 2)).toEqual({ page: 2, pageSize: 20, search: 'AFN', branchId: undefined });
  });

  it('needs a real reason to take a parcel back', () => {
    expect(validateCancelReason('')).not.toBeNull();
    expect(validateCancelReason('no')).not.toBeNull();
    expect(validateCancelReason('Agent is ill today')).toBeNull();
    expect(validateCancelReason('x'.repeat(CANCEL_REASON_MAX_LENGTH + 1))).not.toBeNull();
  });
});

describe('performance dates', () => {
  it('works out the Nepal calendar day, not the UTC day', () => {
    // 20:00 UTC on 18 Nov is 01:45 on 19 Nov in Nepal (UTC+05:45).
    expect(nepalDate(new Date('2026-11-18T20:00:00Z'))).toBe('2026-11-19');
    expect(nepalDate(new Date('2026-11-18T06:00:00Z'))).toBe('2026-11-18');
  });

  it('moves dates across month and year ends', () => {
    expect(shiftDate('2026-03-01', -1)).toBe('2026-02-28');
    expect(shiftDate('2026-01-01', -1)).toBe('2025-12-31');
    expect(shiftDate('2028-02-28', 1)).toBe('2028-02-29');
    expect(shiftDate('2026-11-19', 0)).toBe('2026-11-19');
  });

  it('defaults to the last 30 Nepal days including today', () => {
    const range = defaultPerformanceRange(new Date('2026-11-18T20:00:00Z'));
    expect(range).toEqual({ from: '2026-10-21', to: '2026-11-19' });
    expect(shiftDate(range.to, -29)).toBe(range.from);
  });

  it('validates the range', () => {
    expect(validatePerformanceRange('2026-11-01', '2026-11-19')).toBeNull();
    expect(validatePerformanceRange('2026-11-01', '2026-11-01')).toBeNull();
    expect(validatePerformanceRange('2026-11-19', '2026-11-01')).toMatch(/before/);
    expect(validatePerformanceRange('', '2026-11-01')).toMatch(/both dates/);
  });

  it('sends the branch and the dates', () => {
    expect(performanceQuery({ from: '2026-11-01', to: '2026-11-19', branchId: '' })).toEqual({ from: '2026-11-01', to: '2026-11-19', branchId: undefined });
  });
});

const row = (patch: Partial<AgentPerformanceRow> = {}): AgentPerformanceRow => ({
  deliveryAgentId: 'ag1',
  fullName: 'Ram Rider',
  branchName: 'Kathmandu Main',
  isActive: true,
  assigned: 12,
  delivered: 9,
  failed: 1,
  cancelled: 1,
  inProgress: 1,
  successRatePercent: 90,
  firstAttemptSuccessPercent: 88.9,
  averageDeliveryHours: 5.5,
  codCollections: 4,
  codCollected: 12345.5,
  ...patch,
});

describe('performance numbers', () => {
  it('works out a success rate to one decimal place', () => {
    expect(computeSuccessRate(9, 1)).toBe(90);
    expect(computeSuccessRate(2, 1)).toBe(66.7);
    expect(computeSuccessRate(0, 0)).toBeNull();
    expect(computeSuccessRate(0, 3)).toBe(0);
    expect(computeSuccessRate(5, 0)).toBe(100);
  });

  it('prefers the server\'s rate and falls back to the counts', () => {
    expect(rowSuccessRate(row({ successRatePercent: 75 }))).toBe(75);
    expect(rowSuccessRate(row({ successRatePercent: null, delivered: 3, failed: 1 }))).toBe(75);
    expect(rowSuccessRate(row({ successRatePercent: undefined, delivered: 0, failed: 0 }))).toBeNull();
    expect(rowSuccessRate(row({ successRatePercent: 0 }))).toBe(0);
  });

  it('formats missing values as a dash', () => {
    expect(formatPercent(null)).toBe('—');
    expect(formatPercent(undefined)).toBe('—');
    expect(formatPercent(66.7)).toBe('66.7%');
    expect(formatAverageHours(null)).toBe('—');
    expect(formatAverageHours(0.4)).toBe('< 1 h');
    expect(formatAverageHours(5.5)).toBe('6 h');
    expect(formatAverageHours(51)).toBe('2 d 3 h');
  });

  it('colours and sizes the bars', () => {
    expect(rateTone(95)).toContain('emerald');
    expect(rateTone(80)).toContain('amber');
    expect(rateTone(40)).toContain('red');
    expect(rateTone(null)).toContain('slate');
    expect(rateBarWidth(null)).toBe(0);
    expect(rateBarWidth(120)).toBe(100);
    expect(rateBarWidth(-5)).toBe(0);
    expect(rateBarWidth(66.7)).toBe(66.7);
  });
});

describe('CSV', () => {
  it('quotes cells that need it and doubles quotes', () => {
    expect(csvCell('plain')).toBe('plain');
    expect(csvCell('a,b')).toBe('"a,b"');
    expect(csvCell('say "hi"')).toBe('"say ""hi"""');
    expect(csvCell('two\nlines')).toBe('"two\nlines"');
  });

  it('keeps numbers as they are, including negatives, and blanks for nothing', () => {
    expect(csvCell(12345.5)).toBe('12345.5');
    expect(csvCell(-3)).toBe('-3');
    expect(csvCell(0)).toBe('0');
    expect(csvCell(null)).toBe('');
    expect(csvCell(undefined)).toBe('');
    expect(csvCell(Number.NaN)).toBe('');
    expect(csvCell(true)).toBe('true');
  });

  it('defuses spreadsheet formulas in text', () => {
    expect(csvCell('=HYPERLINK("http://evil")')).toBe('"\'=HYPERLINK(""http://evil"")"');
    expect(csvCell('+1+1')).toBe("'+1+1");
    expect(csvCell('-2')).toBe("'-2");
    expect(csvCell('@SUM(A1)')).toBe("'@SUM(A1)");
    expect(csvCell('\tcmd')).toBe("'\tcmd");
    expect(csvCell('Ram = rider')).toBe('Ram = rider');
  });

  it('joins a line', () => {
    expect(csvLine(['a', 1, null, 'b,c'])).toBe('a,1,,"b,c"');
  });

  const report: AgentPerformanceReport = {
    from: '2026-10-21',
    to: '2026-11-19',
    agents: [row(), row({ deliveryAgentId: 'ag2', fullName: '=Evil, Agent', isActive: false, successRatePercent: null, averageDeliveryHours: null, firstAttemptSuccessPercent: null, delivered: 0, failed: 0 })],
    totals: row({ assigned: 24, delivered: 9, failed: 1 }),
  };

  it('writes the header, one line per agent and the totals', () => {
    const lines = performanceCsv(report).split('\r\n');
    expect(lines[0]).toBe('Delivery agent performance,2026-10-21 to 2026-11-19');
    expect(lines).toHaveLength(6); // title, header, two agents, totals, and the empty piece after the final line break
    expect(lines[1]).toBe(PERFORMANCE_CSV_HEADER.join(','));
    expect(lines[2]).toBe('Ram Rider,Kathmandu Main,Yes,12,9,1,1,1,90,88.9,5.5,4,12345.5');
    expect(lines[3]).toBe(`"'=Evil, Agent",Kathmandu Main,No,12,0,0,1,1,,,,4,12345.5`);
    expect(lines[4]).toBe('Total,,,24,9,1,1,1,90,88.9,5.5,4,12345.5');
    expect(lines[5]).toBe('');
  });

  it('names the file after the period', () => {
    expect(performanceFileName(report)).toBe('agent-performance-2026-10-21-to-2026-11-19.csv');
  });
});
