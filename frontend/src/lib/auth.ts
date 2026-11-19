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

/**
 * Roles that may move a parcel along its journey through the status API (the server decides which statuses each role may
 * set). Delivery agents are deliberately not here: they work only on parcels assigned to them, through the agent API.
 */
const STATUS_UPDATERS: readonly string[] = [ROLES.SuperAdmin, ROLES.BranchManager, ROLES.BookingStaff, ROLES.WarehouseStaff];

/** Roles that may create and edit branches, warehouses and staff assignments (managers only edit their own branch). */
const NETWORK_MANAGERS: readonly string[] = [ROLES.SuperAdmin, ROLES.BranchManager];

/**
 * Roles that may see the finance pages: payments, reports, cash on delivery and settlements. A branch manager only ever sees
 * their own branch (the server enforces that too).
 */
const FINANCE_VIEWERS: readonly string[] = [ROLES.SuperAdmin, ROLES.BranchManager, ROLES.Accountant];

/** Roles that may see the network pages of the admin area (branches, warehouses, inventory, scan log). Not the accountant. */
const NETWORK_VIEWERS: readonly string[] = [ROLES.SuperAdmin, ROLES.BranchManager, ROLES.BookingStaff, ROLES.WarehouseStaff];

/** Roles that may open the notifications pages of the admin area. A branch manager only reads. */
const NOTIFICATION_VIEWERS: readonly string[] = [ROLES.SuperAdmin, ROLES.BranchManager];

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
export const isSuperAdmin = (roles: Roles) => hasAny(roles, [ROLES.SuperAdmin]);
/** Can create/edit branches and warehouses, assign staff and see inactive records. */
export const canManageNetwork = (roles: Roles) => hasAny(roles, NETWORK_MANAGERS);
/** Can open the admin area (branch, warehouse, inventory and scan-log views). */
export const canViewAdmin = (roles: Roles) => hasAny(roles, PARCEL_VIEWERS);
/** Can see warehouse inventory, the waiting lists and the scan log. */
export const canViewInventory = (roles: Roles) => hasAny(roles, PARCEL_VIEWERS);
/** Can look parcels up by scanned code and read their movement history and labels. */
export const canLookupParcels = (roles: Roles) => hasAny(roles, PARCEL_VIEWERS);
/** Can open the staff list and assign people to branches. */
export const canManageStaff = (roles: Roles) => hasAny(roles, NETWORK_MANAGERS);
/** Can update parcel status (which statuses exactly is up to the server: see ParcelResponse.allowedNextStatuses). */
export const canUpdateStatus = (roles: Roles) => hasAny(roles, STATUS_UPDATERS);

/** Delivery agents use the mobile agent app (their own assignments only). */
export const isDeliveryAgent = (roles: Roles) => hasAny(roles, [ROLES.DeliveryAgent]);
export const canUseAgentApp = isDeliveryAgent;
/** Can create and manage delivery agents, assign parcels to them and see their performance (managers only see their own branch). */
export const canManageDelivery = (roles: Roles) => hasAny(roles, NETWORK_MANAGERS);

/** Can open the finance pages (transactions, reports, agent cash and settlements). */
export const canViewFinance = (roles: Roles) => hasAny(roles, FINANCE_VIEWERS);
/** Can open the network pages of the admin area. */
export const canViewNetworkAdmin = (roles: Roles) => hasAny(roles, NETWORK_VIEWERS);
/** An accountant with no other admin role: in the admin area they see the finance pages and nothing else. */
export const isFinanceOnly = (roles: Roles) => canViewFinance(roles) && !canViewNetworkAdmin(roles);
/** Super admins and accountants see every branch in the finance reports; a branch manager is limited to their own. */
export const canChooseFinanceBranch = (roles: Roles) => hasAny(roles, [ROLES.SuperAdmin, ROLES.Accountant]);
/** Counter staff take cash at the office. */
export const canRecordOfficeCash = isCounterStaff;

/** Can open the notifications pages (messages, templates, opt-outs). Not the accountant. A branch manager can only read. */
export const canViewNotifications = (roles: Roles) => hasAny(roles, NOTIFICATION_VIEWERS);
/** Can retry or cancel messages, edit templates, change opt-outs and use the mock provider's test inbox. */
export const canEditNotifications = isSuperAdmin;
/** Can open the mock provider's test inbox (it only exists while a mock provider is active). */
export const canUseMockInbox = isSuperAdmin;
/** Can read the messages of a parcel (everyone who can read parcels; delivery agents cannot). */
export const canViewParcelNotifications = (roles: Roles) => hasAny(roles, PARCEL_VIEWERS);
/** Can record, at the counter, whether a parcel's sender agrees to get updates. */
export const canSetParcelConsent = isCounterStaff;
/** Can choose their own SMS and WhatsApp updates: a signed-in customer. */
export const canManageOwnNotifications = isCustomer;

export const AGENT_HOME = '/agent';

/** Where the admin area opens for this person: the finance pages for an accountant, the branches for everyone else. */
export const adminHomePath = (roles: Roles): string => (isFinanceOnly(roles) ? '/admin/payments' : '/admin/branches');

/**
 * Where to send someone after signing in. A delivery agent has nothing to do on the customer pages, so unless they were sent
 * to sign in from somewhere inside the agent app they land on the agent home.
 */
export function postLoginPath(roles: Roles, returnTo: string): string {
  if (!isDeliveryAgent(roles)) return returnTo;
  return returnTo === AGENT_HOME || returnTo.startsWith(`${AGENT_HOME}/`) ? returnTo : AGENT_HOME;
}

/** What a route asks of the signed-in user. */
export interface AccessRequirements {
  needsBooking?: boolean;
  needsScan?: boolean;
  needsAdmin?: boolean;
  needsAgent?: boolean;
  needsDeliveryManagement?: boolean;
  needsFinance?: boolean;
  needsNetworkView?: boolean;
  needsNotifications?: boolean;
  needsCustomer?: boolean;
}

/** The message to show when the roles do not satisfy the route, or null when access is fine. Route guards use this. */
export function accessDeniedMessage(roles: Roles, needs: AccessRequirements): string | null {
  if (needs.needsBooking && !canBook(roles)) {
    return 'Your account role cannot create bookings. Ask a branch manager if you need access.';
  }
  if (needs.needsScan && !canScan(roles)) {
    return 'Your account role cannot scan parcels. Ask a branch manager if you need access.';
  }
  if (needs.needsAdmin && !canViewAdmin(roles)) {
    return 'Your account role cannot open the admin area. Ask a branch manager if you need access.';
  }
  if (needs.needsAgent && !canUseAgentApp(roles)) {
    return 'This area is for delivery agents. Sign in with a delivery agent account.';
  }
  if (needs.needsDeliveryManagement && !canManageDelivery(roles)) {
    return 'Only a super admin or a branch manager can manage delivery agents. Ask a branch manager if you need access.';
  }
  if (needs.needsFinance && !canViewFinance(roles)) {
    return 'Only accountants, branch managers and super admins can open the finance pages. Ask a branch manager if you need access.';
  }
  if (needs.needsNetworkView && !canViewNetworkAdmin(roles)) {
    return 'Your account role can only open the finance pages of the admin area.';
  }
  if (needs.needsNotifications && !canViewNotifications(roles)) {
    return 'Only super admins and branch managers can open the notification pages. Ask a branch manager if you need access.';
  }
  if (needs.needsCustomer && !canManageOwnNotifications(roles)) {
    return 'This page is for customer accounts. Staff do not have personal notification choices.';
  }
  return null;
}

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
