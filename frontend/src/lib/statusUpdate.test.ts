import { describe, expect, it } from 'vitest';
import {
  REMARKS_MAX_LENGTH,
  buildStatusRequest,
  emptyStatusForm,
  locationRequirement,
  remarksRequired,
  resolveLocationKind,
  statusesOfferedTo,
  usesDestinationWarehouse,
  validateStatusForm,
  type StatusForm,
} from './statusUpdate';

const form = (patch: Partial<StatusForm>): StatusForm => ({ ...emptyStatusForm('PickedUp'), ...patch });

describe('statusesOfferedTo', () => {
  const allowed = ['PickedUp', 'OutForDelivery', 'Delivered', 'DeliveryFailed'];

  it('offers delivery agents no statuses at all: they deliver through the agent app', () => {
    expect(statusesOfferedTo(['DeliveryAgent'], allowed)).toEqual([]);
    expect(statusesOfferedTo(['DeliveryAgent'], ['PickedUp'])).toEqual([]);
  });

  it.each(['SuperAdmin', 'BranchManager', 'BookingStaff', 'WarehouseStaff'])('%s is offered what the server allows', (role) => {
    expect(statusesOfferedTo([role], allowed)).toEqual(allowed);
  });

  it('offers nothing to customers, accountants and signed-out visitors', () => {
    expect(statusesOfferedTo(['Customer'], allowed)).toEqual([]);
    expect(statusesOfferedTo(['Accountant'], allowed)).toEqual([]);
    expect(statusesOfferedTo(undefined, allowed)).toEqual([]);
    expect(statusesOfferedTo(null, allowed)).toEqual([]);
  });

  it('returns a copy, so callers cannot change the parcel data', () => {
    const result = statusesOfferedTo(['SuperAdmin'], allowed);
    expect(result).not.toBe(allowed);
  });
});

describe('status rules (mirror of the server rules)', () => {
  it.each([
    ['Shipped', 'branch'],
    ['ArrivedAtWarehouse', 'warehouse'],
    ['OutForDelivery', 'either'],
    ['Returned', 'either'],
    ['PickedUp', 'optional'],
    ['InTransit', 'optional'],
    ['Delivered', 'optional'],
    ['DeliveryFailed', 'optional'],
  ])('%s needs location: %s', (status, expected) => {
    expect(locationRequirement(status)).toBe(expected);
  });

  it('only delivery failures and returns need remarks', () => {
    expect(remarksRequired('DeliveryFailed')).toBe(true);
    expect(remarksRequired('Returned')).toBe(true);
    for (const status of ['PickedUp', 'Shipped', 'InTransit', 'ArrivedAtWarehouse', 'OutForDelivery', 'Delivered']) {
      expect(remarksRequired(status)).toBe(false);
    }
  });

  it('fixes the kind of place for branch-only and warehouse-only statuses, whatever was chosen', () => {
    expect(resolveLocationKind('Shipped', 'warehouse')).toBe('branch');
    expect(resolveLocationKind('ArrivedAtWarehouse', 'branch')).toBe('warehouse');
  });

  it('lets the user choose when either place is fine, defaulting to a branch', () => {
    expect(resolveLocationKind('OutForDelivery', '')).toBe('branch');
    expect(resolveLocationKind('OutForDelivery', 'warehouse')).toBe('warehouse');
    expect(resolveLocationKind('Returned', 'warehouse')).toBe('warehouse');
  });

  it('keeps an optional location empty until one is chosen', () => {
    expect(resolveLocationKind('PickedUp', '')).toBe('');
    expect(resolveLocationKind('PickedUp', 'branch')).toBe('branch');
  });
});

describe('validateStatusForm', () => {
  it('needs a status', () => {
    expect(validateStatusForm(form({ status: '' }))).toHaveProperty('status');
  });

  it('accepts a simple status with no location and no remarks', () => {
    expect(validateStatusForm(form({ status: 'InTransit' }))).toEqual({});
  });

  it('asks for a branch when shipping', () => {
    expect(validateStatusForm(form({ status: 'Shipped' }))).toHaveProperty('branchId');
    expect(validateStatusForm(form({ status: 'Shipped', branchId: 'b1' }))).toEqual({});
  });

  it('asks for a warehouse when a parcel arrives at one', () => {
    expect(validateStatusForm(form({ status: 'ArrivedAtWarehouse' }))).toHaveProperty('warehouseId');
    expect(validateStatusForm(form({ status: 'ArrivedAtWarehouse', warehouseId: 'w1' }))).toEqual({});
  });

  it('asks for the chosen place when going out for delivery', () => {
    expect(validateStatusForm(form({ status: 'OutForDelivery' }))).toHaveProperty('branchId');
    expect(validateStatusForm(form({ status: 'OutForDelivery', locationKind: 'warehouse' }))).toHaveProperty('warehouseId');
    expect(validateStatusForm(form({ status: 'OutForDelivery', locationKind: 'warehouse', warehouseId: 'w1' }))).toEqual({});
  });

  it('does not ask for a place that was not chosen for an optional location', () => {
    expect(validateStatusForm(form({ status: 'PickedUp', branchId: 'ignored' }))).toEqual({});
    expect(validateStatusForm(form({ status: 'PickedUp', locationKind: 'branch' }))).toHaveProperty('branchId');
  });

  it('requires remarks for a failed delivery, ignoring blanks', () => {
    expect(validateStatusForm(form({ status: 'DeliveryFailed', remarks: '   ' }))).toHaveProperty('remarks');
    expect(validateStatusForm(form({ status: 'DeliveryFailed', remarks: 'Receiver not home' }))).toEqual({});
  });

  it('requires both remarks and a place to return a parcel', () => {
    expect(Object.keys(validateStatusForm(form({ status: 'Returned' }))).sort()).toEqual(['branchId', 'remarks']);
  });

  it('limits remarks to 500 characters', () => {
    expect(validateStatusForm(form({ status: 'InTransit', remarks: 'x'.repeat(REMARKS_MAX_LENGTH) }))).toEqual({});
    expect(validateStatusForm(form({ status: 'InTransit', remarks: 'x'.repeat(REMARKS_MAX_LENGTH + 1) }))).toHaveProperty('remarks');
  });
});

describe('destination warehouse for InTransit', () => {
  it('is offered only when going in transit', () => {
    expect(usesDestinationWarehouse('InTransit')).toBe(true);
    for (const status of ['PickedUp', 'Shipped', 'ArrivedAtWarehouse', 'OutForDelivery', 'Delivered', 'DeliveryFailed', 'Returned']) {
      expect(usesDestinationWarehouse(status)).toBe(false);
    }
  });

  it('is sent for InTransit and left out when empty or for other statuses', () => {
    expect(buildStatusRequest(form({ status: 'InTransit', toWarehouseId: 'w2' }))).toEqual({ status: 'InTransit', toWarehouseId: 'w2' });
    expect('toWarehouseId' in buildStatusRequest(form({ status: 'InTransit', toWarehouseId: '' }))).toBe(false);
    expect('toWarehouseId' in buildStatusRequest(form({ status: 'Shipped', branchId: 'b1', toWarehouseId: 'w2' }))).toBe(false);
  });

  it('must differ from the warehouse the movement is recorded at', () => {
    const base = { status: 'InTransit', locationKind: 'warehouse' as const, warehouseId: 'w1' };

    expect(validateStatusForm(form({ ...base, toWarehouseId: 'w1' }))).toHaveProperty('toWarehouseId');
    expect(validateStatusForm(form({ ...base, toWarehouseId: 'w2' }))).toEqual({});
    expect(validateStatusForm(form({ ...base, toWarehouseId: '' }))).toEqual({});
  });

  it('is not compared with a warehouse that is not being recorded', () => {
    expect(validateStatusForm(form({ status: 'InTransit', locationKind: 'branch', branchId: 'b1', warehouseId: 'w1', toWarehouseId: 'w1' }))).toEqual({});
  });
});

describe('buildStatusRequest', () => {
  it('sends only the status when nothing else was given', () => {
    expect(buildStatusRequest(form({ status: 'InTransit' }))).toEqual({ status: 'InTransit' });
  });

  it('trims remarks and leaves blank ones out', () => {
    expect(buildStatusRequest(form({ status: 'InTransit', remarks: '  on the highway  ' }))).toEqual({
      status: 'InTransit',
      remarks: 'on the highway',
    });
    expect('remarks' in buildStatusRequest(form({ status: 'InTransit', remarks: '   ' }))).toBe(false);
  });

  it('sends the branch for a branch status', () => {
    expect(buildStatusRequest(form({ status: 'Shipped', branchId: 'b1' }))).toEqual({ status: 'Shipped', branchId: 'b1' });
  });

  it('sends the warehouse for a warehouse status', () => {
    expect(buildStatusRequest(form({ status: 'ArrivedAtWarehouse', warehouseId: 'w1' }))).toEqual({
      status: 'ArrivedAtWarehouse',
      warehouseId: 'w1',
    });
  });

  it('never sends both a branch and a warehouse, even when both selects hold a value', () => {
    for (const status of ['PickedUp', 'Shipped', 'InTransit', 'ArrivedAtWarehouse', 'OutForDelivery', 'Delivered', 'DeliveryFailed', 'Returned']) {
      for (const locationKind of ['', 'branch', 'warehouse'] as const) {
        const request = buildStatusRequest(form({ status, locationKind, branchId: 'b1', warehouseId: 'w1', remarks: 'note' }));

        expect('branchId' in request && 'warehouseId' in request).toBe(false);
      }
    }
  });

  it('follows the choice for statuses that accept either place', () => {
    expect(buildStatusRequest(form({ status: 'OutForDelivery', locationKind: 'warehouse', branchId: 'b1', warehouseId: 'w1' }))).toEqual({
      status: 'OutForDelivery',
      warehouseId: 'w1',
    });
    expect(buildStatusRequest(form({ status: 'OutForDelivery', locationKind: 'branch', branchId: 'b1', warehouseId: 'w1' }))).toEqual({
      status: 'OutForDelivery',
      branchId: 'b1',
    });
  });

  it('leaves out a place for an optional status when none was chosen', () => {
    expect(buildStatusRequest(form({ status: 'Delivered', branchId: 'b1', warehouseId: 'w1' }))).toEqual({ status: 'Delivered' });
  });
});
