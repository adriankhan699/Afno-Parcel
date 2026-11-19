import { describe, expect, it } from 'vitest';
import {
  buildBookParcelRequest,
  buildQuoteRequest,
  emptyBookingForm,
  isNepalPhone,
  isPrepaid,
  parseNumber,
  validateBookingForm,
  type BookingForm,
  type PartyForm,
} from './booking';

const party = (over: Partial<PartyForm> = {}): PartyForm => ({
  fullName: ' Sita Sharma ',
  phoneNumber: '9841234567',
  province: 'Bagmati',
  district: 'Kathmandu',
  municipality: ' Kathmandu Metropolitan City ',
  wardNumber: '10',
  streetOrTole: ' New Baneshwor ',
  addressInstructions: '',
  ...over,
});

const validForm = (over: Partial<BookingForm> = {}): BookingForm => ({
  ...emptyBookingForm(),
  sender: party(),
  receiver: party({ fullName: 'Ram Thapa', phoneNumber: '9851234567', province: 'Gandaki', district: 'Kaski', municipality: 'Pokhara', wardNumber: '8' }),
  originBranchId: '0b0a6c5e-0000-4000-8000-000000000001',
  description: 'Books',
  quantity: '2',
  weightKg: '2.5',
  ...over,
});

describe('isNepalPhone', () => {
  it.each([
    ['9841234567', true],
    ['+977 984-1234567', true],
    ['9779841234567', true],
    ['01-5523456', true],
    ['9541234567', false],
    ['12345', false],
    ['', false],
  ])('%s → %s', (value, expected) => expect(isNepalPhone(value)).toBe(expected));
});

describe('parseNumber', () => {
  it('returns null for blank, NaN for junk and the value otherwise', () => {
    expect(parseNumber('  ')).toBeNull();
    expect(parseNumber('abc')).toBeNaN();
    expect(parseNumber('1e3')).toBeNaN();
    expect(parseNumber(' 2.5 ')).toBe(2.5);
  });
});

describe('validateBookingForm', () => {
  it('accepts a complete form', () => {
    expect(validateBookingForm(validForm())).toEqual({});
  });

  it('flags every required field of an empty form with API-style keys', () => {
    const errors = validateBookingForm(emptyBookingForm());
    for (const key of [
      'sender.fullName',
      'sender.phoneNumber',
      'sender.address.province',
      'sender.address.district',
      'sender.address.municipality',
      'sender.address.wardNumber',
      'receiver.fullName',
      'receiver.address.district',
      'originBranchId',
      'description',
      'weightKg',
    ]) {
      expect(errors[key], key).toBeTruthy();
    }
  });

  it.each([
    ['0', true],
    ['36', true],
    ['1.5', true],
    ['1', false],
    ['35', false],
  ])('ward %s invalid=%s', (ward, invalid) => {
    const errors = validateBookingForm(validForm({ receiver: party({ wardNumber: ward }) }));
    expect(Boolean(errors['receiver.address.wardNumber'])).toBe(invalid);
  });

  it.each([
    ['0', true],
    ['101', true],
    ['2.5', true],
    ['1', false],
    ['100', false],
  ])('quantity %s invalid=%s', (quantity, invalid) => {
    expect(Boolean(validateBookingForm(validForm({ quantity })).quantity)).toBe(invalid);
  });

  it.each([
    ['0', true],
    ['-1', true],
    ['abc', true],
    ['1.2345', true],
    ['0.001', false],
    ['25', false],
  ])('weight %s invalid=%s', (weightKg, invalid) => {
    expect(Boolean(validateBookingForm(validForm({ weightKg })).weightKg)).toBe(invalid);
  });

  it('requires all three dimensions or none', () => {
    const partial = validateBookingForm(validForm({ lengthCm: '30' }));
    expect(partial.widthCm).toBeTruthy();
    expect(partial.heightCm).toBeTruthy();
    expect(partial.lengthCm).toBeUndefined();

    expect(validateBookingForm(validForm({ lengthCm: '30', widthCm: '20', heightCm: '10' }))).toEqual({});
    expect(validateBookingForm(validForm({ lengthCm: '0', widthCm: '20', heightCm: '10' })).lengthCm).toBeTruthy();
  });

  it('checks optional money fields and text limits', () => {
    expect(validateBookingForm(validForm({ declaredValue: '-5' })).declaredValue).toBeTruthy();
    expect(validateBookingForm(validForm({ codAmount: '10.123' })).codAmount).toBeTruthy();
    expect(validateBookingForm(validForm({ codAmount: '0' })).codAmount).toBeUndefined();
    expect(validateBookingForm(validForm({ description: 'x'.repeat(201) })).description).toBeTruthy();
    expect(validateBookingForm(validForm({ notes: 'x'.repeat(501) })).notes).toBeTruthy();
    expect(validateBookingForm(validForm({ receiver: party({ addressInstructions: 'x'.repeat(301) }) }))['receiver.addressInstructions']).toBeTruthy();
  });

  it('rejects an invalid phone number on either party', () => {
    const errors = validateBookingForm(validForm({ sender: party({ phoneNumber: '123' }) }));
    expect(errors['sender.phoneNumber']).toBeTruthy();
    expect(errors['receiver.phoneNumber']).toBeUndefined();
  });
});

describe('buildBookParcelRequest', () => {
  it('trims text, converts numbers and nulls empty optionals', () => {
    const request = buildBookParcelRequest(validForm(), { allowPaymentReference: false });

    expect(request.sender.fullName).toBe('Sita Sharma');
    expect(request.sender.address).toEqual({
      province: 'Bagmati',
      district: 'Kathmandu',
      municipality: 'Kathmandu Metropolitan City',
      wardNumber: 10,
      streetOrTole: 'New Baneshwor',
      landmark: null,
      postalCode: null,
    });
    expect(request.quantity).toBe(2);
    expect(request.weightKg).toBe(2.5);
    expect(request.lengthCm).toBeNull();
    expect(request.declaredValue).toBeNull();
    expect(request.codAmount).toBeNull();
    expect(request.notes).toBeNull();
    expect(request.sender.addressInstructions).toBeNull();
  });

  it('never contains a price: only the whitelisted booking fields are sent', () => {
    const form = validForm({ codAmount: '1500', declaredValue: '800', lengthCm: '30', widthCm: '20', heightCm: '10' });
    const json = JSON.parse(JSON.stringify(buildBookParcelRequest(form, { allowPaymentReference: true }))) as Record<string, unknown>;

    expect(Object.keys(json).sort()).toEqual(
      [
        'codAmount',
        'declaredValue',
        'description',
        'heightCm',
        'lengthCm',
        'notes',
        'originBranchId',
        'paymentMethod',
        'paymentReference',
        'quantity',
        'receiver',
        'sender',
        'serviceType',
        'weightKg',
        'widthCm',
      ].sort(),
    );
    const text = JSON.stringify(json).toLowerCase();
    for (const forbidden of ['charge', 'price', 'total', 'vat', 'fee']) {
      expect(text, forbidden).not.toContain(forbidden);
    }
  });

  it('sends the COD goods amount only when positive', () => {
    expect(buildBookParcelRequest(validForm({ codAmount: '1500' }), { allowPaymentReference: false }).codAmount).toBe(1500);
    expect(buildBookParcelRequest(validForm({ codAmount: '0' }), { allowPaymentReference: false }).codAmount).toBeNull();
  });

  it('sends a payment reference only to counter staff and only for prepaid methods', () => {
    const form = validForm({ paymentMethod: 'Esewa', paymentReference: ' TX-1 ' });
    expect(buildBookParcelRequest(form, { allowPaymentReference: true }).paymentReference).toBe('TX-1');
    expect(buildBookParcelRequest(form, { allowPaymentReference: false }).paymentReference).toBeNull();
    expect(
      buildBookParcelRequest({ ...form, paymentMethod: 'CashOnDelivery' }, { allowPaymentReference: true }).paymentReference,
    ).toBeNull();
  });
});

describe('buildQuoteRequest', () => {
  it('is null until branch, receiver address and weight are complete', () => {
    expect(buildQuoteRequest(emptyBookingForm())).toBeNull();
    expect(buildQuoteRequest(validForm({ originBranchId: '' }))).toBeNull();
    expect(buildQuoteRequest(validForm({ weightKg: '' }))).toBeNull();
    expect(buildQuoteRequest(validForm({ receiver: party({ district: '' }) }))).toBeNull();
    expect(buildQuoteRequest(validForm({ receiver: party({ wardNumber: '99' }) }))).toBeNull();
  });

  it('is null for partial or invalid dimensions', () => {
    expect(buildQuoteRequest(validForm({ lengthCm: '30' }))).toBeNull();
    expect(buildQuoteRequest(validForm({ lengthCm: '30', widthCm: 'x', heightCm: '10' }))).toBeNull();
  });

  it('builds the destination from the receiver only, without any sender data or price', () => {
    const quote = buildQuoteRequest(validForm({ codAmount: '2000', lengthCm: '30', widthCm: '20', heightCm: '10', serviceType: 'Express' }));

    expect(quote).toMatchObject({
      destination: { province: 'Gandaki', district: 'Kaski', municipality: 'Pokhara', wardNumber: 8 },
      serviceType: 'Express',
      weightKg: 2.5,
      lengthCm: 30,
      widthCm: 20,
      heightCm: 10,
      codAmount: 2000,
    });
    expect(JSON.stringify(quote)).not.toContain('Sita');
  });
});

describe('isPrepaid', () => {
  it('treats everything except cash on delivery as prepaid', () => {
    expect(isPrepaid('Cash')).toBe(true);
    expect(isPrepaid('Khalti')).toBe(true);
    expect(isPrepaid('CashOnDelivery')).toBe(false);
  });
});
