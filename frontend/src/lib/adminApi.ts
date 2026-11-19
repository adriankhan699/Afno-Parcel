/**
 * Paths of the network and staff endpoints in one place, so a route change on the server is a one-line fix here.
 * All paths are relative to the API origin and already carry the /api/v1 prefix.
 */
export const adminApi = {
  branches: '/api/v1/branches',
  warehouses: '/api/v1/warehouses',
  serviceAreas: '/api/v1/service-areas',
  warehouseInventory: (warehouseId: string) => `/api/v1/warehouses/${warehouseId}/inventory`,
  waitingParcels: '/api/v1/inventory/waiting',
  staff: '/api/v1/staff',
  staffAssignment: (userId: string) => `/api/v1/staff/${userId}/assignment`,
  myAssignment: '/api/v1/staff/me/assignment',
  createStaffUser: '/api/v1/admin/users',
  scan: '/api/v1/scans',
  scanBatch: '/api/v1/scans/batch',
  scanLog: '/api/v1/scans',
  parcelMovements: (parcelId: string) => `/api/v1/parcels/${parcelId}/movements`,
  parcelLabel: (parcelId: string) => `/api/v1/parcels/${parcelId}/label`,
  parcelLookup: '/api/v1/parcels/lookup',
} as const;
