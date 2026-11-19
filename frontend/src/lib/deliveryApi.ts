/**
 * Paths of the delivery endpoints in one place, so a route change on the server is a one-line fix here.
 * All paths are relative to the API origin and already carry the /api/v1 prefix.
 */
export const deliveryApi = {
  // Managers (SuperAdmin, BranchManager)
  agents: '/api/v1/delivery-agents',
  agent: (id: string) => `/api/v1/delivery-agents/${id}`,
  agentActivate: (id: string) => `/api/v1/delivery-agents/${id}/activate`,
  agentDeactivate: (id: string) => `/api/v1/delivery-agents/${id}/deactivate`,
  agentAvailability: (id: string) => `/api/v1/delivery-agents/${id}/availability`,
  performance: '/api/v1/delivery-agents/performance',
  ready: '/api/v1/delivery/ready',
  assignments: '/api/v1/delivery/assignments',
  assignmentCancel: (id: string) => `/api/v1/delivery/assignments/${id}/cancel`,
  proofFile: (proofId: string) => `/api/v1/delivery/proofs/${proofId}/file`,

  // The signed-in agent
  me: '/api/v1/agent/me',
  myAvailability: '/api/v1/agent/me/availability',
  myAssignments: '/api/v1/agent/assignments',
  myAssignment: (id: string) => `/api/v1/agent/assignments/${id}`,
  accept: (id: string) => `/api/v1/agent/assignments/${id}/accept`,
  start: (id: string) => `/api/v1/agent/assignments/${id}/start`,
  fail: (id: string) => `/api/v1/agent/assignments/${id}/fail`,
  deliveryCode: (id: string) => `/api/v1/agent/assignments/${id}/delivery-code`,
  proofOtp: (id: string) => `/api/v1/agent/assignments/${id}/proof/otp`,
  proofFileUpload: (id: string) => `/api/v1/agent/assignments/${id}/proof/file`,
  collectCod: (id: string) => `/api/v1/agent/assignments/${id}/collect-cod`,
  complete: (id: string) => `/api/v1/agent/assignments/${id}/complete`,
  returnToWarehouse: (id: string) => `/api/v1/agent/assignments/${id}/return-to-warehouse`,
} as const;
