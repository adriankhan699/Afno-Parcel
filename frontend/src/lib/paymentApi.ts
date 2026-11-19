/**
 * Paths of the payment endpoints in one place, so a route change on the server is a one-line fix here.
 * All paths are relative to the API origin and already carry the /api/v1 prefix.
 */
export const paymentApi = {
  // Paying online
  initiate: '/api/v1/payments/initiate',
  status: (reference: string) => `/api/v1/payments/status/${encodeURIComponent(reference)}`,
  callback: (provider: string) => `/api/v1/payments/callback/${encodeURIComponent(provider)}`,

  // The simulated provider (exists only while the server runs in test mode; a 404 means it is off)
  testGateway: (reference: string) => `/api/v1/payments/test-gateway/${encodeURIComponent(reference)}`,
  testGatewayComplete: (reference: string) => `/api/v1/payments/test-gateway/${encodeURIComponent(reference)}/complete`,

  // Customers and the counter
  forParcel: (parcelId: string) => `/api/v1/payments/parcel/${encodeURIComponent(parcelId)}`,
  receipt: (paymentId: string) => `/api/v1/payments/${encodeURIComponent(paymentId)}/receipt`,
  officeCash: '/api/v1/payments/office-cash',

  // Finance (accountants, super admins, branch managers for their own branch)
  transactions: '/api/v1/payments/transactions',
  reportSummary: '/api/v1/payments/reports/summary',
  agentCash: '/api/v1/payments/agent-cash',
  settlements: '/api/v1/payments/settlements',
  settlement: (id: string) => `/api/v1/payments/settlements/${encodeURIComponent(id)}`,
  settlementVerify: (id: string) => `/api/v1/payments/settlements/${encodeURIComponent(id)}/verify`,
  settlementResolve: (id: string) => `/api/v1/payments/settlements/${encodeURIComponent(id)}/resolve`,

  // The signed-in delivery agent
  myCash: '/api/v1/agent/cash',
  mySettlements: '/api/v1/agent/settlements',
} as const;
