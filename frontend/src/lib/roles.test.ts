import { describe, expect, it } from 'vitest';
import { canScan, canUpdateStatus } from './auth';

describe('canScan', () => {
  it.each(['SuperAdmin', 'BranchManager', 'WarehouseStaff'])('%s can scan', (role) => {
    expect(canScan([role])).toBe(true);
  });

  it.each(['BookingStaff', 'DeliveryAgent', 'Accountant', 'Customer'])('%s cannot scan', (role) => {
    expect(canScan([role])).toBe(false);
  });

  it('nobody signed in cannot scan', () => {
    expect(canScan(undefined)).toBe(false);
    expect(canScan(null)).toBe(false);
    expect(canScan([])).toBe(false);
  });

  it('any one matching role is enough', () => {
    expect(canScan(['Customer', 'WarehouseStaff'])).toBe(true);
  });
});

describe('canUpdateStatus', () => {
  it.each(['SuperAdmin', 'BranchManager', 'BookingStaff', 'WarehouseStaff', 'DeliveryAgent'])('%s can update status', (role) => {
    expect(canUpdateStatus([role])).toBe(true);
  });

  it.each(['Accountant', 'Customer'])('%s cannot update status', (role) => {
    expect(canUpdateStatus([role])).toBe(false);
  });

  it('nobody signed in cannot update status', () => {
    expect(canUpdateStatus(undefined)).toBe(false);
  });
});
