import {
  PREPAID_PAYMENT_METHODS,
  type AddressDto,
  type BookParcelRequest,
  type BookingPartyDto,
  type PaymentMethod,
  type QuoteRequest,
  type ServiceType,
} from '../types/api';

// Limits mirror the server's validators. The server stays the authority: these only give quick feedback.
export const LIMITS = {
  name: 150,
  municipality: 100,
  street: 200,
  instructions: 300,
  description: 200,
  notes: 500,
  reference: 100,
  maxQuantity: 100,
  maxWard: 35,
} as const;

export interface PartyForm {
  fullName: string;
  phoneNumber: string;
  province: string;
  district: string;
  municipality: string;
  wardNumber: string;
  streetOrTole: string;
  addressInstructions: string;
}

export interface BookingForm {
  sender: PartyForm;
  receiver: PartyForm;
  originBranchId: string;
  description: string;
  quantity: string;
  weightKg: string;
  lengthCm: string;
  widthCm: string;
  heightCm: string;
  declaredValue: string;
  serviceType: ServiceType;
  paymentMethod: PaymentMethod;
  codAmount: string;
  paymentReference: string;
  notes: string;
}

/** Field errors keyed by the API's property path, e.g. "receiver.address.district" or "weightKg". */
export type FormErrors = Record<string, string>;

export const emptyParty = (): PartyForm => ({
  fullName: '',
  phoneNumber: '',
  province: '',
  district: '',
  municipality: '',
  wardNumber: '',
  streetOrTole: '',
  addressInstructions: '',
});

export const emptyBookingForm = (): BookingForm => ({
  sender: emptyParty(),
  receiver: emptyParty(),
  originBranchId: '',
  description: '',
  quantity: '1',
  weightKg: '',
  lengthCm: '',
  widthCm: '',
  heightCm: '',
  declaredValue: '',
  serviceType: 'Standard',
  paymentMethod: 'Cash',
  codAmount: '',
  paymentReference: '',
  notes: '',
});

export const isPrepaid = (method: PaymentMethod): boolean => (PREPAID_PAYMENT_METHODS as readonly string[]).includes(method);

/** Mirrors the server's phone rule: mobile 96/97/98 + 8 digits, or a landline starting with 0 (optionally +977 / 977). */
export function isNepalPhone(value: string): boolean {
  let compact = value.trim().replace(/[\s\-()]/g, '');
  if (compact.startsWith('+977')) {
    compact = compact.slice(4);
  } else if (compact.startsWith('977') && compact.length === 13) {
    compact = compact.slice(3);
  }
  return /^9[678]\d{8}$/.test(compact) || /^0\d{7,9}$/.test(compact);
}

/** Parses a number typed into an input: '' → null, junk → NaN, otherwise the value. */
export function parseNumber(text: string): number | null {
  const trimmed = text.trim();
  if (trimmed === '') return null;
  return /^-?\d+(\.\d+)?$/.test(trimmed) ? Number(trimmed) : Number.NaN;
}

const decimalPlaces = (n: number): number => {
  const text = String(n);
  const dot = text.indexOf('.');
  return dot === -1 ? 0 : text.length - dot - 1;
};

function validateParty(party: PartyForm, prefix: 'sender' | 'receiver', label: string, errors: FormErrors): void {
  const name = party.fullName.trim();
  if (!name) errors[`${prefix}.fullName`] = `${label} name is required.`;
  else if (name.length > LIMITS.name) errors[`${prefix}.fullName`] = `Name must be at most ${LIMITS.name} characters.`;

  if (!party.phoneNumber.trim()) errors[`${prefix}.phoneNumber`] = `${label} phone number is required.`;
  else if (!isNepalPhone(party.phoneNumber)) {
    errors[`${prefix}.phoneNumber`] = 'Enter a Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.';
  }

  if (!party.province) errors[`${prefix}.address.province`] = 'Choose a province.';
  if (!party.district) errors[`${prefix}.address.district`] = 'Choose a district.';

  const municipality = party.municipality.trim();
  if (!municipality) errors[`${prefix}.address.municipality`] = 'Municipality is required.';
  else if (municipality.length > LIMITS.municipality) {
    errors[`${prefix}.address.municipality`] = `Must be at most ${LIMITS.municipality} characters.`;
  }

  const ward = parseNumber(party.wardNumber);
  if (ward === null) errors[`${prefix}.address.wardNumber`] = 'Ward number is required.';
  else if (!Number.isInteger(ward) || ward < 1 || ward > LIMITS.maxWard) {
    errors[`${prefix}.address.wardNumber`] = `Ward must be a whole number from 1 to ${LIMITS.maxWard}.`;
  }

  if (party.streetOrTole.trim().length > LIMITS.street) {
    errors[`${prefix}.address.streetOrTole`] = `Must be at most ${LIMITS.street} characters.`;
  }
  if (party.addressInstructions.trim().length > LIMITS.instructions) {
    errors[`${prefix}.addressInstructions`] = `Must be at most ${LIMITS.instructions} characters.`;
  }
}

/** Optional non-negative money amount. */
function validateMoney(text: string, key: string, label: string, errors: FormErrors): void {
  const value = parseNumber(text);
  if (value === null) return;
  if (Number.isNaN(value) || value < 0) errors[key] = `${label} must be a number of 0 or more.`;
  else if (decimalPlaces(value) > 2) errors[key] = `${label} can have at most 2 decimal places.`;
}

/** Returns field errors keyed by property path; an empty object means the form can be submitted. */
export function validateBookingForm(form: BookingForm): FormErrors {
  const errors: FormErrors = {};

  validateParty(form.sender, 'sender', 'Sender', errors);
  validateParty(form.receiver, 'receiver', 'Receiver', errors);

  if (!form.originBranchId) errors.originBranchId = 'Choose the branch where the parcel will be handed over.';

  const description = form.description.trim();
  if (!description) errors.description = 'Describe what is inside the parcel.';
  else if (description.length > LIMITS.description) errors.description = `Must be at most ${LIMITS.description} characters.`;

  const quantity = parseNumber(form.quantity);
  if (quantity === null) errors.quantity = 'Quantity is required.';
  else if (!Number.isInteger(quantity) || quantity < 1 || quantity > LIMITS.maxQuantity) {
    errors.quantity = `Quantity must be a whole number from 1 to ${LIMITS.maxQuantity}.`;
  }

  const weight = parseNumber(form.weightKg);
  if (weight === null) errors.weightKg = 'Weight is required.';
  else if (Number.isNaN(weight) || weight <= 0) errors.weightKg = 'Weight must be greater than 0.';
  else if (decimalPlaces(weight) > 3) errors.weightKg = 'Weight can have at most 3 decimal places.';

  const dimensions = [
    ['lengthCm', 'Length', form.lengthCm],
    ['widthCm', 'Width', form.widthCm],
    ['heightCm', 'Height', form.heightCm],
  ] as const;
  const given = dimensions.filter(([, , text]) => text.trim() !== '');
  for (const [key, label, text] of given) {
    const value = parseNumber(text);
    if (value === null || Number.isNaN(value) || value <= 0) errors[key] = `${label} must be greater than 0.`;
  }
  if (given.length > 0 && given.length < 3) {
    for (const [key, label, text] of dimensions) {
      if (text.trim() === '') errors[key] = `${label} is required when other dimensions are given (or clear them all).`;
    }
  }

  validateMoney(form.declaredValue, 'declaredValue', 'Declared value', errors);
  validateMoney(form.codAmount, 'codAmount', 'Amount to collect', errors);

  if (form.paymentReference.trim().length > LIMITS.reference) {
    errors.paymentReference = `Must be at most ${LIMITS.reference} characters.`;
  }
  if (form.notes.trim().length > LIMITS.notes) errors.notes = `Must be at most ${LIMITS.notes} characters.`;

  return errors;
}

const orNull = (text: string): string | null => {
  const trimmed = text.trim();
  return trimmed === '' ? null : trimmed;
};

const numberOrNull = (text: string): number | null => {
  const value = parseNumber(text);
  return value === null || Number.isNaN(value) ? null : value;
};

function toAddress(party: PartyForm): AddressDto {
  return {
    province: party.province,
    district: party.district,
    municipality: party.municipality.trim(),
    wardNumber: Number(party.wardNumber),
    streetOrTole: orNull(party.streetOrTole),
    landmark: null,
    postalCode: null,
  };
}

function toParty(party: PartyForm): BookingPartyDto {
  return {
    fullName: party.fullName.trim(),
    phoneNumber: party.phoneNumber.trim(),
    address: toAddress(party),
    addressInstructions: orNull(party.addressInstructions),
  };
}

/**
 * Builds the booking request. It has no price fields by construction: the delivery charge is calculated by the
 * server from its pricing rules, and the quote shown on screen is never sent back.
 * Call only after validateBookingForm returned no errors.
 */
export function buildBookParcelRequest(form: BookingForm, options: { allowPaymentReference: boolean }): BookParcelRequest {
  const cod = numberOrNull(form.codAmount);
  const reference = orNull(form.paymentReference);
  return {
    sender: toParty(form.sender),
    receiver: toParty(form.receiver),
    originBranchId: form.originBranchId,
    description: form.description.trim(),
    quantity: Number(form.quantity),
    weightKg: Number(form.weightKg),
    serviceType: form.serviceType,
    paymentMethod: form.paymentMethod,
    lengthCm: numberOrNull(form.lengthCm),
    widthCm: numberOrNull(form.widthCm),
    heightCm: numberOrNull(form.heightCm),
    declaredValue: numberOrNull(form.declaredValue),
    codAmount: cod !== null && cod > 0 ? cod : null,
    paymentReference: options.allowPaymentReference && isPrepaid(form.paymentMethod) ? reference : null,
    notes: orNull(form.notes),
  };
}

/**
 * The request for the live price estimate, or null while the form is not complete enough to ask.
 * The destination needs municipality and ward because the server validates a full address.
 */
export function buildQuoteRequest(form: BookingForm): QuoteRequest | null {
  const r = form.receiver;
  const weight = parseNumber(form.weightKg);
  const ward = parseNumber(r.wardNumber);
  if (!form.originBranchId || !r.province || !r.district || !r.municipality.trim()) return null;
  if (ward === null || !Number.isInteger(ward) || ward < 1 || ward > LIMITS.maxWard) return null;
  if (weight === null || Number.isNaN(weight) || weight <= 0 || decimalPlaces(weight) > 3) return null;

  const dims = [parseNumber(form.lengthCm), parseNumber(form.widthCm), parseNumber(form.heightCm)];
  const dimsGiven = dims.filter((d): d is number => d !== null);
  if (dimsGiven.length !== 0 && dimsGiven.length !== 3) return null;
  if (dimsGiven.some((d) => Number.isNaN(d) || d <= 0)) return null;

  const cod = parseNumber(form.codAmount);
  if (cod !== null && (Number.isNaN(cod) || cod < 0 || decimalPlaces(cod) > 2)) return null;

  return {
    originBranchId: form.originBranchId,
    destination: toAddress(r),
    serviceType: form.serviceType,
    weightKg: weight,
    lengthCm: dims[0] ?? null,
    widthCm: dims[1] ?? null,
    heightCm: dims[2] ?? null,
    codAmount: cod !== null && cod > 0 ? cod : null,
  };
}
