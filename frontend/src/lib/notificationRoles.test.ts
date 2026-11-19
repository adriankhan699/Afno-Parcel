import { describe, expect, it } from 'vitest';
import {
  accessDeniedMessage,
  canEditNotifications,
  canManageOwnNotifications,
  canSetParcelConsent,
  canUseMockInbox,
  canViewAdmin,
  canViewNotifications,
  canViewParcelNotifications,
} from './auth';

describe('the notifications admin pages', () => {
  it.each(['SuperAdmin', 'BranchManager'])('%s can open them', (role) => {
    expect(canViewNotifications([role])).toBe(true);
    expect(accessDeniedMessage([role], { needsNotifications: true })).toBeNull();
  });

  // The accountant may enter the admin area (finance) but must not get the notifications pages.
  it.each(['Accountant', 'BookingStaff', 'WarehouseStaff', 'DeliveryAgent', 'Customer'])('%s cannot open them', (role) => {
    expect(canViewNotifications([role])).toBe(false);
    expect(accessDeniedMessage([role], { needsNotifications: true })).toMatch(/notification pages/i);
  });

  it('the accountant can open the admin area but not the notification pages', () => {
    expect(canViewAdmin(['Accountant'])).toBe(true);
    expect(canViewNotifications(['Accountant'])).toBe(false);
  });

  it('an accountant who is also a branch manager gets them', () => {
    expect(canViewNotifications(['Accountant', 'BranchManager'])).toBe(true);
  });

  it('nobody signed in gets them', () => {
    expect(canViewNotifications(undefined)).toBe(false);
    expect(canViewNotifications(null)).toBe(false);
    expect(canViewNotifications([])).toBe(false);
    expect(accessDeniedMessage(undefined, { needsNotifications: true })).not.toBeNull();
  });
});

describe('who may change things', () => {
  it('only a super admin edits, retries, cancels and uses the test inbox; a branch manager only reads', () => {
    expect(canEditNotifications(['SuperAdmin'])).toBe(true);
    expect(canUseMockInbox(['SuperAdmin'])).toBe(true);
    for (const role of ['BranchManager', 'BookingStaff', 'WarehouseStaff', 'Accountant', 'DeliveryAgent', 'Customer']) {
      expect(canEditNotifications([role])).toBe(false);
      expect(canUseMockInbox([role])).toBe(false);
    }
  });
});

describe("a parcel's notifications", () => {
  it.each(['SuperAdmin', 'BranchManager', 'BookingStaff', 'WarehouseStaff', 'Accountant'])('%s can read them', (role) => {
    expect(canViewParcelNotifications([role])).toBe(true);
  });

  it.each(['DeliveryAgent', 'Customer'])('%s cannot', (role) => {
    expect(canViewParcelNotifications([role])).toBe(false);
  });

  it.each(['SuperAdmin', 'BranchManager', 'BookingStaff'])('%s can record the sender consent at the counter', (role) => {
    expect(canSetParcelConsent([role])).toBe(true);
  });

  it.each(['WarehouseStaff', 'Accountant', 'DeliveryAgent', 'Customer'])('%s cannot record the sender consent', (role) => {
    expect(canSetParcelConsent([role])).toBe(false);
  });
});

describe('the customer preferences page', () => {
  it('is for customers only', () => {
    expect(canManageOwnNotifications(['Customer'])).toBe(true);
    expect(accessDeniedMessage(['Customer'], { needsCustomer: true })).toBeNull();
    for (const role of ['SuperAdmin', 'BranchManager', 'BookingStaff', 'WarehouseStaff', 'Accountant', 'DeliveryAgent']) {
      expect(canManageOwnNotifications([role])).toBe(false);
      expect(accessDeniedMessage([role], { needsCustomer: true })).toMatch(/customer accounts/i);
    }
  });
});
