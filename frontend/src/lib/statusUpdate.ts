import type { UpdateParcelStatusRequest } from '../types/api';
import { canUpdateStatus } from './auth';

/**
 * The statuses to offer on the status screen: what the server says the parcel may move to, but only to roles that use the
 * status API at all. Delivery agents are offered none: they deliver through the agent app, with proof and cash handling.
 */
export function statusesOfferedTo(roles: readonly string[] | null | undefined, allowed: readonly string[]): string[] {
  return canUpdateStatus(roles) ? [...allowed] : [];
}

// These rules mirror NepalParcel.Domain.Parcels.ParcelStatusRules so the form can guide staff. The server enforces them.

export type LocationRequirement = 'optional' | 'branch' | 'warehouse' | 'either';
export type LocationKind = '' | 'branch' | 'warehouse';

export const REMARKS_MAX_LENGTH = 500;

const LOCATION_REQUIREMENTS: Record<string, LocationRequirement> = {
  Shipped: 'branch',
  ArrivedAtWarehouse: 'warehouse',
  OutForDelivery: 'either',
  Returned: 'either',
};

const REMARKS_REQUIRED: readonly string[] = ['DeliveryFailed', 'Returned'];

export const locationRequirement = (status: string): LocationRequirement => LOCATION_REQUIREMENTS[status] ?? 'optional';

export const remarksRequired = (status: string): boolean => REMARKS_REQUIRED.includes(status);

export interface StatusForm {
  status: string;
  remarks: string;
  /** Only used when the status lets the user choose (an optional location, or a branch-or-warehouse one). */
  locationKind: LocationKind;
  branchId: string;
  warehouseId: string;
  /** Only for InTransit: the warehouse the parcel is travelling to. Empty means not specified. */
  toWarehouseId: string;
}

export const emptyStatusForm = (status = ''): StatusForm => ({
  status,
  remarks: '',
  locationKind: '',
  branchId: '',
  warehouseId: '',
  toWarehouseId: '',
});

/** A parcel that goes in transit can say which warehouse it is heading to, so that warehouse sees it as inbound. */
export const usesDestinationWarehouse = (status: string): boolean => status === 'InTransit';

/** Which kind of place the form is recording: fixed by the status, or the user's choice. '' means none. */
export function resolveLocationKind(status: string, chosen: LocationKind): LocationKind {
  switch (locationRequirement(status)) {
    case 'branch':
      return 'branch';
    case 'warehouse':
      return 'warehouse';
    case 'either':
      return chosen === '' ? 'branch' : chosen;
    default:
      return chosen;
  }
}

/** Field errors keyed like the API's property names (status, remarks, branchId, warehouseId). */
export function validateStatusForm(form: StatusForm): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!form.status) {
    errors.status = 'Choose the new status.';
    return errors;
  }

  const remarks = form.remarks.trim();
  if (remarksRequired(form.status) && !remarks) {
    errors.remarks = 'Remarks are required for this status.';
  } else if (remarks.length > REMARKS_MAX_LENGTH) {
    errors.remarks = `Remarks can be at most ${REMARKS_MAX_LENGTH} characters.`;
  }

  const kind = resolveLocationKind(form.status, form.locationKind);
  if (kind === 'branch' && !form.branchId) {
    errors.branchId = 'Choose the branch.';
  }
  if (kind === 'warehouse' && !form.warehouseId) {
    errors.warehouseId = 'Choose the warehouse.';
  }
  if (
    usesDestinationWarehouse(form.status) &&
    form.toWarehouseId &&
    kind === 'warehouse' &&
    form.toWarehouseId === form.warehouseId
  ) {
    errors.toWarehouseId = 'The destination must be a different warehouse from where it is recorded.';
  }
  return errors;
}

/**
 * The request body. It carries at most one of branchId and warehouseId (the server rejects both), and only the one that
 * matches the place being recorded, even if the other select still holds an old value. The destination warehouse
 * (toWarehouseId) is only sent when going in transit.
 */
export function buildStatusRequest(form: StatusForm): UpdateParcelStatusRequest {
  const request: UpdateParcelStatusRequest = { status: form.status };
  const remarks = form.remarks.trim();
  if (remarks) request.remarks = remarks;

  const kind = resolveLocationKind(form.status, form.locationKind);
  if (kind === 'branch' && form.branchId) request.branchId = form.branchId;
  if (kind === 'warehouse' && form.warehouseId) request.warehouseId = form.warehouseId;
  if (usesDestinationWarehouse(form.status) && form.toWarehouseId) request.toWarehouseId = form.toWarehouseId;
  return request;
}
