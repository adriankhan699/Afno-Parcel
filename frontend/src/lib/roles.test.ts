import { describe, expect, it } from 'vitest';
import {
  accessDeniedMessage,
  canLookupParcels,
  canManageDelivery,
  canManageNetwork,
  canManageStaff,
  canScan,
  canUpdateStatus,
  canUseAgentApp,
  canViewAdmin,
  canViewInventory,
  isDeliveryAgent,
  isSuperAdmin,
  postLoginPath,
} from './auth';

describe('admin area access', () => {
  it.each(['SuperAdmin', 'BranchManager', 'BookingStaff', 'WarehouseStaff', 'Accountant'])('%s can open the admin area, inventory and parcel lookup', (role) => {
    expect(canViewAdmin([role])).toBe(true);
    expect(canViewInventory([role])).toBe(true);
    expect(canLookupParcels([role])).toBe(true);
  });

  it.each(['DeliveryAgent', 'Customer'])('%s cannot open the admin area', (role) => {
    expect(canViewAdmin([role])).toBe(false);
    expect(canViewInventory([role])).toBe(false);
    expect(canLookupParcels([role])).toBe(false);
  });

  it.each(['SuperAdmin', 'BranchManager'])('%s can manage the network and staff', (role) => {
    expect(canManageNetwork([role])).toBe(true);
    expect(canManageStaff([role])).toBe(true);
  });

  it.each(['BookingStaff', 'WarehouseStaff', 'Accountant', 'DeliveryAgent', 'Customer'])('%s cannot manage the network or staff', (role) => {
    expect(canManageNetwork([role])).toBe(false);
    expect(canManageStaff([role])).toBe(false);
  });

  it('only a super admin is a super admin', () => {
    expect(isSuperAdmin(['SuperAdmin'])).toBe(true);
    expect(isSuperAdmin(['BranchManager'])).toBe(false);
    expect(isSuperAdmin(undefined)).toBe(false);
  });

  it('nobody signed in has no admin access', () => {
    expect(canViewAdmin(undefined)).toBe(false);
    expect(canManageNetwork(null)).toBe(false);
    expect(canManageStaff([])).toBe(false);
  });
});

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
  it.each(['SuperAdmin', 'BranchManager', 'BookingStaff', 'WarehouseStaff'])('%s can update status', (role) => {
    expect(canUpdateStatus([role])).toBe(true);
  });

  it.each(['DeliveryAgent', 'Accountant', 'Customer'])('%s cannot update status through the status screen', (role) => {
    expect(canUpdateStatus([role])).toBe(false);
  });

  it('nobody signed in cannot update status', () => {
    expect(canUpdateStatus(undefined)).toBe(false);
  });
});

describe('delivery agent roles', () => {
  it('only delivery agents use the agent app', () => {
    expect(isDeliveryAgent(['DeliveryAgent'])).toBe(true);
    expect(canUseAgentApp(['DeliveryAgent'])).toBe(true);
    for (const role of ['SuperAdmin', 'BranchManager', 'BookingStaff', 'WarehouseStaff', 'Accountant', 'Customer']) {
      expect(canUseAgentApp([role])).toBe(false);
    }
    expect(canUseAgentApp(undefined)).toBe(false);
  });

  it.each(['SuperAdmin', 'BranchManager'])('%s can manage delivery agents', (role) => {
    expect(canManageDelivery([role])).toBe(true);
  });

  it.each(['BookingStaff', 'WarehouseStaff', 'Accountant', 'DeliveryAgent', 'Customer'])('%s cannot manage delivery agents', (role) => {
    expect(canManageDelivery([role])).toBe(false);
  });

  it('an agent has no link to the scanner, the admin area or booking', () => {
    expect(canScan(['DeliveryAgent'])).toBe(false);
    expect(canViewAdmin(['DeliveryAgent'])).toBe(false);
    expect(canUpdateStatus(['DeliveryAgent'])).toBe(false);
  });
});

describe('postLoginPath', () => {
  it('sends delivery agents to the agent home unless they were already heading into the agent app', () => {
    expect(postLoginPath(['DeliveryAgent'], '/')).toBe('/agent');
    expect(postLoginPath(['DeliveryAgent'], '/parcels')).toBe('/agent');
    expect(postLoginPath(['DeliveryAgent'], '/agent/assignments/abc')).toBe('/agent/assignments/abc');
    expect(postLoginPath(['DeliveryAgent'], '/agentx')).toBe('/agent');
  });

  it('leaves everyone else where they were going', () => {
    expect(postLoginPath(['Customer'], '/parcels')).toBe('/parcels');
    expect(postLoginPath(['SuperAdmin'], '/')).toBe('/');
    expect(postLoginPath(undefined, '/book')).toBe('/book');
  });
});

describe('accessDeniedMessage (route guards)', () => {
  it('lets anybody through a route that asks for nothing', () => {
    expect(accessDeniedMessage(['Customer'], {})).toBeNull();
    expect(accessDeniedMessage(undefined, {})).toBeNull();
  });

  it('guards the agent app: agents only', () => {
    expect(accessDeniedMessage(['DeliveryAgent'], { needsAgent: true })).toBeNull();
    expect(accessDeniedMessage(['SuperAdmin'], { needsAgent: true })).toMatch(/delivery agents/i);
    expect(accessDeniedMessage(['Customer'], { needsAgent: true })).not.toBeNull();
    expect(accessDeniedMessage(undefined, { needsAgent: true })).not.toBeNull();
  });

  it('guards delivery management: super admins and branch managers only', () => {
    expect(accessDeniedMessage(['BranchManager'], { needsDeliveryManagement: true })).toBeNull();
    expect(accessDeniedMessage(['SuperAdmin'], { needsDeliveryManagement: true })).toBeNull();
    for (const role of ['BookingStaff', 'WarehouseStaff', 'Accountant', 'DeliveryAgent', 'Customer']) {
      expect(accessDeniedMessage([role], { needsDeliveryManagement: true })).toMatch(/super admin or a branch manager/i);
    }
  });

  it('keeps the older guards working', () => {
    expect(accessDeniedMessage(['Customer'], { needsAdmin: true })).toMatch(/admin area/i);
    expect(accessDeniedMessage(['Accountant'], { needsScan: true })).toMatch(/scan/i);
    expect(accessDeniedMessage(['WarehouseStaff'], { needsBooking: true })).toMatch(/bookings/i);
    expect(accessDeniedMessage(['WarehouseStaff'], { needsAdmin: true, needsScan: true })).toBeNull();
  });
});
