import { describe, expect, it } from 'vitest';
import {
  accessDeniedMessage,
  adminHomePath,
  canChooseFinanceBranch,
  canRecordOfficeCash,
  canViewAdmin,
  canViewFinance,
  canViewNetworkAdmin,
  isFinanceOnly,
} from './auth';

describe('finance access', () => {
  it.each(['SuperAdmin', 'BranchManager', 'Accountant'])('%s can open the finance pages', (role) => {
    expect(canViewFinance([role])).toBe(true);
    expect(accessDeniedMessage([role], { needsFinance: true })).toBeNull();
  });

  it.each(['BookingStaff', 'WarehouseStaff', 'DeliveryAgent', 'Customer'])('%s cannot open the finance pages', (role) => {
    expect(canViewFinance([role])).toBe(false);
    expect(accessDeniedMessage([role], { needsFinance: true })).toMatch(/finance pages/i);
  });

  it('nobody signed in has no finance access', () => {
    expect(canViewFinance(undefined)).toBe(false);
    expect(canViewFinance(null)).toBe(false);
    expect(canViewFinance([])).toBe(false);
    expect(accessDeniedMessage(undefined, { needsFinance: true })).not.toBeNull();
  });

  it('only super admins and accountants choose the branch in the finance reports (a branch manager always gets their own)', () => {
    expect(canChooseFinanceBranch(['SuperAdmin'])).toBe(true);
    expect(canChooseFinanceBranch(['Accountant'])).toBe(true);
    expect(canChooseFinanceBranch(['BranchManager'])).toBe(false);
    expect(canChooseFinanceBranch(['BookingStaff'])).toBe(false);
  });
});

describe('the accountant sees only the finance pages of the admin area', () => {
  it('an accountant may enter the admin area, but not its network pages', () => {
    expect(canViewAdmin(['Accountant'])).toBe(true);
    expect(canViewNetworkAdmin(['Accountant'])).toBe(false);
    expect(isFinanceOnly(['Accountant'])).toBe(true);
    expect(accessDeniedMessage(['Accountant'], { needsNetworkView: true })).toMatch(/finance pages/i);
  });

  it.each(['SuperAdmin', 'BranchManager', 'BookingStaff', 'WarehouseStaff'])('%s keeps the network pages and is not finance-only', (role) => {
    expect(canViewNetworkAdmin([role])).toBe(true);
    expect(isFinanceOnly([role])).toBe(false);
    expect(accessDeniedMessage([role], { needsNetworkView: true })).toBeNull();
  });

  it('an accountant who is also a branch manager gets both', () => {
    expect(isFinanceOnly(['Accountant', 'BranchManager'])).toBe(false);
    expect(canViewNetworkAdmin(['Accountant', 'BranchManager'])).toBe(true);
  });

  it('delivery agents and customers are neither', () => {
    for (const role of ['DeliveryAgent', 'Customer']) {
      expect(isFinanceOnly([role])).toBe(false);
      expect(canViewNetworkAdmin([role])).toBe(false);
    }
  });

  it('the admin area opens on the finance pages for an accountant and on the branches for everyone else', () => {
    expect(adminHomePath(['Accountant'])).toBe('/admin/payments');
    expect(adminHomePath(['BranchManager'])).toBe('/admin/branches');
    expect(adminHomePath(['SuperAdmin'])).toBe('/admin/branches');
    expect(adminHomePath(['BookingStaff'])).toBe('/admin/branches');
    expect(adminHomePath(undefined)).toBe('/admin/branches');
  });
});

describe('cash at the office', () => {
  it.each(['SuperAdmin', 'BranchManager', 'BookingStaff'])('%s can record cash received', (role) => {
    expect(canRecordOfficeCash([role])).toBe(true);
  });

  it.each(['WarehouseStaff', 'Accountant', 'DeliveryAgent', 'Customer'])('%s cannot record cash received', (role) => {
    expect(canRecordOfficeCash([role])).toBe(false);
  });
});
