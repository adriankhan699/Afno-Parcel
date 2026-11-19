import { describe, expect, it } from 'vitest';
import {
  ADMIN_PAGE_SIZE,
  barWidth,
  branchQuery,
  buildAssignRequest,
  buildCreateBranchRequest,
  buildCreateStaffRequest,
  buildCreateWarehouseRequest,
  buildQuery,
  buildUpdateWarehouseRequest,
  emptyBranchFilters,
  emptyBranchForm,
  emptyScanLogFilters,
  emptyStaffAccountForm,
  emptyStaffFilters,
  emptyWaitingFilters,
  emptyWarehouseFilters,
  emptyWarehouseForm,
  formatWaiting,
  inventoryQuery,
  isRangeInvalid,
  roleLabel,
  scanLogQuery,
  staffQuery,
  utilisation,
  utilisationTone,
  validateAssignment,
  validateBranchForm,
  validateStaffAccountForm,
  validateWarehouseForm,
  waitLevel,
  waitTone,
  waitingQuery,
  warehouseQuery,
  warehousesForBranch,
} from './network';

describe('buildQuery', () => {
  it('builds a query string and percent-encodes values', () => {
    expect(buildQuery({ page: 2, search: 'new road' })).toBe('?page=2&search=new+road');
    expect(buildQuery({ search: 'a&b=c' })).toBe('?search=a%26b%3Dc');
  });

  it('leaves out blank values, false flags and missing values, but keeps zero and true', () => {
    expect(buildQuery({ a: '', b: undefined, c: null, d: false, e: 0, f: true })).toBe('?e=0&f=true');
  });

  it('returns an empty string when there is nothing to send', () => {
    expect(buildQuery({})).toBe('');
    expect(buildQuery({ search: '' })).toBe('');
  });
});

describe('filters to queries', () => {
  it('branchQuery trims the search and sends the paging', () => {
    expect(branchQuery({ ...emptyBranchFilters(), search: '  ktm ' }, 3)).toEqual({
      page: 3,
      pageSize: ADMIN_PAGE_SIZE,
      search: 'ktm',
      type: undefined,
      includeInactive: false,
    });
    expect(buildQuery(branchQuery({ search: ' ', type: 'HeadOffice', includeInactive: true }, 1))).toBe(
      `?page=1&pageSize=${ADMIN_PAGE_SIZE}&type=HeadOffice&includeInactive=true`,
    );
  });

  it('warehouseQuery sends the branch and the inactive flag only when set', () => {
    expect(buildQuery(warehouseQuery(emptyWarehouseFilters(), 1))).toBe(`?page=1&pageSize=${ADMIN_PAGE_SIZE}`);
    expect(buildQuery(warehouseQuery({ search: 'a', branchId: 'b1', includeInactive: true }, 2))).toBe(
      `?page=2&pageSize=${ADMIN_PAGE_SIZE}&search=a&branchId=b1&includeInactive=true`,
    );
  });

  it('staffQuery drops the branch and warehouse when only unassigned staff are wanted', () => {
    const filters = { ...emptyStaffFilters(), role: 'WarehouseStaff', branchId: 'b1', warehouseId: 'w1' };

    expect(staffQuery(filters, 1)).toMatchObject({ role: 'WarehouseStaff', branchId: 'b1', warehouseId: 'w1', unassigned: false });
    expect(staffQuery({ ...filters, unassigned: true }, 1)).toMatchObject({ branchId: undefined, warehouseId: undefined, unassigned: true });
    expect(buildQuery(staffQuery({ ...filters, unassigned: true }, 1))).toBe(`?page=1&pageSize=${ADMIN_PAGE_SIZE}&role=WarehouseStaff&unassigned=true`);
  });

  it('waitingQuery sends the kind and place filters', () => {
    expect(buildQuery(waitingQuery({ ...emptyWaitingFilters(), kind: 'Pickup', warehouseId: 'w1' }, 1))).toBe(
      `?page=1&pageSize=${ADMIN_PAGE_SIZE}&kind=Pickup&warehouseId=w1`,
    );
  });

  it('scanLogQuery trims the tracking number and ignores malformed dates', () => {
    const query = scanLogQuery({ ...emptyScanLogFilters(), trackingNumber: ' AFNK7M2Q9XT4B3 ', from: '2025-01-05', to: '05/01/2025', outcome: 'Duplicate' }, 1);

    expect(query).toMatchObject({ trackingNumber: 'AFNK7M2Q9XT4B3', from: '2025-01-05', to: undefined, outcome: 'Duplicate' });
  });

  it('inventoryQuery trims the search', () => {
    expect(inventoryQuery('  afn ', 2)).toEqual({ page: 2, pageSize: ADMIN_PAGE_SIZE, search: 'afn' });
    expect(inventoryQuery('   ', 1).search).toBeUndefined();
  });

  it('isRangeInvalid is true only for a back-to-front range', () => {
    expect(isRangeInvalid('2025-02-01', '2025-01-01')).toBe(true);
    expect(isRangeInvalid('2025-01-01', '2025-01-01')).toBe(false);
    expect(isRangeInvalid('2025-01-01', '')).toBe(false);
    expect(isRangeInvalid('', '')).toBe(false);
  });
});

describe('waiting time', () => {
  it('flags a day or more and two days or more', () => {
    expect(waitLevel(0)).toBe('normal');
    expect(waitLevel(23.9)).toBe('normal');
    expect(waitLevel(24)).toBe('warning');
    expect(waitLevel(47.9)).toBe('warning');
    expect(waitLevel(48)).toBe('late');
    expect(waitLevel(200)).toBe('late');
  });

  it('uses amber for warning and red for late', () => {
    expect(waitTone('warning')).toContain('amber');
    expect(waitTone('late')).toContain('red');
    expect(waitTone('normal')).not.toContain('amber');
  });

  it('formats hours as a short phrase', () => {
    expect(formatWaiting(0.2)).toBe('< 1 h');
    expect(formatWaiting(5)).toBe('5 h');
    expect(formatWaiting(30)).toBe('30 h');
    expect(formatWaiting(48)).toBe('2 d');
    expect(formatWaiting(51)).toBe('2 d 3 h');
    expect(formatWaiting(Number.NaN)).toBe('< 1 h');
  });
});

describe('capacity', () => {
  it('works out utilisation to one decimal, and null without a capacity', () => {
    expect(utilisation(50, 200)).toBe(25);
    expect(utilisation(1, 3)).toBe(33.3);
    expect(utilisation(250, 200)).toBe(125);
    expect(utilisation(5, null)).toBeNull();
    expect(utilisation(5, undefined)).toBeNull();
    expect(utilisation(5, 0)).toBeNull();
  });

  it('clamps the bar width to 0-100', () => {
    expect(barWidth(25)).toBe(25);
    expect(barWidth(125)).toBe(100);
    expect(barWidth(-5)).toBe(0);
    expect(barWidth(null)).toBe(0);
  });

  it('turns amber at 80% and red at 100%', () => {
    expect(utilisationTone(50)).toContain('emerald');
    expect(utilisationTone(80)).toContain('amber');
    expect(utilisationTone(100)).toContain('red');
    expect(utilisationTone(null)).toContain('slate');
  });
});

describe('staff assignment', () => {
  const warehouses = [
    { id: 'w1', branchId: 'b1' },
    { id: 'w2', branchId: 'b2' },
    { id: 'w3', branchId: 'b1' },
  ];

  it('lists only the warehouses of the chosen branch', () => {
    expect(warehousesForBranch(warehouses, 'b1').map((w) => w.id)).toEqual(['w1', 'w3']);
    expect(warehousesForBranch(warehouses, '')).toEqual([]);
    expect(warehousesForBranch(warehouses, 'nope')).toEqual([]);
  });

  it('needs a branch; a warehouse is optional', () => {
    expect(validateAssignment({ branchId: '', warehouseId: '' }, warehouses)).toHaveProperty('branchId');
    expect(validateAssignment({ branchId: 'b1', warehouseId: '' }, warehouses)).toEqual({});
  });

  it('refuses a warehouse from another branch or an unknown one', () => {
    expect(validateAssignment({ branchId: 'b1', warehouseId: 'w2' }, warehouses)).toHaveProperty('warehouseId');
    expect(validateAssignment({ branchId: 'b1', warehouseId: 'zzz' }, warehouses)).toHaveProperty('warehouseId');
    expect(validateAssignment({ branchId: 'b1', warehouseId: 'w3' }, warehouses)).toEqual({});
  });

  it('builds the request with the warehouse only when one was chosen', () => {
    expect(buildAssignRequest({ branchId: 'b1', warehouseId: '' })).toEqual({ branchId: 'b1' });
    expect(buildAssignRequest({ branchId: 'b1', warehouseId: 'w1' })).toEqual({ branchId: 'b1', warehouseId: 'w1' });
  });
});

const address = { province: 'Bagmati', district: 'Kathmandu', municipality: 'Kathmandu', wardNumber: '10', streetOrTole: '', postalCode: '' };

describe('branch form', () => {
  const valid = { ...emptyBranchForm(), ...address, code: 'ktm01', name: 'Kathmandu Hub', phoneNumber: '01-4123456' };

  it('accepts a complete form', () => {
    expect(validateBranchForm(valid, true)).toEqual({});
  });

  it('asks for everything that is missing when creating', () => {
    expect(Object.keys(validateBranchForm(emptyBranchForm(), true)).sort()).toEqual([
      'address.district',
      'address.municipality',
      'address.province',
      'address.wardNumber',
      'code',
      'name',
      'phoneNumber',
    ]);
  });

  it('does not check the code when editing, since it cannot change', () => {
    expect(validateBranchForm({ ...valid, code: '' }, false)).toEqual({});
  });

  it('checks the code format, phone, email, ward and postal code', () => {
    expect(validateBranchForm({ ...valid, code: 'KTM-01' }, true)).toHaveProperty('code');
    expect(validateBranchForm({ ...valid, code: 'A'.repeat(11) }, true)).toHaveProperty('code');
    expect(validateBranchForm({ ...valid, phoneNumber: '12345' }, true)).toHaveProperty('phoneNumber');
    expect(validateBranchForm({ ...valid, email: 'nope' }, true)).toHaveProperty('email');
    expect(validateBranchForm({ ...valid, wardNumber: '0' }, true)).toHaveProperty(['address.wardNumber']);
    expect(validateBranchForm({ ...valid, postalCode: '123' }, true)).toHaveProperty(['address.postalCode']);
  });

  it('builds the create request with an upper-case code and no empty optional fields', () => {
    expect(buildCreateBranchRequest({ ...valid, name: '  Kathmandu Hub ' })).toEqual({
      code: 'KTM01',
      name: 'Kathmandu Hub',
      type: 'Branch',
      address: { province: 'Bagmati', district: 'Kathmandu', municipality: 'Kathmandu', wardNumber: 10 },
      phoneNumber: '01-4123456',
      email: null,
    });
  });
});

describe('warehouse form', () => {
  const valid = { ...emptyWarehouseForm(), ...address, branchId: 'b1', code: 'ktmw1', name: 'Kathmandu Warehouse', phoneNumber: '01-4123456' };

  it('accepts a complete form with no capacity', () => {
    expect(validateWarehouseForm(valid, true)).toEqual({});
  });

  it('needs a branch and code when creating, but not when editing', () => {
    expect(validateWarehouseForm({ ...valid, branchId: '', code: '' }, true)).toHaveProperty('branchId');
    expect(validateWarehouseForm({ ...valid, branchId: '', code: '' }, false)).toEqual({});
  });

  it('accepts a whole-number capacity of at least 1', () => {
    expect(validateWarehouseForm({ ...valid, capacity: '500' }, true)).toEqual({});
    expect(validateWarehouseForm({ ...valid, capacity: '0' }, true)).toHaveProperty('capacity');
    expect(validateWarehouseForm({ ...valid, capacity: '2.5' }, true)).toHaveProperty('capacity');
    expect(validateWarehouseForm({ ...valid, capacity: 'lots' }, true)).toHaveProperty('capacity');
  });

  it('builds requests with the capacity as a number, or null when blank', () => {
    expect(buildCreateWarehouseRequest({ ...valid, capacity: '500' })).toMatchObject({ branchId: 'b1', code: 'KTMW1', capacityParcels: 500 });
    expect(buildCreateWarehouseRequest(valid).capacityParcels).toBeNull();
    expect(buildUpdateWarehouseRequest({ ...valid, capacity: ' 40 ' })).toEqual({
      name: 'Kathmandu Warehouse',
      address: { province: 'Bagmati', district: 'Kathmandu', municipality: 'Kathmandu', wardNumber: 10 },
      phoneNumber: '01-4123456',
      capacityParcels: 40,
    });
  });
});

describe('staff account form', () => {
  const valid = { ...emptyStaffAccountForm(), fullName: 'Sita Rai', email: 'sita@example.com', role: 'WarehouseStaff', password: 'Str0ngPassw0rd' };

  it('accepts a complete form without a phone number', () => {
    expect(validateStaffAccountForm(valid)).toEqual({});
  });

  it('checks the name, email, role and phone', () => {
    expect(Object.keys(validateStaffAccountForm(emptyStaffAccountForm())).sort()).toEqual(['email', 'fullName', 'password', 'role']);
    expect(validateStaffAccountForm({ ...valid, email: 'sita' })).toHaveProperty('email');
    expect(validateStaffAccountForm({ ...valid, phoneNumber: '123' })).toHaveProperty('phoneNumber');
    expect(validateStaffAccountForm({ ...valid, phoneNumber: '9841234567' })).toEqual({});
  });

  it('requires 10 characters with upper case, lower case and a digit', () => {
    for (const password of ['Short1a', 'alllowercase1', 'ALLUPPERCASE1', 'NoDigitsHereAtAll']) {
      expect(validateStaffAccountForm({ ...valid, password })).toHaveProperty('password');
    }
  });

  it('builds the request, trimming text and sending a blank phone as null', () => {
    expect(buildCreateStaffRequest({ ...valid, fullName: ' Sita Rai ', email: ' sita@example.com ' })).toEqual({
      fullName: 'Sita Rai',
      email: 'sita@example.com',
      phoneNumber: null,
      password: 'Str0ngPassw0rd',
      role: 'WarehouseStaff',
    });
  });

  it('shows role names with spaces', () => {
    expect(roleLabel('WarehouseStaff')).toBe('Warehouse Staff');
    expect(roleLabel('SuperAdmin')).toBe('Super Admin');
    expect(roleLabel('Accountant')).toBe('Accountant');
  });
});
