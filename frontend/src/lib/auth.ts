// Role names mirror NepalParcel.Domain.Identity.Roles. The server enforces them; these helpers only decide what to show.
export const ROLES = {
  SuperAdmin: 'SuperAdmin',
  BranchManager: 'BranchManager',
  BookingStaff: 'BookingStaff',
  WarehouseStaff: 'WarehouseStaff',
  DeliveryAgent: 'DeliveryAgent',
  Accountant: 'Accountant',
  Customer: 'Customer',
} as const;

/** Roles that may book at the counter for walk-in customers, cancel bookings and confirm cash payments. */
const COUNTER_STAFF: readonly string[] = [ROLES.SuperAdmin, ROLES.BranchManager, ROLES.BookingStaff];

/** Roles that may see every parcel (not just their own). */
const PARCEL_VIEWERS: readonly string[] = [
  ROLES.SuperAdmin,
  ROLES.BranchManager,
  ROLES.BookingStaff,
  ROLES.WarehouseStaff,
  ROLES.Accountant,
];

/** Roles that may scan parcels in and out of warehouses. */
const SCANNERS: readonly string[] = [ROLES.SuperAdmin, ROLES.BranchManager, ROLES.WarehouseStaff];

/** Roles that may move a parcel along its journey (the server decides which statuses each role may set). */
const STATUS_UPDATERS: readonly string[] = [
  ROLES.SuperAdmin,
  ROLES.BranchManager,
  ROLES.BookingStaff,
  ROLES.WarehouseStaff,
  ROLES.DeliveryAgent,
];

type Roles = readonly string[] | null | undefined;

const hasAny = (roles: Roles, allowed: readonly string[]) => !!roles && roles.some((r) => allowed.includes(r));

export const isCustomer = (roles: Roles) => hasAny(roles, [ROLES.Customer]);
export const isCounterStaff = (roles: Roles) => hasAny(roles, COUNTER_STAFF);
/** True for anyone who can see all parcels. */
export const isStaff = (roles: Roles) => hasAny(roles, PARCEL_VIEWERS);
/** Customers and counter staff are the only roles that can create a booking. */
export const canBook = (roles: Roles) => isCustomer(roles) || isCounterStaff(roles);
/** Can use the warehouse scan page. */
export const canScan = (roles: Roles) => hasAny(roles, SCANNERS);
/** Can update parcel status (which statuses exactly is up to the server: see ParcelResponse.allowedNextStatuses). */
export const canUpdateStatus = (roles: Roles) => hasAny(roles, STATUS_UPDATERS);

/** Only a path inside this app may be used as a post-login destination (never an absolute or protocol-relative URL). */
export function safeReturnPath(candidate: unknown, fallback = '/'): string {
  if (typeof candidate !== 'string') return fallback;
  if (!candidate.startsWith('/') || candidate.startsWith('//') || candidate.startsWith('/\\')) return fallback;
  return candidate;
}

const REFRESH_TOKEN_KEY = 'afno.refreshToken';

/** Minimal storage surface so the helpers can be tested without a browser. */
export interface TokenStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

function browserStorage(): TokenStorage | null {
  try {
    return typeof sessionStorage === 'undefined' ? null : sessionStorage;
  } catch {
    return null; // storage blocked
  }
}

// The refresh token lives in sessionStorage (cleared when the tab closes); the short-lived access token is memory only.
export function readRefreshToken(storage: TokenStorage | null = browserStorage()): string | null {
  try {
    return storage?.getItem(REFRESH_TOKEN_KEY) ?? null;
  } catch {
    return null;
  }
}

export function writeRefreshToken(token: string, storage: TokenStorage | null = browserStorage()): void {
  try {
    storage?.setItem(REFRESH_TOKEN_KEY, token);
  } catch {
    // storage unavailable: the user will simply have to sign in again after a reload
  }
}

export function clearRefreshToken(storage: TokenStorage | null = browserStorage()): void {
  try {
    storage?.removeItem(REFRESH_TOKEN_KEY);
  } catch {
    // ignore
  }
}
