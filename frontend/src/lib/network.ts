import type {
  AddressDto,
  BranchQuery,
  ScanLogQuery,
  StaffQuery,
  WaitingQuery,
  WarehouseQuery,
} from '../types/api';
import { LIMITS, isNepalPhone, parseNumber, type FormErrors } from './booking';

export const ADMIN_PAGE_SIZE = 20;

type QueryValue = string | number | boolean | undefined | null;

/**
 * "?page=1&search=ktm". Blank values are left out and so are flags that are false, so the server applies no filter.
 * Values are percent-encoded.
 */
export function buildQuery(params: object): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params as Record<string, QueryValue>)) {
    if (value === undefined || value === null || value === '' || value === false) continue;
    search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

const trimmed = (value: string): string | undefined => {
  const text = value.trim();
  return text ? text : undefined;
};

const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;
const asDate = (value: string): string | undefined => (DATE_PATTERN.test(value) ? value : undefined);

// ---------------------------------------------------------------------------------------------
// Filters as typed in the forms (all strings, so inputs stay controlled) → API queries

export interface BranchFilters {
  search: string;
  type: string;
  includeInactive: boolean;
}
export const emptyBranchFilters = (): BranchFilters => ({ search: '', type: '', includeInactive: false });

export function branchQuery(filters: BranchFilters, page: number, pageSize = ADMIN_PAGE_SIZE): BranchQuery {
  return { page, pageSize, search: trimmed(filters.search), type: filters.type || undefined, includeInactive: filters.includeInactive };
}

export interface WarehouseFilters {
  search: string;
  branchId: string;
  includeInactive: boolean;
}
export const emptyWarehouseFilters = (): WarehouseFilters => ({ search: '', branchId: '', includeInactive: false });

export function warehouseQuery(filters: WarehouseFilters, page: number, pageSize = ADMIN_PAGE_SIZE): WarehouseQuery {
  return {
    page,
    pageSize,
    search: trimmed(filters.search),
    branchId: filters.branchId || undefined,
    includeInactive: filters.includeInactive,
  };
}

export interface StaffFilters {
  search: string;
  role: string;
  branchId: string;
  warehouseId: string;
  unassigned: boolean;
}
export const emptyStaffFilters = (): StaffFilters => ({ search: '', role: '', branchId: '', warehouseId: '', unassigned: false });

export function staffQuery(filters: StaffFilters, page: number, pageSize = ADMIN_PAGE_SIZE): StaffQuery {
  // "Unassigned" and a branch/warehouse filter contradict each other, so the branch and warehouse are dropped.
  return {
    page,
    pageSize,
    search: trimmed(filters.search),
    role: filters.role || undefined,
    branchId: filters.unassigned ? undefined : filters.branchId || undefined,
    warehouseId: filters.unassigned ? undefined : filters.warehouseId || undefined,
    unassigned: filters.unassigned,
  };
}

export interface WaitingFilters {
  kind: string;
  branchId: string;
  warehouseId: string;
}
export const emptyWaitingFilters = (): WaitingFilters => ({ kind: '', branchId: '', warehouseId: '' });

export function waitingQuery(filters: WaitingFilters, page: number, pageSize = ADMIN_PAGE_SIZE): WaitingQuery {
  return {
    page,
    pageSize,
    kind: filters.kind || undefined,
    branchId: filters.branchId || undefined,
    warehouseId: filters.warehouseId || undefined,
  };
}

export interface ScanLogFilters {
  warehouseId: string;
  scanType: string;
  outcome: string;
  trackingNumber: string;
  from: string;
  to: string;
}
export const emptyScanLogFilters = (): ScanLogFilters => ({ warehouseId: '', scanType: '', outcome: '', trackingNumber: '', from: '', to: '' });

export function scanLogQuery(filters: ScanLogFilters, page: number, pageSize = ADMIN_PAGE_SIZE): ScanLogQuery {
  return {
    page,
    pageSize,
    warehouseId: filters.warehouseId || undefined,
    scanType: filters.scanType || undefined,
    outcome: filters.outcome || undefined,
    trackingNumber: trimmed(filters.trackingNumber),
    from: asDate(filters.from),
    to: asDate(filters.to),
  };
}

/** True when a date range is back to front (the server would return nothing or an error). */
export const isRangeInvalid = (from: string, to: string): boolean => Boolean(from && to && from > to);

export function inventoryQuery(search: string, page: number, pageSize = ADMIN_PAGE_SIZE): { page: number; pageSize: number; search?: string } {
  return { page, pageSize, search: trimmed(search) };
}

// ---------------------------------------------------------------------------------------------
// Waiting time and capacity

export type WaitLevel = 'normal' | 'warning' | 'late';

export const WAIT_WARNING_HOURS = 24;
export const WAIT_LATE_HOURS = 48;

/** Parcels waiting a day or more are flagged; two days or more are late. */
export function waitLevel(hours: number): WaitLevel {
  if (hours >= WAIT_LATE_HOURS) return 'late';
  if (hours >= WAIT_WARNING_HOURS) return 'warning';
  return 'normal';
}

export function waitTone(level: WaitLevel): string {
  switch (level) {
    case 'late':
      return 'bg-red-50 text-red-700';
    case 'warning':
      return 'bg-amber-50 text-amber-800';
    default:
      return 'bg-slate-100 text-slate-700';
  }
}

/** "< 1 h", "5 h", "2 d 3 h". */
export function formatWaiting(hours: number): string {
  if (!Number.isFinite(hours) || hours < 1) return '< 1 h';
  const whole = Math.round(hours);
  if (whole < 48) return `${whole} h`;
  const days = Math.floor(whole / 24);
  const rest = whole - days * 24;
  return rest === 0 ? `${days} d` : `${days} d ${rest} h`;
}

/** Share of a warehouse's capacity in use, 0–100+ (can exceed 100 when over capacity). Null when no capacity is set. */
export function utilisation(held: number, capacity: number | null | undefined): number | null {
  if (!capacity || capacity <= 0) return null;
  return Math.round((held / capacity) * 1000) / 10;
}

/** Width of the utilisation bar, clamped to 0–100. */
export const barWidth = (percent: number | null): number => (percent === null ? 0 : Math.max(0, Math.min(100, percent)));

export function utilisationTone(percent: number | null): string {
  if (percent === null) return 'bg-slate-300';
  if (percent >= 100) return 'bg-red-500';
  if (percent >= 80) return 'bg-amber-500';
  return 'bg-emerald-500';
}

// ---------------------------------------------------------------------------------------------
// Staff assignment

export interface AssignmentForm {
  branchId: string;
  warehouseId: string;
}

export function warehousesForBranch<T extends { branchId: string }>(warehouses: readonly T[], branchId: string): T[] {
  return branchId ? warehouses.filter((w) => w.branchId === branchId) : [];
}

/** A branch is needed; a warehouse is optional, but if given it must belong to that branch. */
export function validateAssignment(form: AssignmentForm, warehouses: readonly { id: string; branchId: string }[]): FormErrors {
  const errors: FormErrors = {};
  if (!form.branchId) {
    errors.branchId = 'Choose a branch.';
    return errors;
  }
  if (form.warehouseId) {
    const warehouse = warehouses.find((w) => w.id === form.warehouseId);
    if (!warehouse || warehouse.branchId !== form.branchId) {
      errors.warehouseId = 'This warehouse does not belong to the chosen branch.';
    }
  }
  return errors;
}

export function buildAssignRequest(form: AssignmentForm): { branchId: string; warehouseId?: string } {
  return form.warehouseId ? { branchId: form.branchId, warehouseId: form.warehouseId } : { branchId: form.branchId };
}

// ---------------------------------------------------------------------------------------------
// Branch and warehouse forms

export interface AddressForm {
  province: string;
  district: string;
  municipality: string;
  wardNumber: string;
  streetOrTole: string;
  postalCode: string;
}

export const emptyAddressForm = (): AddressForm => ({
  province: '',
  district: '',
  municipality: '',
  wardNumber: '',
  streetOrTole: '',
  postalCode: '',
});

export const addressToForm = (a: AddressDto): AddressForm => ({
  province: a.province,
  district: a.district,
  municipality: a.municipality,
  wardNumber: String(a.wardNumber),
  streetOrTole: a.streetOrTole ?? '',
  postalCode: a.postalCode ?? '',
});

export function validateAddressForm(form: AddressForm, errors: FormErrors): void {
  if (!form.province) errors['address.province'] = 'Choose a province.';
  if (!form.district) errors['address.district'] = 'Choose a district.';
  if (!form.municipality.trim()) errors['address.municipality'] = 'Enter the municipality.';
  else if (form.municipality.trim().length > LIMITS.municipality) errors['address.municipality'] = `At most ${LIMITS.municipality} characters.`;

  const ward = parseNumber(form.wardNumber);
  if (ward === null || !Number.isInteger(ward) || ward < 1 || ward > LIMITS.maxWard) {
    errors['address.wardNumber'] = `Ward number must be a whole number from 1 to ${LIMITS.maxWard}.`;
  }
  if (form.streetOrTole.trim().length > LIMITS.street) errors['address.streetOrTole'] = `At most ${LIMITS.street} characters.`;
  if (form.postalCode.trim() && !/^\d{5}$/.test(form.postalCode.trim())) errors['address.postalCode'] = 'A postal code has exactly 5 digits.';
}

export function addressFormToDto(form: AddressForm): AddressDto {
  const dto: AddressDto = {
    province: form.province,
    district: form.district,
    municipality: form.municipality.trim(),
    wardNumber: Number(parseNumber(form.wardNumber)),
  };
  if (form.streetOrTole.trim()) dto.streetOrTole = form.streetOrTole.trim();
  if (form.postalCode.trim()) dto.postalCode = form.postalCode.trim();
  return dto;
}

const CODE_PATTERN = /^[A-Za-z0-9]+$/;
const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export interface BranchForm extends AddressForm {
  code: string;
  name: string;
  type: 'Branch' | 'HeadOffice';
  phoneNumber: string;
  email: string;
}

export const emptyBranchForm = (): BranchForm => ({
  ...emptyAddressForm(),
  code: '',
  name: '',
  type: 'Branch',
  phoneNumber: '',
  email: '',
});

/** `isNew` also checks the code, which cannot be changed afterwards. */
export function validateBranchForm(form: BranchForm, isNew: boolean): FormErrors {
  const errors: FormErrors = {};
  if (isNew) {
    const code = form.code.trim();
    if (!code) errors.code = 'Enter a code, e.g. KTM01.';
    else if (code.length > 10) errors.code = 'The code can be at most 10 characters.';
    else if (!CODE_PATTERN.test(code)) errors.code = 'Use letters and digits only.';
  }
  if (!form.name.trim()) errors.name = 'Enter a name.';
  else if (form.name.trim().length > 150) errors.name = 'The name can be at most 150 characters.';
  if (!isNepalPhone(form.phoneNumber)) errors.phoneNumber = 'Enter a valid Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.';
  if (form.email.trim() && !EMAIL_PATTERN.test(form.email.trim())) errors.email = 'Enter a valid email address.';
  validateAddressForm(form, errors);
  return errors;
}

export function buildCreateBranchRequest(form: BranchForm) {
  return {
    code: form.code.trim().toUpperCase(),
    name: form.name.trim(),
    type: form.type,
    address: addressFormToDto(form),
    phoneNumber: form.phoneNumber.trim(),
    email: form.email.trim() || null,
  };
}

export function buildUpdateBranchRequest(form: BranchForm) {
  return {
    name: form.name.trim(),
    address: addressFormToDto(form),
    phoneNumber: form.phoneNumber.trim(),
    email: form.email.trim() || null,
  };
}

export interface WarehouseForm extends AddressForm {
  branchId: string;
  code: string;
  name: string;
  phoneNumber: string;
  capacity: string;
}

export const emptyWarehouseForm = (): WarehouseForm => ({
  ...emptyAddressForm(),
  branchId: '',
  code: '',
  name: '',
  phoneNumber: '',
  capacity: '',
});

/** `isNew` also checks the branch and the code, which cannot be changed afterwards. */
export function validateWarehouseForm(form: WarehouseForm, isNew: boolean): FormErrors {
  const errors: FormErrors = {};
  if (isNew) {
    if (!form.branchId) errors.branchId = 'Choose the branch that runs this warehouse.';
    const code = form.code.trim();
    if (!code) errors.code = 'Enter a code, e.g. KTMW1.';
    else if (code.length > 10) errors.code = 'The code can be at most 10 characters.';
    else if (!CODE_PATTERN.test(code)) errors.code = 'Use letters and digits only.';
  }
  if (!form.name.trim()) errors.name = 'Enter a name.';
  else if (form.name.trim().length > 150) errors.name = 'The name can be at most 150 characters.';
  if (!isNepalPhone(form.phoneNumber)) errors.phoneNumber = 'Enter a valid Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.';
  if (form.capacity.trim()) {
    const capacity = parseNumber(form.capacity);
    if (capacity === null || !Number.isInteger(capacity) || capacity < 1) errors.capacity = 'Capacity must be a whole number of parcels, at least 1.';
  }
  validateAddressForm(form, errors);
  return errors;
}

function capacityOf(form: WarehouseForm): number | null {
  return form.capacity.trim() ? Number(parseNumber(form.capacity)) : null;
}

export function buildCreateWarehouseRequest(form: WarehouseForm) {
  return {
    branchId: form.branchId,
    code: form.code.trim().toUpperCase(),
    name: form.name.trim(),
    address: addressFormToDto(form),
    phoneNumber: form.phoneNumber.trim(),
    capacityParcels: capacityOf(form),
  };
}

export function buildUpdateWarehouseRequest(form: WarehouseForm) {
  return {
    name: form.name.trim(),
    address: addressFormToDto(form),
    phoneNumber: form.phoneNumber.trim(),
    capacityParcels: capacityOf(form),
  };
}

// ---------------------------------------------------------------------------------------------
// Staff accounts

export interface StaffAccountForm {
  fullName: string;
  email: string;
  phoneNumber: string;
  password: string;
  role: string;
}

export const emptyStaffAccountForm = (): StaffAccountForm => ({ fullName: '', email: '', phoneNumber: '', password: '', role: '' });

export function validateStaffAccountForm(form: StaffAccountForm): FormErrors {
  const errors: FormErrors = {};
  if (!form.fullName.trim()) errors.fullName = 'Enter the full name.';
  if (!EMAIL_PATTERN.test(form.email.trim())) errors.email = 'Enter a valid email address.';
  if (form.phoneNumber.trim() && !isNepalPhone(form.phoneNumber)) errors.phoneNumber = 'Enter a valid Nepali phone number or leave it empty.';
  if (!form.role) errors.role = 'Choose a role.';
  const password = form.password;
  if (password.length < 10 || !/[a-z]/.test(password) || !/[A-Z]/.test(password) || !/\d/.test(password)) {
    errors.password = 'At least 10 characters with an upper-case letter, a lower-case letter and a digit.';
  }
  return errors;
}

export function buildCreateStaffRequest(form: StaffAccountForm) {
  return {
    fullName: form.fullName.trim(),
    email: form.email.trim(),
    phoneNumber: form.phoneNumber.trim() || null,
    password: form.password,
    role: form.role,
  };
}

/** "Branch Manager" for display. */
export function roleLabel(role: string): string {
  return role.replace(/([a-z])([A-Z])/g, '$1 $2');
}
